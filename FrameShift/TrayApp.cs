using System.Diagnostics;
using FrameShift.Core;

namespace FrameShift;

internal sealed class TrayApp : ApplicationContext
{
    private readonly NotifyIcon tray;
    private readonly Icon trayIcon;
    private readonly LoadingIndicator loading = new(SystemInformation.SmallIconSize);
    private readonly ContextMenuStrip menu = new();
    private readonly System.Windows.Forms.Timer timer = new();
    private readonly System.Windows.Forms.Timer startup = new() { Interval = 200 };
    private readonly System.Windows.Forms.Timer animation = new() { Interval = 100 };
    private readonly CancellationTokenSource shutdown = new();
    private readonly HttpClient api = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly HttpClient images = new() { Timeout = TimeSpan.FromSeconds(90) };
    private readonly WallpaperController controller;
    private readonly RefreshSchedule schedule;
    private readonly ToolStripMenuItem status = new("Ready") { Enabled = false };
    private readonly ToolStripMenuItem next = new("Next wallpaper");
    private readonly ToolStripMenuItem topic = new("Change topic…");
    private readonly ToolStripMenuItem sourceMenu = new("Photo source");
    private readonly ToolStripMenuItem settingsMenu = new("Settings");
    private readonly ToolStripMenuItem intervalMenu = new("Refresh interval");
    private readonly ToolStripMenuItem startWithWindows = new("Start with Windows");
    private readonly Dictionary<int, ToolStripMenuItem> intervals = new();
    private readonly ToolStripMenuItem unsplash = new("Unsplash (authorized API use)");
    private readonly ToolStripMenuItem local = new("Local photo folder…");
    private readonly ToolStripMenuItem credit = new("Photographer credit") { Enabled = false };
    private readonly ToolStripMenuItem photoLink = new("View photo on Unsplash") { Enabled = false };
    private readonly ToolStripMenuItem setup = new("Configure API…");
    private string? sessionKey;
    private bool busy;
    private bool dialogOpen;
    private bool exiting;
    private bool outageNotified;
    private int animationFrame;
    private Task? activeRefresh;

