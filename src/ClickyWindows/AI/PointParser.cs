namespace ClickyWindows.AI;

/// <summary>
/// A parsed POINT target. X and Y are physical pixel offsets relative to the monitor's top-left
/// (i.e., relative to monitor.PhysicalBounds.Left/Top, NOT absolute screen coords).
/// </summary>
public record PointTarget(int X, int Y, string Label, int ScreenIndex);
