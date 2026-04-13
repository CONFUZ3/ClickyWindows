using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using Serilog;

namespace ClickyWindows.Screen;

/// <param name="Width">Screenshot width in pixels (may be scaled down from physical).</param>
/// <param name="Height">Screenshot height in pixels (may be scaled down from physical).</param>
/// <param name="PhysicalWidth">Original monitor width in physical pixels (before scaling).</param>
/// <param name="PhysicalHeight">Original monitor height in physical pixels (before scaling).</param>
public record ScreenCapture(string Base64Jpeg, int Width, int Height, int PhysicalWidth, int PhysicalHeight, int MonitorIndex, bool IsFocus);

/// <summary>
/// Captures screens using Graphics.CopyFromScreen.
/// Screenshots are scaled to at most MaxScreenshotDimension on the longest edge before sending to
/// Gemini. This keeps the coordinate space small and matches the resolution that vision models
/// reason most accurately about. Coordinates Gemini returns must be scaled back to physical
/// pixels using the Width/PhysicalWidth ratio in ScreenCapture.
/// </summary>
public class ScreenCaptureService
{
    private const int JpegQuality = 80;

    // Max pixel dimension (width or height) for screenshots sent to Gemini.
    // Smaller images keep coordinate error small and match vision model internal resolutions.
    public const int MaxScreenshotDimension = 1280;

    /// <summary>
    /// Compute the screenshot dimensions that CaptureMonitor will produce for a physical resolution.
    /// Used by GeminiLiveService to tell Gemini the exact coordinate space before capturing.
    /// </summary>
    public static (int Width, int Height) GetScaledDimensions(int physicalWidth, int physicalHeight)
    {
        double scale = Math.Min(1.0, (double)MaxScreenshotDimension / Math.Max(physicalWidth, physicalHeight));
        return ((int)(physicalWidth * scale), (int)(physicalHeight * scale));
    }

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

        using var fullBitmap = new Bitmap(physBounds.Width, physBounds.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(fullBitmap);

        g.CopyFromScreen(physBounds.X, physBounds.Y, 0, 0,
            new Size(physBounds.Width, physBounds.Height),
            CopyPixelOperation.SourceCopy);

        var (scaledWidth, scaledHeight) = GetScaledDimensions(physBounds.Width, physBounds.Height);

        string base64;
        if (scaledWidth == physBounds.Width && scaledHeight == physBounds.Height)
        {
            // No scaling needed — encode the full bitmap directly.
            base64 = EncodeJpeg(fullBitmap);
        }
        else
        {
            // Downscale with high-quality bilinear interpolation so UI text stays readable.
            using var scaledBitmap = new Bitmap(scaledWidth, scaledHeight, PixelFormat.Format32bppArgb);
            using var scaledGraphics = Graphics.FromImage(scaledBitmap);
            scaledGraphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            scaledGraphics.DrawImage(fullBitmap, 0, 0, scaledWidth, scaledHeight);
            base64 = EncodeJpeg(scaledBitmap);
        }

        Log.Debug("Screenshot for monitor {Index}: {SW}x{SH} (physical {PW}x{PH})",
            monitor.Index, scaledWidth, scaledHeight, physBounds.Width, physBounds.Height);

        return new ScreenCapture(base64, scaledWidth, scaledHeight,
            physBounds.Width, physBounds.Height, monitor.Index, monitor.IsPrimary);
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
