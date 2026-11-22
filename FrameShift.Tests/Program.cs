using System.Net;
using System.Text;
using FrameShift.Core;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Unsplash query, resize, attribution, tracking, and isolated credentials", UnsplashContract),
    ("HTTP failures preserve settings and current wallpaper", FailedTopic),
    ("Downloads and wallpaper failures remove incomplete files", FailedDownloads),
    ("Only ten wallpapers remain after repeated refreshes", CacheRetention),
    ("Concurrent refreshes do not overlap", ConcurrentRefresh),
    ("Settings survive restart and recover from corrupt JSON", SettingsRoundTrip),
    ("Local source filters filenames and leaves originals intact", LocalPhotos),
    ("Missing API key causes no network calls", MissingKey),
    ("Local key file loading, precedence, and safe format errors", LocalKeyConfiguration),
    ("Refresh interval persistence, migration, validation, and failed saves", RefreshPreferences),
    ("Offline backoff, recovery reset, permanent errors, and manual mode", RetryScheduling),
    ("API rate limits and server failures provide retry classification", ApiRetryClassification),
    ("Interrupted network reads are retryable and remove partial images", InterruptedNetworkRead),
};
var failed = 0;
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception ex) { failed++; Console.Error.WriteLine($"FAIL {test.Name}: {ex}"); }
}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} checks passed.");
return failed == 0 ? 0 : 1;

static async Task UnsplashContract()
{
    var requests = new List<string>();
    using var api = new HttpClient(new Handler(request =>
    {
        Check(request.Headers.Authorization?.ToString() == "Client-ID test-key", "Missing API authorization");
        Check(request.Headers.GetValues("Accept-Version").Single() == "v1", "API version missing");
        requests.Add(request.RequestUri!.AbsoluteUri);
        return Json(request.RequestUri.AbsolutePath.Contains("download") ? "{}" : PhotoJson());
    }));
    using var images = new HttpClient(new Handler(request =>
    {
        Check(request.Headers.Authorization is null, "Credential leaked to CDN");
        Check(requests.Count == 2, "Download tracking must precede the image download");
        return new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
    }));
    var source = new UnsplashSource(api, images, () => "test-key");
    var photo = await source.FetchAsync("sea & sky", 2560, 1440, default);
    Check(requests[0].Contains("query=sea%20%26%20sky"), "Query is not escaped");
    Check(photo.ImageUrl.Contains("ixid=retained&w=2560&h=1440"), "CDN tracking or screen dimensions lost");
    Check(photo.ProfileUrl!.Contains("utm_source=frame_shift&utm_medium=referral"), "Attribution parameters missing");
    using var temp = new Temp();
    var path = Path.Combine(temp.Path, "photo.jpg");
    await source.DownloadAsync(photo, path, default);
    Check(File.ReadAllBytes(path).SequenceEqual(new byte[] { 1, 2, 3 }), "Image was not downloaded");
    Check(requests[1].Contains("/photos/abc/download?ixid=retained"), "Download location altered");
}

static async Task FailedTopic()
{
    using var temp = new Temp();
    var store = new SettingsStore(temp.Path);
    var target = new Target();
    var controller = new WallpaperController(store, new(), target);
    await controller.RefreshAsync(new FakeSource(), new() { Topic = "mountains" }, 1920, 1080, default);
    var current = controller.Current;
    foreach (var code in new[] { HttpStatusCode.NotFound, HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden,
                 HttpStatusCode.TooManyRequests, HttpStatusCode.ServiceUnavailable })
    {
        using var client = new HttpClient(new Handler(_ => new(code)));
        var source = new UnsplashSource(client, client, () => "key");
        await Throws<PhotoSourceException>(() => controller.RefreshAsync(source, new() { Topic = "no results" }, 1920, 1080, default));
        Check(controller.Current == current && target.Applied == 1, "Failed request changed wallpaper");
        Check(store.Load().Topic == "mountains" && controller.CurrentSettings.Topic == "mountains", "Failed request saved topic");
    }
}

