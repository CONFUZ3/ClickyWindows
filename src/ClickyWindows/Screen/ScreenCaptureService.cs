using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Serilog;

namespace ClickyWindows.Screen;

public record ScreenCapture(string Base64Jpeg, int Width, int Height, int MonitorIndex, bool IsFocus);

/// <summary>
/// Captures screens using Graphics.CopyFromScreen at full physical resolution.
/// Sends JPEG at 80% quality — full resolution is required so Gemini's coordinate estimates
/// map directly to physical screen pixels without any scaling correction.
/// </summary>
public class ScreenCaptureService
{
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

        // Send at full physical resolution so Gemini's coordinate estimates match the screen exactly.
        // Resizing to a smaller image causes Gemini to report coordinates in an ambiguous pixel space,
        // making it impossible to accurately map them back to physical screen coordinates.
        var base64 = EncodeJpeg(bitmap);
        return new ScreenCapture(base64, physBounds.Width, physBounds.Height, monitor.Index, monitor.IsPrimary);
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
