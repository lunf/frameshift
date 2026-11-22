using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace FrameShift.Core;

public sealed record Photo(string Id, string ImageUrl, string Photographer, string? ProfileUrl,
    string? PhotoUrl, string? DownloadLocation);

public interface IPhotoSource
{
    Task<Photo> FetchAsync(string topic, int width, int height, CancellationToken token);
    Task DownloadAsync(Photo photo, string destination, CancellationToken token);
}

public sealed class PhotoSourceException(string message, bool isTransient = false, TimeSpan? retryAfter = null) : Exception(message)
{
    public bool IsTransient { get; } = isTransient;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

public sealed class UnsplashSource(HttpClient api, HttpClient images, Func<string?> accessKey) : IPhotoSource
{
    public async Task<Photo> FetchAsync(string topic, int width, int height, CancellationToken token)
    {
        var key = accessKey()?.Trim();
        if (string.IsNullOrEmpty(key))
            throw new PhotoSourceException("Add UNSPLASH_ACCESS_KEY to .env.local and restart, enter a key using API setup, or choose a local photo folder.");
        var url = "https://api.unsplash.com/photos/random?orientation=landscape";
        if (!string.IsNullOrWhiteSpace(topic)) url += "&query=" + Uri.EscapeDataString(topic.Trim());
        using var response = await SendApiAsync(url, key, token);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        var root = json.RootElement;
        try
        {
            var raw = RequireHttps(root.GetProperty("urls").GetProperty("raw").GetString(), "images.unsplash.com");
            var imageUrl = AddQuery(raw, $"w={width}&h={height}&fit=crop&q=85&fm=jpg");
            var links = root.GetProperty("links");
            return new Photo(root.GetProperty("id").GetString() ?? "photo", imageUrl,
                root.GetProperty("user").GetProperty("name").GetString() ?? "Unknown photographer",
                Attribution(root.GetProperty("user").GetProperty("links").GetProperty("html").GetString()),
                Attribution(links.GetProperty("html").GetString()),
                RequireHttps(links.GetProperty("download_location").GetString(), "api.unsplash.com"));
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or UriFormatException)
        { throw new PhotoSourceException("Unsplash returned incomplete photo information. Try again later."); }
    }

    public async Task DownloadAsync(Photo photo, string destination, CancellationToken token)
    {
        // Register each wallpaper download before requesting the original CDN URL.
        using (var tracked = await SendApiAsync(RequireHttps(photo.DownloadLocation, "api.unsplash.com"),
                   accessKey()?.Trim() ?? "", token)) { }
        using var response = await images.GetAsync(photo.ImageUrl, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 50 * 1024 * 1024)
            throw new PhotoSourceException("The photo is too large to download (50 MB maximum).");
        await using var input = await response.Content.ReadAsStreamAsync(token);
        await using var output = File.Create(destination);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while (true)
        {
            try { read = await input.ReadAsync(buffer, token); }
            catch (IOException ex) { throw new HttpRequestException("The photo download was interrupted.", ex); }
            if (read == 0) break;
            total += read;
            if (total > 50 * 1024 * 1024) throw new PhotoSourceException("The photo exceeds the 50 MB download limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), token);
        }
    }

    private async Task<HttpResponseMessage> SendApiAsync(string url, string key, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Client-ID", key);
        request.Headers.Add("Accept-Version", "v1");
        var response = await api.SendAsync(request, token);
        if (response.IsSuccessStatusCode) return response;
        var message = response.StatusCode switch
        {
            HttpStatusCode.NotFound => "No photos matched that topic. Your previous topic and wallpaper have been kept.",
            HttpStatusCode.Unauthorized => "Unsplash rejected the access key. Check it in API setup.",
            HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests => "Unsplash denied this request or its rate limit was reached. Wait before trying again and check your application's permissions.",
            _ => $"Unsplash is unavailable (HTTP {(int)response.StatusCode}). Try again later."
        };
        var rateLimited = response.StatusCode == HttpStatusCode.TooManyRequests ||
            (response.StatusCode == HttpStatusCode.Forbidden && response.Headers.TryGetValues("X-Ratelimit-Remaining", out var remaining) && remaining.Contains("0"));
        var transient = rateLimited || response.StatusCode == HttpStatusCode.RequestTimeout || (int)response.StatusCode >= 500;
        TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta;
        if (retryAfter is null && response.Headers.RetryAfter?.Date is { } date)
            retryAfter = date - DateTimeOffset.UtcNow;
        if (rateLimited && (retryAfter is null || retryAfter < TimeSpan.FromMinutes(1)))
            retryAfter = TimeSpan.FromHours(1);
        response.Dispose();
        throw new PhotoSourceException(message, transient, retryAfter);
    }

    public static string AddQuery(string url, string query) => url + (url.Contains('?') ? "&" : "?") + query;
    private static string Attribution(string? url) => AddQuery(RequireHttps(url, "unsplash.com"), "utm_source=frame_shift&utm_medium=referral");
    private static string RequireHttps(string? url, string host)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase) || !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort)
            throw new PhotoSourceException("Unsplash returned an unexpected photo URL.");
        return uri.AbsoluteUri;
    }
}

public sealed class LocalFolderSource(string folder) : IPhotoSource
{
    public Task<Photo> FetchAsync(string topic, int width, int height, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Directory.Exists(folder)) throw new PhotoSourceException("The local photo folder no longer exists. Choose another folder.");
        var files = Directory.EnumerateFiles(folder)
            .Where(p => new[] { ".jpg", ".jpeg", ".png", ".bmp" }.Contains(Path.GetExtension(p).ToLowerInvariant()))
            .Where(p => string.IsNullOrWhiteSpace(topic) || Path.GetFileNameWithoutExtension(p).Contains(topic, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (files.Length == 0) throw new PhotoSourceException("No images matched that topic in the folder. Try an empty topic for all images. Your previous settings have been kept.");
        var path = files[Random.Shared.Next(files.Length)];
        return Task.FromResult(new Photo(Path.GetFileName(path), path, "Local photo", null, null, null));
    }

    public async Task DownloadAsync(Photo photo, string destination, CancellationToken token)
    {
        await using var input = File.OpenRead(photo.ImageUrl);
        await using var output = File.Create(destination);
        await input.CopyToAsync(output, token);
    }
}
