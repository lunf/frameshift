namespace FrameShift;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var instance = new Mutex(true, "Local\\FrameShift.Wallpaper", out var created);
        if (!created)
        {
            MessageBox.Show("Frame Shift is already running. Look for its icon in the Windows tray (including hidden icons).",
                "Frame Shift", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        ApplicationConfiguration.Initialize();
        try { Autostart.RestoreWorkingDirectory(args); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show("The startup folder could not be opened. Check your photo source and API key configuration.",
                "Frame Shift", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        using var context = new TrayApp();
        Application.Run(context);
    }
}
