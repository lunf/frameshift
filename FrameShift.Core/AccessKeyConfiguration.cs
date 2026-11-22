namespace FrameShift.Core;

public sealed record AccessKeyResult(string? Key, string? Warning = null);

public static class AccessKeyConfiguration
{
    public static AccessKeyResult Load(string appDirectory, string workingDirectory, string? environmentKey)
    {
        if (!string.IsNullOrWhiteSpace(environmentKey)) return new(environmentKey.Trim());

        foreach (var directory in new[] { appDirectory, workingDirectory }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var path = Path.Combine(directory, ".env.local");
            try
            {
                if (!File.Exists(path)) continue;
                foreach (var rawLine in File.ReadLines(path))
                {
                    var line = rawLine.Trim();
                    if (line.Length == 0 || line.StartsWith('#')) continue;
                    var separator = line.IndexOf('=');
                    if (separator < 0 || line[..separator].Trim() != "UNSPLASH_ACCESS_KEY") continue;
                    var value = line[(separator + 1)..].Trim();
                    if (value.StartsWith('\'') || value.StartsWith('"'))
                    {
                        if (value.Length < 2 || value[^1] != value[0])
                            return new(null, "The UNSPLASH_ACCESS_KEY value in .env.local has mismatched quotes. Correct it and restart Frame Shift.");
                        value = value[1..^1].Trim();
                    }
                    if (!string.IsNullOrWhiteSpace(value)) return new(value);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { return new(null, "Could not read .env.local. Check its file permissions, or enter a session key using API setup."); }
        }
        return new(null);
    }
}
