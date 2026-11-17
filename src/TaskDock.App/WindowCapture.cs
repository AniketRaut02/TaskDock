using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using TaskDock.Platform;

namespace TaskDock.App;

/// <summary>
/// Peek: one JPEG of a source's window. PrintWindow with full content works for Unity, Visual Studio, Chrome and Electron,
/// covered or with the PC locked (spike S5). Sizes are physical pixels because the app is per-monitor DPI aware;
/// a DPI-unaware caller would get a scaled rect and a cropped image.
/// </summary>
static class WindowCapture
{
    const int MaxWidth = 1080;

    public static (byte[]? Jpeg, string? Code) Capture(nint hwnd)
    {
        if (!Desktop.IsWindow(hwnd)) return (null, "not_found");
        if (Win32.IsIconic(hwnd)) return (null, "minimized"); // a minimized window is a 159×27 title bar
        if (!Win32.GetWindowRect(hwnd, out var r) || r.Width < 2 || r.Height < 2) return (null, "not_found");

        using var full = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(full))
        {
            var hdc = g.GetHdc();
            try { if (!Win32.PrintWindow(hwnd, hdc, Win32.PW_RENDERFULLCONTENT)) return (null, "blank"); }
            finally { g.ReleaseHdc(hdc); }
        }
        if (IsBlank(full)) return (null, "blank"); // not seen in S5, but sleep or a disconnected display can give one

        var scale = Math.Min(1.0, (double)MaxWidth / full.Width);
        using var small = new Bitmap(Math.Max(1, (int)(full.Width * scale)), Math.Max(1, (int)(full.Height * scale)), PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(full, 0, 0, small.Width, small.Height);
        }
        using var ms = new MemoryStream();
        var jpeg = ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == ImageFormat.Jpeg.Guid);
        using var quality = new EncoderParameters(1) { Param = { [0] = new EncoderParameter(Encoder.Quality, 70L) } };
        small.Save(ms, jpeg, quality);
        return (ms.ToArray(), null);
    }

    /// <summary>98% of a 32×32 sample grid near black.</summary>
    static bool IsBlank(Bitmap b)
    {
        int dark = 0, n = 0;
        for (var y = 0; y < 32; y++)
            for (var x = 0; x < 32; x++, n++)
            {
                var c = b.GetPixel(x * (b.Width - 1) / 31, y * (b.Height - 1) / 31);
                if (c.R + c.G + c.B < 24) dark++;
            }
        return dark >= n * 0.98;
    }
}
