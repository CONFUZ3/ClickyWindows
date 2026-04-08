using System.Windows;
using ClickyWindows.Helpers;
using ClickyWindows.Interop;
using Serilog;

namespace ClickyWindows.Screen;

/// <summary>
/// Enumerates monitors using EnumDisplayMonitors + GetDpiForMonitor P/Invoke.
/// Does NOT use Screen.AllScreens due to confirmed DPI bugs (dotnet/winforms#10952).
/// </summary>
public static class MonitorEnumerator
{
    // State shared with the callback
    private static List<MonitorInfo>? _currentMonitors;
    private static int _currentIndex;

    public static List<MonitorInfo> GetMonitors()
    {
        var monitors = new List<MonitorInfo>();
        _currentMonitors = monitors;
        _currentIndex = 0;

        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, MonitorCallback, IntPtr.Zero);

        _currentMonitors = null;

        // Sort: primary first, then by left edge
        monitors.Sort((a, b) =>
        {
            if (a.IsPrimary && !b.IsPrimary) return -1;
            if (!a.IsPrimary && b.IsPrimary) return 1;
            return a.Bounds.Left.CompareTo(b.Bounds.Left);
        });

        Log.Debug("Enumerated {Count} monitors", monitors.Count);
        return monitors;
    }

    private static bool MonitorCallback(IntPtr hMonitor, IntPtr hdcMonitor, ref NativeMethods.RECT lprcMonitor, IntPtr dwData)
    {
        var info = new NativeMethods.MONITORINFOEX();
        info.cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(info);

        if (!NativeMethods.GetMonitorInfo(hMonitor, ref info))
        {
            Log.Warning("GetMonitorInfo failed for monitor {Index}", _currentIndex);
            return true;
        }

        var (dpiX, dpiY) = DpiHelper.GetDpiForMonitor(hMonitor);
        double scale = DpiHelper.GetScaleForDpi(dpiX);

        var physBounds = new System.Drawing.Rectangle(
            info.rcMonitor.Left,
            info.rcMonitor.Top,
            info.rcMonitor.Width,
            info.rcMonitor.Height);

        // Convert physical pixel bounds to logical WPF units
        var logicalBounds = new Rect(
            info.rcMonitor.Left / scale,
            info.rcMonitor.Top / scale,
            info.rcMonitor.Width / scale,
            info.rcMonitor.Height / scale);

        _currentMonitors!.Add(new MonitorInfo
        {
            Handle = hMonitor,
            Bounds = logicalBounds,
            PhysicalBounds = physBounds,
            DpiX = dpiX,
            DpiY = dpiY,
            DpiScale = scale,
            IsPrimary = (info.dwFlags & 0x1) != 0,
            Index = _currentIndex
        });

        _currentIndex++;
        return true;
    }
}
