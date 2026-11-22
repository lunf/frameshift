using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace FrameShift;

internal sealed class LoadingIndicator : IDisposable
{
    public Icon[] Frames { get; }

    public LoadingIndicator(Size size)
    {
        Frames = new Icon[12];
        for (var frame = 0; frame < Frames.Length; frame++)
        {
            using var bitmap = new Bitmap(size.Width, size.Height);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            var width = Math.Max(2f, size.Width / 8f);
            var inset = width + 1;
            var bounds = new RectangleF(inset, inset, size.Width - 2 * inset, size.Height - 2 * inset);
            using var track = new Pen(Color.FromArgb(40, 110, 75), width);
            using var arc = new Pen(Color.FromArgb(46, 234, 132), width) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            graphics.DrawEllipse(track, bounds);
            graphics.DrawArc(arc, bounds, frame * 30, 130);
            var handle = bitmap.GetHicon();
            try
            {
                using var borrowed = Icon.FromHandle(handle);
                Frames[frame] = (Icon)borrowed.Clone();
            }
            finally { DestroyIcon(handle); }
        }
    }

    public void Dispose() { foreach (var frame in Frames) frame.Dispose(); }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