static async Task FailedDownloads()
{
    using var temp = new Temp();
    var target = new Target();
    var controller = new WallpaperController(new(temp.Path), new(), target);
    var source = new FakeSource { FailDownload = true };
    await Throws<IOException>(() => controller.RefreshAsync(source, new() { Topic = "new" }, 100, 100, default));
    Check(!Directory.EnumerateFiles(controller.CacheDirectory).Any(), "Partial image remains");
    source.FailDownload = false;
    target.Fail = true;
    await Throws<InvalidOperationException>(() => controller.RefreshAsync(source, new() { Topic = "new" }, 100, 100, default));
    Check(!Directory.EnumerateFiles(controller.CacheDirectory).Any(), "Failed wallpaper remains");
    Check(controller.CurrentSettings.Topic == "nature", "Failed image changed topic");
    target.Fail = false;
    using var canceled = new CancellationTokenSource();
    canceled.Cancel();
    await Throws<OperationCanceledException>(() => controller.RefreshAsync(source, new(), 100, 100, canceled.Token));
    Check(await controller.RefreshAsync(source, new(), 100, 100, default) is not null, "Gate stuck after failure");
}

static async Task CacheRetention()
{
    using var temp = new Temp();
    var controller = new WallpaperController(new(temp.Path), new(), new Target());
    for (var i = 0; i < 14; i++)
        await controller.RefreshAsync(new FakeSource(), new(), 100, 100, default);
    Check(Directory.GetFiles(controller.CacheDirectory, "*.jpg").Length == 10, "Cache must contain ten images");
    Check(File.Exists(controller.Current!.Path), "Current wallpaper was deleted");
}

static async Task ConcurrentRefresh()
{
    using var temp = new Temp();
    var target = new Target();
    var source = new FakeSource { Wait = new(TaskCreationOptions.RunContinuationsAsynchronously) };
    var controller = new WallpaperController(new(temp.Path), new(), target);
    var first = controller.RefreshAsync(source, new(), 100, 100, default);
    Check(await controller.RefreshAsync(new FakeSource(), new(), 100, 100, default) is null, "Second refresh was allowed");
    source.Wait.SetResult(true);
    await first;
    Check(target.Applied == 1, "Multiple wallpapers applied");
}

static Task SettingsRoundTrip()
{
    using var temp = new Temp();
    var store = new SettingsStore(temp.Path);
    store.Save(new() { Topic = "海 & mountains", Source = "Local", LocalFolder = temp.Path });
    Check(new SettingsStore(temp.Path).Load() == store.Load(), "Settings did not round-trip");
    File.WriteAllText(store.FilePath, "{bad json");
    Check(store.Load().Topic == "nature" && store.LoadWarning is not null, "Corrupt settings did not recover");
    Check(!File.Exists(store.FilePath + ".tmp"), "Temporary settings remain");
    return Task.CompletedTask;
}

static async Task LocalPhotos()
{
    using var temp = new Temp();
    File.WriteAllText(Path.Combine(temp.Path, "mountains.JPG"), "original");
    File.WriteAllText(Path.Combine(temp.Path, "sea.png"), "other");
    File.WriteAllText(Path.Combine(temp.Path, "mountains.txt"), "not an image");
    var source = new LocalFolderSource(temp.Path);
    var photo = await source.FetchAsync("MOUNTAIN", 100, 100, default);
    Check(photo.Id == "mountains.JPG", "Topic or extension filter failed");
    await source.DownloadAsync(photo, Path.Combine(temp.Path, "copy.jpg"), default);
    Check(File.ReadAllText(photo.ImageUrl) == "original", "Original modified");
    await Throws<PhotoSourceException>(() => source.FetchAsync("missing", 100, 100, default));
}

static async Task MissingKey()
{
    using var client = new HttpClient(new Handler(_ => throw new Exception("Network must not be used")));
    await Throws<PhotoSourceException>(() => new UnsplashSource(client, client, () => null).FetchAsync("nature", 100, 100, default));
}

