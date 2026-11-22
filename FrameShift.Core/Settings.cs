using System.Text.Json;

namespace FrameShift.Core;

public sealed record Settings
{
    public string Topic { get; init; } = "nature";
    public string Source { get; init; } = "Unsplash";
    public string? LocalFolder { get; init; }
    public int IntervalMinutes { get; init; } = 60;
    public static IReadOnlyList<int> AllowedIntervals { get; } = Array.AsReadOnly(new[] { 0, 15, 30, 60, 180, 360, 1440 });
    public static int NormalizeInterval(int value) => AllowedIntervals.Contains(value) ? value : 60;
}

public sealed class SettingsStore(string directory)
{
    public string DirectoryPath { get; } = directory;
    public string FilePath => Path.Combine(DirectoryPath, "settings.json");
    public string? LoadWarning { get; private set; }

    public Settings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath))
                ?? throw new JsonException("Empty settings.");
            return settings with { Topic = settings.Topic?.Trim() ?? "nature",
                Source = settings.Source == "Local" ? "Local" : "Unsplash",
                IntervalMinutes = Settings.NormalizeInterval(settings.IntervalMinutes) };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LoadWarning = "Settings could not be read. Defaults are being used; your next successful change will save new settings.";
            return new();
        }
    }

    public void Save(Settings settings)
    {
        Directory.CreateDirectory(DirectoryPath);
        var temporary = FilePath + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
