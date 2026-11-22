namespace FrameShift.Core;

public interface IWallpaperTarget
{
    void Apply(string imagePath, int width, int height);
}

public sealed record RefreshResult(Photo Photo, string Path, string? Warning);

public sealed class WallpaperController(SettingsStore store, Settings initial, IWallpaperTarget target)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public Settings CurrentSettings { get; private set; } = initial;
    public RefreshResult? Current { get; private set; }
    public string CacheDirectory => Path.Combine(store.DirectoryPath, "cache");

    public void SetInterval(int minutes)
    {
        if (!Settings.AllowedIntervals.Contains(minutes)) throw new ArgumentOutOfRangeException(nameof(minutes));
        var updated = CurrentSettings with { IntervalMinutes = minutes };
        store.Save(updated);
        CurrentSettings = updated;
    }

    public async Task<RefreshResult?> RefreshAsync(IPhotoSource source, Settings candidate, int width, int height,
        CancellationToken token)
    {
        if (!await gate.WaitAsync(0, token)) return null;
        string? staged = null;
        string? final = null;
        try
        {
            var photo = await source.FetchAsync(candidate.Topic, width, height, token);
            Directory.CreateDirectory(CacheDirectory);
            var name = $"{DateTime.UtcNow:yyyyMMdd_HHmmss_fffffff}_{Guid.NewGuid():N}";
            staged = Path.Combine(CacheDirectory, name + ".partial");
            await source.DownloadAsync(photo, staged, token);
            token.ThrowIfCancellationRequested();
            final = Path.ChangeExtension(staged, ".jpg");
            File.Move(staged, final);
            // The target decodes and fits the image before invoking the Windows API.
            target.Apply(final, width, height);
            CurrentSettings = candidate;
            string? warning = null;
            try { store.Save(candidate); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { warning = "Wallpaper changed, but settings could not be saved. Check that your app data folder is writable."; }
            Current = new(photo, final, warning);
            final = null; // Now owned by the cache; never delete the active wallpaper on failure.
            try { PruneCache(Current.Path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Current = Current with { Warning = "Wallpaper changed, but old cached images could not be removed." }; }
            return Current;
        }
        finally
        {
            TryDelete(staged);
            TryDelete(final);
            gate.Release();
        }
    }

    private void PruneCache(string current)
    {
        foreach (var file in new DirectoryInfo(CacheDirectory).GetFiles("*.jpg")
                     .Where(f => !f.FullName.Equals(current, StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(f => f.Name, StringComparer.Ordinal).Skip(9)) file.Delete();
        foreach (var file in new DirectoryInfo(CacheDirectory).GetFiles("*.partial")) file.Delete();
    }

    private static void TryDelete(string? path)
    {
        try { if (path is not null && File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
