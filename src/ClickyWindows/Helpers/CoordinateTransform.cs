using ClickyWindows.Screen;

namespace ClickyWindows.Helpers;

/// <summary>
/// Maps between coordinate spaces:
/// - Screenshot pixel coords (physical, per-monitor)
/// - Virtual desktop coords (logical WPF units from top-left of virtual desktop)
/// - Per-overlay-window local coords
/// </summary>
public static class CoordinateTransform
{
    /// <summary>
    /// Convert Claude [POINT] coords (screenshot pixel space) to virtual desktop logical coords.
    /// </summary>
    public static System.Windows.Point ScreenshotToVirtualDesktop(int screenshotX, int screenshotY, MonitorInfo monitor)
    {
        double logicalX = monitor.Bounds.Left + screenshotX / monitor.DpiScale;
        double logicalY = monitor.Bounds.Top + screenshotY / monitor.DpiScale;
        return new System.Windows.Point(logicalX, logicalY);
    }

    /// <summary>
    /// Convert virtual desktop logical coords to overlay-window local coords.
    /// </summary>
    public static System.Windows.Point VirtualDesktopToOverlay(System.Windows.Point virtualDesktopPoint, MonitorInfo monitor)
    {
        return new System.Windows.Point(
            virtualDesktopPoint.X - monitor.Bounds.Left,
            virtualDesktopPoint.Y - monitor.Bounds.Top);
    }

    /// <summary>
    /// Convert physical screen coords (from GetCursorPos) to overlay-window local coords.
    /// </summary>
    public static System.Windows.Point PhysicalToOverlayLocal(int physicalX, int physicalY, MonitorInfo monitor)
    {
        double logicalX = (physicalX - monitor.PhysicalBounds.Left) / monitor.DpiScale + monitor.Bounds.Left;
        double logicalY = (physicalY - monitor.PhysicalBounds.Top) / monitor.DpiScale + monitor.Bounds.Top;
        return new System.Windows.Point(logicalX - monitor.Bounds.Left, logicalY - monitor.Bounds.Top);
    }
}
