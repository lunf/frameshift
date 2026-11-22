using Microsoft.Win32;

namespace FrameShift;

internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "FrameShift";

    public static string BuildCommand(string executable, string workingDirectory)
        => $"{QuotePath(executable)} --working-directory {QuotePath(workingDirectory)}";

    private static string QuotePath(string path)
    {
        if (path.Contains('"')) throw new ArgumentException("A startup path cannot contain quotes.");
        var trailingSlashes = path.Length - path.TrimEnd('\\').Length;
        return "\"" + path + new string('\\', trailingSlashes) + "\"";
    }

    private static string CurrentCommand()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Could not locate the app executable.");
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Launch FrameShift.exe before enabling Start with Windows.");
        return BuildCommand(executable, Environment.CurrentDirectory);
    }

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return string.Equals(key?.GetValue(ValueName) as string, CurrentCommand(), StringComparison.OrdinalIgnoreCase);
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
            ?? throw new IOException("The Windows startup settings could not be opened.");
        if (enabled) key.SetValue(ValueName, CurrentCommand(), RegistryValueKind.String);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    public static void RestoreWorkingDirectory(string[] args)
    {
        if (args.Length == 2 && args[0] == "--working-directory" && Directory.Exists(args[1]))
            Directory.SetCurrentDirectory(args[1]);
    }
}
