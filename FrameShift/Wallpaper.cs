using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using FrameShift.Core;

namespace FrameShift;

internal sealed class Wallpaper(Action<string>? applyWallpaper = null) : IWallpaperTarget
{
    public void Apply(string imagePath, int width, int height)
    {
        var prepared = imagePath + ".prepared";
        try
        {
            using (var image = Image.FromFile(imagePath))
            using (var bitmap = new Bitmap(width, height))
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.Clear(Color.Black);
                var scale = Math.Max((double)width / image.Width, (double)height / image.Height);
                var w = (float)(image.Width * scale);
                var h = (float)(image.Height * scale);
                graphics.DrawImage(image, (width - w) / 2, (height - h) / 2, w, h);
                bitmap.Save(prepared, ImageFormat.Jpeg);
            }
            File.Move(prepared, imagePath, overwrite: true);
            if (applyWallpaper is not null) applyWallpaper(imagePath);
            else if (!SystemParametersInfo(0x0014, 0, imagePath, 0x01 | 0x02))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not set the desktop wallpaper.");
        }
        finally { if (File.Exists(prepared)) File.Delete(prepared); }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint param, string value, uint flags);
}