static Task LocalKeyConfiguration()
{
    using var temp = new Temp();
    var app = Path.Combine(temp.Path, "app");
    var working = Path.Combine(temp.Path, "working");
    Directory.CreateDirectory(app);
    Directory.CreateDirectory(working);
    var file = Path.Combine(working, ".env.local");
    File.WriteAllText(file, "# comment\nOTHER_KEY=ignored\nUNSPLASH_ACCESS_KEY = 'working-key'\n");
    Check(AccessKeyConfiguration.Load(app, working, null).Key == "working-key", "Working-directory key was not loaded");
    File.WriteAllText(Path.Combine(app, ".env.local"), "UNSPLASH_ACCESS_KEY=\"app-key\"");
    Check(AccessKeyConfiguration.Load(app, working, null).Key == "app-key", "App-local file must take precedence");
    Check(AccessKeyConfiguration.Load(app, working, " env-key ").Key == "env-key", "Environment must take precedence");
    File.WriteAllText(Path.Combine(app, ".env.local"), "UNSPLASH_ACCESS_KEY=\n");
    Check(AccessKeyConfiguration.Load(app, working, " ").Key == "working-key", "Empty values should fall through");
    File.WriteAllText(Path.Combine(app, ".env.local"), "UNSPLASH_ACCESS_KEY=\"private-test-value");
    var result = AccessKeyConfiguration.Load(app, working, null);
    Check(result.Key is null && result.Warning is not null && !result.Warning.Contains("private-test-value"), "Malformed file must not reveal the key");
    Check(AccessKeyConfiguration.Load(temp.Path, temp.Path, null).Key is null, "Missing file should need no key");
    return Task.CompletedTask;
}

static async Task RefreshPreferences()
{
    using var temp = new Temp();
    var store = new SettingsStore(temp.Path);
    File.WriteAllText(store.FilePath, "{\"Topic\":\"sea\"}");
    Check(store.Load().IntervalMinutes == 60, "Existing settings must retain the hourly default");
    File.WriteAllText(store.FilePath, "{\"IntervalMinutes\":-1}");
    Check(store.Load().IntervalMinutes == 60, "Invalid saved interval was not normalized");
    var target = new Target();
    var controller = new WallpaperController(store, new(), target);
    controller.SetInterval(15);
    Check(store.Load().IntervalMinutes == 15 && target.Applied == 0, "Preference change should persist without changing wallpaper");
    await Throws<ArgumentOutOfRangeException>(() => { controller.SetInterval(1); return Task.CompletedTask; });
    Check(controller.CurrentSettings.IntervalMinutes == 15, "Invalid interval changed settings");
    File.Delete(store.FilePath);
    Directory.CreateDirectory(store.FilePath);
    try
    {
        controller.SetInterval(30);
        throw new Exception("Expected the settings write to fail");
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    Check(controller.CurrentSettings.IntervalMinutes == 15, "Failed save changed the live interval");
}

static Task RetryScheduling()
{
    var now = new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
    var schedule = new RefreshSchedule(60);
    schedule.Configure(15, now);
    Check(schedule.NextDueUtc == now.AddMinutes(15), "Changing interval did not reset the due time");
    foreach (var minutes in new[] { 1, 2, 5, 15, 15 })
    {
        schedule.RecordFailure(new HttpRequestException("offline"), now);
        Check(schedule.RetryPending && schedule.NextDueUtc == now.AddMinutes(minutes), "Unexpected retry backoff");
    }
    schedule.RecordSuccess(now);
    Check(!schedule.RetryPending && schedule.NextDueUtc == now.AddMinutes(15), "Recovery must restore the normal interval");
    schedule.RecordFailure(new TaskCanceledException("timeout"), now);
    Check(schedule.NextDueUtc == now.AddMinutes(1), "Success must reset the failure count");
    schedule.RecordFailure(new PhotoSourceException("invalid key"), now);
    Check(!schedule.RetryPending && schedule.NextDueUtc == now.AddMinutes(15), "Permanent failure was retried early");
    schedule.RecordFailure(new HttpRequestException("offline"), now);
    Check(schedule.NextDueUtc == now.AddMinutes(1), "Permanent failure should reset transient backoff");
    schedule.RecordFailure(new PhotoSourceException("limited", true, TimeSpan.FromHours(2)), now);
    Check(schedule.NextDueUtc == now.AddHours(2), "Server Retry-After was not honored");
    schedule.Configure(0, now);
    schedule.RecordFailure(new HttpRequestException("offline"), now);
    Check(schedule.NextDueUtc is null && !schedule.RetryPending, "Manual-only mode scheduled a background retry");
    schedule.RecordSuccess(now);
    Check(schedule.NextDueUtc is null, "Manual-only mode scheduled a regular refresh");
    Check(RefreshSchedule.RetryDelay(new IOException("disk full"), 1) is null, "Disk failures should not be network retries");
    Check(RefreshSchedule.RetryDelay(new HttpRequestException("unauthorized", null, HttpStatusCode.Unauthorized), 1) is null,
        "CDN authorization failure should not be a network retry");
    return Task.CompletedTask;
}

static async Task ApiRetryClassification()
{
    foreach (var (code, rateLimit, transient) in new[]
    {
        (HttpStatusCode.NotFound, false, false),
        (HttpStatusCode.Unauthorized, false, false),
        (HttpStatusCode.Forbidden, false, false),
        (HttpStatusCode.Forbidden, true, true),
        (HttpStatusCode.TooManyRequests, false, true),
        (HttpStatusCode.ServiceUnavailable, false, true),
        (HttpStatusCode.RequestTimeout, false, true)
    })
    {
        using var client = new HttpClient(new Handler(_ =>
        {
            var response = new HttpResponseMessage(code);
            if (rateLimit) response.Headers.Add("X-Ratelimit-Remaining", "0");
            if (code == HttpStatusCode.TooManyRequests)
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(7));
            return response;
        }));
        try
        {
            await new UnsplashSource(client, client, () => "fake").FetchAsync("sea", 100, 100, default);
            throw new Exception("Expected API failure");
        }
        catch (PhotoSourceException ex)
        {
            Check(ex.IsTransient == transient, $"Wrong retry classification for {code}");
            if (code == HttpStatusCode.TooManyRequests)
                Check(ex.RetryAfter == TimeSpan.FromMinutes(7), "Retry-After header lost");
            if (rateLimit) Check(ex.RetryAfter >= TimeSpan.FromHours(1), "Rate limits without Retry-After need a conservative delay");
        }
    }
}

