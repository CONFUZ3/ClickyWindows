using System.Text.RegularExpressions;
using ClickyWindows.Screen;
using Serilog;

namespace ClickyWindows.AI;

public record PointTarget(int X, int Y, string Label, int ScreenIndex);

/// <summary>
/// Parses [POINT:x,y:label:screenN] tags from Claude's response text.
/// Validates coordinates within monitor bounds and clamps if out of range.
/// </summary>
public static class PointParser
{
    // [POINT:x,y:label:screenN]
    private static readonly Regex PointRegex = new(
        @"\[POINT:(\d+),(\d+):([^:\]]+):screen(\d+)\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static List<PointTarget> ParseAll(string text, IReadOnlyList<MonitorInfo> monitors)
    {
        var results = new List<PointTarget>();

        foreach (Match match in PointRegex.Matches(text))
        {
            if (!int.TryParse(match.Groups[1].Value, out int x) ||
                !int.TryParse(match.Groups[2].Value, out int y) ||
                !int.TryParse(match.Groups[4].Value, out int screenIndex))
            {
                Log.Warning("Failed to parse POINT tag: {Tag}", match.Value);
                continue;
            }

            string label = match.Groups[3].Value;

            // Clamp to valid screen
            screenIndex = Math.Clamp(screenIndex, 0, Math.Max(0, monitors.Count - 1));
            var monitor = monitors[screenIndex];

            // Validate and clamp coordinates within physical monitor bounds
            int physWidth = monitor.PhysicalBounds.Width;
            int physHeight = monitor.PhysicalBounds.Height;

            if (x < 0 || y < 0 || x > physWidth || y > physHeight)
            {
                Log.Warning("POINT {Label} ({X},{Y}) out of bounds for monitor {Screen} ({W}x{H}), clamping",
                    label, x, y, screenIndex, physWidth, physHeight);
                x = Math.Clamp(x, 0, physWidth);
                y = Math.Clamp(y, 0, physHeight);
            }

            results.Add(new PointTarget(x, y, label, screenIndex));
            Log.Debug("Parsed POINT: {Label} at ({X},{Y}) on screen {Screen}", label, x, y, screenIndex);
        }

        return results;
    }
}
