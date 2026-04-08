using System.Windows;

namespace ClickyWindows.Screen;

public class MonitorInfo
{
    /// <summary>Logical bounds in WPF units (96 DPI baseline), virtual desktop space.</summary>
    public Rect Bounds { get; init; }

    /// <summary>Physical bounds in raw pixels.</summary>
    public System.Drawing.Rectangle PhysicalBounds { get; init; }

    public double DpiScale { get; init; }
    public uint DpiX { get; init; }
    public uint DpiY { get; init; }
    public bool IsPrimary { get; init; }
    public int Index { get; init; }
    public IntPtr Handle { get; init; }
}