static async Task InterruptedNetworkRead()
{
    using var temp = new Temp();
    using var api = new HttpClient(new Handler(request => Json(request.RequestUri!.AbsolutePath.Contains("download") ? "{}" : PhotoJson())));
    using var images = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StreamContent(new InterruptedStream()) }));
    var controller = new WallpaperController(new(temp.Path), new(), new Target());
    try
    {
        await controller.RefreshAsync(new UnsplashSource(api, images, () => "fake"), new(), 100, 100, default);
        throw new Exception("Expected an interrupted network download");
    }
    catch (HttpRequestException ex)
    {
        Check(RefreshSchedule.RetryDelay(ex, 1) == TimeSpan.FromMinutes(1), "Interrupted read did not schedule a network retry");
        Check(!Directory.EnumerateFiles(controller.CacheDirectory).Any(), "Interrupted network download left a partial file");
        Check(controller.Current is null, "Interrupted download changed the current photo");
    }
}

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static async Task Throws<T>(Func<Task> work) where T : Exception
{
    try { await work(); } catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}");
}
static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
static string PhotoJson() => """
{"id":"abc","urls":{"raw":"https://images.unsplash.com/photo-abc?ixid=retained"},"user":{"name":"Test Photographer","links":{"html":"https://unsplash.com/@test"}},"links":{"html":"https://unsplash.com/photos/abc","download_location":"https://api.unsplash.com/photos/abc/download?ixid=retained"}}
""";

sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(handle(request));
}
sealed class Target : IWallpaperTarget
{
    public int Applied { get; private set; }
    public bool Fail { get; set; }
    public void Apply(string path, int width, int height)
    {
        if (Fail) throw new InvalidOperationException("Fake Windows failure");
        if (!File.Exists(path)) throw new Exception("Missing image");
        Applied++;
    }
}
sealed class FakeSource : IPhotoSource
{
    public bool FailDownload { get; set; }
    public TaskCompletionSource<bool>? Wait { get; set; }
    public async Task<Photo> FetchAsync(string topic, int width, int height, CancellationToken token)
    {
        if (Wait is not null) await Wait.Task.WaitAsync(token);
        return new("id", "fake", "Photographer", null, null, null);
    }
    public async Task DownloadAsync(Photo photo, string destination, CancellationToken token)
    {
        await File.WriteAllTextAsync(destination, "fake image", token);
        if (FailDownload) throw new IOException("Fake interrupted download");
    }
}
sealed class Temp : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FrameShift.Tests", Guid.NewGuid().ToString("N"));
    public Temp() => Directory.CreateDirectory(Path);
    public void Dispose() => Directory.Delete(Path, recursive: true);
}

sealed class InterruptedStream : MemoryStream
{
    private bool delivered;
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (delivered) throw new IOException("Simulated disconnected image stream");
        delivered = true;
        buffer.Span[0] = 42;
        return ValueTask.FromResult(1);
    }
}