    public TrayApp()
    {
        var keyConfiguration = AccessKeyConfiguration.Load(AppContext.BaseDirectory, Environment.CurrentDirectory,
            Environment.GetEnvironmentVariable("UNSPLASH_ACCESS_KEY"));
        sessionKey = keyConfiguration.Key;
        var store = new SettingsStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrameShift"));
        controller = new(store, store.Load(), new Wallpaper());
        schedule = new(controller.CurrentSettings.IntervalMinutes);
        schedule.Configure(controller.CurrentSettings.IntervalMinutes, DateTimeOffset.UtcNow);
        using (var iconStream = typeof(TrayApp).Assembly.GetManifestResourceStream("FrameShift.app.ico")
               ?? throw new InvalidOperationException("The app icon resource is missing."))
            trayIcon = new Icon(iconStream, SystemInformation.SmallIconSize);
        tray = new NotifyIcon { Icon = trayIcon, Visible = true, Text = "Frame Shift", ContextMenuStrip = menu };
        menu.Items.Add(new ToolStripMenuItem($"Frame Shift v{Application.ProductVersion}") { Enabled = false });
        menu.Items.Add(status);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(next);
        menu.Items.Add(topic);
        sourceMenu.DropDownItems.AddRange([unsplash, local]);
        sourceMenu.DropDownItems.Add(new ToolStripSeparator());
        sourceMenu.DropDownItems.Add(credit);
        sourceMenu.DropDownItems.Add(photoLink);
        sourceMenu.DropDownItems.Add("Photos on Unsplash", null, (_, _) => Open("https://unsplash.com/?utm_source=frame_shift&utm_medium=referral"));
        menu.Items.Add(sourceMenu);
        settingsMenu.DropDownItems.Add(setup);
        settingsMenu.DropDownItems.Add(intervalMenu);
        settingsMenu.DropDownItems.Add(new ToolStripSeparator());
        settingsMenu.DropDownItems.Add(startWithWindows);
        foreach (var minutes in Settings.AllowedIntervals)
        {
            var item = new ToolStripMenuItem(IntervalLabel(minutes));
            item.Click += (_, _) => ChangeInterval(minutes);
            intervals.Add(minutes, item);
            intervalMenu.DropDownItems.Add(item);
        }
        try { startWithWindows.Checked = Autostart.IsEnabled(); }
        catch (Exception ex) when (IsStartupError(ex)) { startWithWindows.Checked = false; }
        menu.Items.Add(settingsMenu);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, async (_, _) => await StopAsync());
        next.Click += async (_, _) => await StartRefreshAsync(controller.CurrentSettings);
        tray.MouseClick += async (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                await StartRefreshAsync(controller.CurrentSettings);
        };
        topic.Click += async (_, _) => await ChangeTopicAsync();
        local.Click += async (_, _) => await ChooseFolderAsync();
        unsplash.Click += async (_, _) => await StartRefreshAsync(controller.CurrentSettings with { Source = "Unsplash" });
        setup.Click += (_, _) => ConfigureApi();
        startWithWindows.Click += (_, _) => ToggleAutostart();
        credit.Click += (_, _) => Open(controller.Current?.Photo.ProfileUrl);
        photoLink.Click += (_, _) => Open(controller.Current?.Photo.PhotoUrl);
        menu.Opening += (_, _) => UpdateMenu();
        animation.Tick += (_, _) => tray.Icon = loading.Frames[++animationFrame % loading.Frames.Length];
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            if (schedule.NextDueUtc <= DateTimeOffset.UtcNow)
                await StartRefreshAsync(controller.CurrentSettings, userInitiated: false);
            else ArmTimer();
        };
        startup.Tick += async (_, _) =>
        {
            startup.Stop();
            if (store.LoadWarning is not null) Notify(store.LoadWarning, ToolTipIcon.Warning);
            if (keyConfiguration.Warning is not null) Notify(keyConfiguration.Warning, ToolTipIcon.Warning);
            if (!dialogOpen) await StartRefreshAsync(controller.CurrentSettings, userInitiated: false);
        };
        UpdateMenu();
        startup.Start();
    }

    private Task StartRefreshAsync(Settings candidate, bool userInitiated = true)
    {
        if (busy || exiting || dialogOpen) return Task.CompletedTask;
        activeRefresh = RefreshAsync(candidate, userInitiated);
        return activeRefresh;
    }

    private async Task RefreshAsync(Settings candidate, bool userInitiated)
    {
        busy = true;
        timer.Stop();
        animationFrame = 0;
        tray.Icon = loading.Frames[0];
        animation.Start();
        UpdateMenu();
        try
        {
            IPhotoSource source = candidate.Source == "Local"
                ? new LocalFolderSource(candidate.LocalFolder ?? "")
                : new UnsplashSource(api, images, () => sessionKey);
            var bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
            var result = await controller.RefreshAsync(source, candidate, bounds.Width, bounds.Height, shutdown.Token);
            if (result is not null && !exiting)
            {
                schedule.RecordSuccess(DateTimeOffset.UtcNow);
                credit.Text = Truncate("Photo by " + result.Photo.Photographer, 85);
                credit.Enabled = result.Photo.ProfileUrl is not null;
                photoLink.Enabled = result.Photo.PhotoUrl is not null;
                if (result.Warning is not null) Notify(result.Warning, ToolTipIcon.Warning);
                else if (outageNotified) Notify("Connection restored. Your wallpaper has been updated.", ToolTipIcon.Info);
                outageNotified = false;
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!exiting)
            {
                schedule.RecordFailure(ex, DateTimeOffset.UtcNow);
                var message = ex switch
                {
                    PhotoSourceException => ex.Message,
                    OperationCanceledException => "The photo request timed out. Your current wallpaper has been kept.",
                    HttpRequestException => "Could not reach the photo service. Check your connection and try again.",
                    IOException or UnauthorizedAccessException => "Could not read or save the image. Check your photo and app data folders.",
                    ArgumentException or OutOfMemoryException => "The downloaded file could not be decoded as an image. Try another photo.",
                    _ => "The wallpaper could not be changed. " + ex.Message
                };
                if (schedule.RetryPending)
                    message += $" Retrying the saved topic at {schedule.NextDueUtc!.Value.ToLocalTime():HH:mm}.";
                if (userInitiated || !outageNotified) Notify(message, ToolTipIcon.Warning);
                outageNotified = schedule.RetryPending;
            }
        }
        finally
        {
            animation.Stop();
            busy = false;
            if (!exiting)
            {
                tray.Icon = trayIcon;
                UpdateMenu();
                ArmTimer();
            }
        }
    }

    private void ArmTimer()
    {
        timer.Stop();
        if (exiting || busy || dialogOpen || schedule.NextDueUtc is not { } due) return;
        timer.Interval = (int)Math.Clamp(Math.Ceiling((due - DateTimeOffset.UtcNow).TotalMilliseconds), 1, int.MaxValue);
        timer.Start();
    }

    private void BeginDialog()
    {
        dialogOpen = true;
        timer.Stop();
        UpdateMenu();
    }

    private void EndDialog()
    {
        dialogOpen = false;
        if (!exiting)
        {
            UpdateMenu();
            ArmTimer();
        }
    }

    private async Task ChangeTopicAsync()
    {
        string? value = null;
        BeginDialog();
        try
        {
            using var dialog = new InputDialog("Change topic", controller.CurrentSettings.Source == "Local"
                ? "Match photo filenames in your folder. Leave empty to use all photos."
                : "Choose a subject for every wallpaper, such as mountains or architecture. Leave empty for any subject.", controller.CurrentSettings.Topic);
            if (dialog.ShowDialog() == DialogResult.OK) value = dialog.Value;
        }
        finally { EndDialog(); }
        if (value is not null) await StartRefreshAsync(controller.CurrentSettings with { Topic = value });
    }

    private async Task ChooseFolderAsync()
    {
        string? folder = null;
        BeginDialog();
        try
        {
            using var dialog = new FolderBrowserDialog { Description = "Choose a folder of JPG, PNG, or BMP photos. Subfolders are not included.",
                UseDescriptionForTitle = true, InitialDirectory = controller.CurrentSettings.LocalFolder ?? "" };
            if (dialog.ShowDialog() == DialogResult.OK) folder = dialog.SelectedPath;
        }
        finally { EndDialog(); }
        if (folder is not null)
            await StartRefreshAsync(controller.CurrentSettings with { Source = "Local", LocalFolder = folder, Topic = "" });
    }

    private void ConfigureApi()
    {
        BeginDialog();
        try
        {
            using var dialog = new InputDialog("Unsplash API setup",
                "Enter an access key for this session. For future launches, set UNSPLASH_ACCESS_KEY in .env.local beside the app or in your project folder. An environment variable overrides the file. Keys are never written to settings.", sessionKey ?? "", secret: true);
            if (dialog.ShowDialog() == DialogResult.OK)
            {
                sessionKey = dialog.Value;
                Notify("Session key updated. Choose Next wallpaper to try it.", ToolTipIcon.Info);
            }
        }
        finally { EndDialog(); }
    }

    private void ChangeInterval(int minutes)
    {
        try
        {
            controller.SetInterval(minutes);
            schedule.Configure(minutes, DateTimeOffset.UtcNow);
            outageNotified = false;
            UpdateMenu();
            ArmTimer();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Notify("The refresh interval could not be saved. Your previous interval has been kept.", ToolTipIcon.Warning); }
    }

    private void ToggleAutostart()
    {
        try
        {
            var enabled = !startWithWindows.Checked;
            Autostart.SetEnabled(enabled);
            startWithWindows.Checked = enabled;
        }
        catch (Exception ex) when (IsStartupError(ex))
        { Notify("Windows startup settings could not be updated. Launch FrameShift.exe from a permanent folder and try again.", ToolTipIcon.Warning); }
    }

    private static bool IsStartupError(Exception error) => error is IOException or UnauthorizedAccessException or
        System.Security.SecurityException or InvalidOperationException or ArgumentException;

    private static string IntervalLabel(int minutes) => minutes switch
    {
        0 => "Manual only",
        15 => "Every 15 minutes",
        30 => "Every 30 minutes",
        60 => "Every hour",
        180 => "Every 3 hours",
        360 => "Every 6 hours",
        1440 => "Every day",
        _ => "Every hour"
    };

    private void UpdateMenu()
    {
        var settings = controller.CurrentSettings;
        var timing = schedule.RetryPending
            ? $"Retry at {schedule.NextDueUtc!.Value.ToLocalTime():HH:mm}"
            : IntervalLabel(settings.IntervalMinutes);
        status.Text = busy ? "Changing wallpaper…" : $"{(settings.Source == "Local" ? "Local folder" : "Unsplash")} · {Truncate(string.IsNullOrEmpty(settings.Topic) ? "All topics" : settings.Topic, 35)} · {timing}";
        next.Enabled = topic.Enabled = sourceMenu.Enabled = settingsMenu.Enabled = !busy && !dialogOpen;
        foreach (var (minutes, item) in intervals) item.Checked = minutes == settings.IntervalMinutes;
        unsplash.Checked = settings.Source == "Unsplash";
        local.Checked = settings.Source == "Local";
        tray.Text = Truncate("Frame Shift · " + (busy ? "Changing wallpaper…" : schedule.RetryPending ? timing :
            string.IsNullOrEmpty(settings.Topic) ? "All topics" : settings.Topic), 63);
    }

    private void Notify(string message, ToolTipIcon icon) => tray.ShowBalloonTip(7000, "Frame Shift", message, icon);
    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
    private void Open(string? url)
    {
        if (url is null) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        { Notify("Windows could not open your browser.", ToolTipIcon.Warning); }
    }

    private async Task StopAsync()
    {
        if (exiting) return;
        exiting = true;
        timer.Stop();
        startup.Stop();
        animation.Stop();
        menu.Enabled = false;
        await shutdown.CancelAsync();
        if (activeRefresh is not null) await activeRefresh;
        tray.Visible = false;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            tray.Dispose(); animation.Dispose(); loading.Dispose(); trayIcon.Dispose();
            menu.Dispose(); timer.Dispose(); startup.Dispose(); shutdown.Dispose(); api.Dispose(); images.Dispose();
        }
        base.Dispose(disposing);
    }
}
