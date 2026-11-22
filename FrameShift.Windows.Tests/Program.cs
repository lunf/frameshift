using System.Drawing.Imaging;
using FrameShift;
using FrameShift.Core;

namespace FrameShift.Windows.Tests;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (args.Contains("--preview"))
        {
            using var dialog = new InputDialog("Unsplash API setup",
                "Use this integration only with separate Unsplash authorization for wallpaper use.\n\nEnter an access key for this session, or set UNSPLASH_ACCESS_KEY before launching for future sessions. Keys are never written to settings.", "", secret: true);
            dialog.ShowDialog();
            return 0;
        }
        var root = Path.Combine(Path.GetTempPath(), "FrameShift.Windows.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            CheckStartupCommands(root);
            using (var indicator = new LoadingIndicator(new Size(32, 32)))
            {
                if (indicator.Frames.Length != 12) throw new Exception("Loading animation frames are missing.");
                using var sheet = new Bitmap(384, 64);
                using var canvas = Graphics.FromImage(sheet);
                canvas.Clear(Color.White);
                canvas.FillRectangle(Brushes.Black, 0, 32, 384, 32);
                for (var i = 0; i < indicator.Frames.Length; i++)
                {
                    using var bitmap = indicator.Frames[i].ToBitmap();
                    canvas.DrawImage(bitmap, i * 32, 0);
                    canvas.DrawImage(bitmap, i * 32, 32);
                }
                if (args.Length == 2 && args[0] == "--loading-sheet") sheet.Save(args[1], ImageFormat.Png);
            }
            Console.WriteLine("PASS loading animation frames render after native handles are released");
            var path = Path.Combine(root, "wallpaper.jpg");
            using (var source = new Bitmap(100, 200))
            {
                using var graphics = Graphics.FromImage(source);
                graphics.Clear(Color.Red);
                graphics.FillRectangle(Brushes.Blue, 0, 75, 100, 50);
                source.Save(path, ImageFormat.Png);
            }
            var applied = false;
            new Wallpaper(imagePath =>
            {
                using var image = new Bitmap(imagePath);
                if (image.Width != 160 || image.Height != 90 || image.RawFormat.Guid != ImageFormat.Jpeg.Guid)
                    throw new Exception("Image dimensions or encoding are incorrect.");
                if (image.GetPixel(80, 45).B < 200) throw new Exception("Image was not center-cropped.");
                applied = true;
            }).Apply(path, 160, 90);
            if (!applied || Directory.GetFiles(root).Length != 1) throw new Exception("Image preparation did not clean up.");
            Console.WriteLine("PASS native image decoding, JPEG conversion, primary-display sizing, and center crop");

            File.WriteAllText(path, "invalid image");
            applied = false;
            try { new Wallpaper(_ => applied = true).Apply(path, 160, 90); }
            catch (PhotoSourceException) { }
            if (applied) throw new Exception("Invalid image reached Windows wallpaper API.");
            Console.WriteLine("PASS invalid images never reach the Windows wallpaper API");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void CheckStartupCommands(string root)
    {
        foreach (var workingDirectory in new[] { @"D:\github\frame shift", @"D:\" })
        {
            var command = Autostart.BuildCommand(@"D:\My Apps\FrameShift.exe", workingDirectory);
            var pointer = CommandLineToArgvW(command, out var count);
            if (pointer == IntPtr.Zero) throw new Exception("Windows could not parse startup command.");
            try
            {
                var values = Enumerable.Range(0, count).Select(i => System.Runtime.InteropServices.Marshal.PtrToStringUni(
                    System.Runtime.InteropServices.Marshal.ReadIntPtr(pointer, i * IntPtr.Size))).ToArray();
                if (!values.SequenceEqual(new[] { @"D:\My Apps\FrameShift.exe", "--working-directory", workingDirectory }))
                    throw new Exception("Startup command did not preserve paths with spaces or trailing slashes.");
            }
            finally { LocalFree(pointer); }
        }
        var original = Environment.CurrentDirectory;
        try
        {
            Autostart.RestoreWorkingDirectory(["--working-directory", root]);
            if (Environment.CurrentDirectory != root) throw new Exception("Startup working directory was not restored.");
        }
        finally { Directory.SetCurrentDirectory(original); }
        Console.WriteLine("PASS Windows startup command quoting and working-directory restoration without changing the registry");
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
