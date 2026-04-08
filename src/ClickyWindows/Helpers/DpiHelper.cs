using ClickyWindows.Interop;

namespace ClickyWindows.Helpers;

public static class DpiHelper
{
    public const double DefaultDpi = 96.0;

    public static double GetScaleForDpi(uint dpi) => dpi / DefaultDpi;

    public static (uint dpiX, uint dpiY) GetDpiForMonitor(IntPtr hMonitor)
    {
        var result = NativeMethods.GetDpiForMonitor(hMonitor, NativeMethods.MDT_EFFECTIVE_DPI, out var dpiX, out var dpiY);
        if (result != 0)
            return (96, 96); // fallback
        return (dpiX, dpiY);
    }
}
