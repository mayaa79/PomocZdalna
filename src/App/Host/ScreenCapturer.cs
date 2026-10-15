using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace PomocZdalna.App.Host;

/// <summary>Zrzut głównego ekranu (GDI) zakodowany do JPEG, z narysowanym kursorem.</summary>
public sealed class ScreenCapturer
{
    private readonly ImageCodecInfo _jpeg =
        ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == ImageFormat.Jpeg.Guid);

    /// <returns>JPEG albo null, gdy zrzut się nie udał (np. bezpieczny pulpit UAC / ekran blokady).</returns>
    public byte[]? Capture(int maxWidth = 1600, long quality = 55)
    {
        try
        {
            int w = Native.GetSystemMetrics(Native.SM_CXSCREEN);
            int h = Native.GetSystemMetrics(Native.SM_CYSCREEN);
            if (w <= 0 || h <= 0) return null;

            using var full = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(full))
            {
                g.CopyFromScreen(0, 0, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
                DrawCursor(g);
            }

            Bitmap src = full;
            Bitmap? scaled = null;
            if (w > maxWidth)
            {
                int nh = h * maxWidth / w;
                scaled = new Bitmap(maxWidth, nh, PixelFormat.Format24bppRgb);
                using var g2 = Graphics.FromImage(scaled);
                g2.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g2.DrawImage(full, 0, 0, maxWidth, nh);
                src = scaled;
            }

            using var ms = new MemoryStream();
            using var ep = new EncoderParameters(1);
            ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
            src.Save(ms, _jpeg, ep);
            scaled?.Dispose();
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static void DrawCursor(Graphics g)
    {
        try
        {
            var ci = new Native.CURSORINFO { cbSize = Marshal.SizeOf<Native.CURSORINFO>() };
            if (!Native.GetCursorInfo(ref ci) || (ci.flags & Native.CURSOR_SHOWING) == 0) return;

            int hx = 0, hy = 0;
            if (Native.GetIconInfo(ci.hCursor, out var ii))
            {
                hx = ii.xHotspot; hy = ii.yHotspot;
                if (ii.hbmMask != IntPtr.Zero) Native.DeleteObject(ii.hbmMask);
                if (ii.hbmColor != IntPtr.Zero) Native.DeleteObject(ii.hbmColor);
            }

            IntPtr hdc = g.GetHdc();
            try { Native.DrawIconEx(hdc, ci.pt.x - hx, ci.pt.y - hy, ci.hCursor, 0, 0, 0, IntPtr.Zero, Native.DI_NORMAL); }
            finally { g.ReleaseHdc(hdc); }
        }
        catch { }
    }
}
