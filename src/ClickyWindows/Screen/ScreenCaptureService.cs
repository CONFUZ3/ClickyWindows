using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using Serilog;

namespace ClickyWindows.Screen;

public record ScreenCapture(string Base64Jpeg, int Width, int Height, int MonitorIndex, bool IsFocus);

/// <summary>
/// Captures screens using Graphics.CopyFromScreen (adequate for single-frame capture per plan).
/// Resizes to max 1280px wide, JPEG 80% quality.
/// </summary>
public class ScreenCaptureService
{
    private const int MaxWidth = 1280;
    private const int JpegQuality = 80;

    public List<ScreenCapture> CaptureAll(IReadOnlyList<MonitorInfo> monitors)
    {
        var captures = new List<ScreenCapture>();

        foreach (var monitor in monitors)
        {
            try
            {
                var capture = CaptureMonitor(monitor);
                if (capture != null)
                    captures.Add(capture);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to capture monitor {Index}", monitor.Index);
            }
        }

        return captures;
    }

    private ScreenCapture? CaptureMonitor(MonitorInfo monitor)
    {
        var physBounds = monitor.PhysicalBounds;
        Log.Debug("Capturing monitor {Index}: {W}x{H} @ ({X},{Y})",
            monitor.Index, physBounds.Width, physBounds.Height, physBounds.X, physBounds.Y);

        using var bitmap = new Bitmap(physBounds.Width, physBounds.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);

        g.CopyFromScreen(physBounds.X, physBounds.Y, 0, 0,
            new Size(physBounds.Width, physBounds.Height),
            CopyPixelOperation.SourceCopy);

        // Resize to max 1280px wide
        var (resized, finalW, finalH) = ResizeIfNeeded(bitmap);
        using (resized)
        {
            var base64 = EncodeJpeg(resized);
            return new ScreenCapture(base64, finalW, finalH, monitor.Index, monitor.IsPrimary);
        }
    }

    private (Bitmap bitmap, int width, int height) ResizeIfNeeded(Bitmap source)
    {
        if (source.Width <= MaxWidth)
            return (source, source.Width, source.Height);

        double scale = (double)MaxWidth / source.Width;
        int newW = MaxWidth;
        int newH = (int)(source.Height * scale);

        var resized = new Bitmap(newW, newH, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(resized);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(source, 0, 0, newW, newH);

        return (resized, newW, newH);
    }

    private static string EncodeJpeg(Bitmap bitmap)
    {
        var encoder = ImageCodecInfo.GetImageEncoders()
            .First(e => e.MimeType == "image/jpeg");

        var encoderParams = new EncoderParameters(1);
        encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, (long)JpegQuality);

        using var ms = new MemoryStream();
        bitmap.Save(ms, encoder, encoderParams);
        return Convert.ToBase64String(ms.ToArray());
    }
}
