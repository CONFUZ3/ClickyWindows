using System.Text.Json.Nodes;
using ClickyWindows.Screen;
using Serilog;

namespace ClickyWindows.AI;

/// <summary>
/// Provider-neutral pieces of the pointing flow: the prompt asking a vision model for a UI
/// element's location, and parsing the model's reply (normalized 0-1000 coordinates) into a
/// physical-pixel <see cref="PointTarget"/>. Shared by <see cref="GeminiFlashPointingService"/>
/// and <see cref="OpenAiPointingService"/> so both behave identically.
/// </summary>
public static class PointingHelper
{
    public static string BuildPrompt(string userTranscript)
    {
        // Use string concatenation to avoid escaping conflicts between interpolation
        // braces and the literal JSON braces in the example object.
        // Vision models map their internal 0-1000 normalized coordinate space to whatever bound
        // they're given. Asking for pixel values causes systematic inaccuracy; matching the native
        // 0-1000 range lets us convert reliably via: physX = (x / 1000.0) * monitorPhysicalWidth.
        return
            $"The user asked: \"{userTranscript}\"\n\n" +
            "Look at the screenshot carefully.\n\n" +
            "If the user is asking you to locate, point at, click, find, or navigate to a specific\n" +
            "UI element, and you can clearly and confidently see that element in the screenshot,\n" +
            "respond with ONLY a raw JSON object (no markdown, no code fences):\n" +
            "{\"x\": <number>, \"y\": <number>, \"label\": \"<short label>\"}\n\n" +
            "Use normalized coordinates where x=0 is the LEFT edge, x=1000 is the RIGHT\n" +
            "edge, y=0 is the TOP edge, and y=1000 is the BOTTOM edge of the screenshot.\n\n" +
            "IMPORTANT: Only return coordinates if the element is actually visible and you are\n" +
            "certain of its location. Do NOT guess or approximate based on where it usually appears.\n" +
            "If the element is not visible, or the question is general conversation, respond with exactly: null";
    }

    /// <summary>
    /// Parses the model's raw text reply (expected: a bare {"x","y","label"} object in 0-1000
    /// normalized space, possibly wrapped in code fences, or the literal "null") into a
    /// <see cref="PointTarget"/> in physical pixels relative to the monitor's top-left.
    /// </summary>
    public static PointTarget? ParseModelText(string? text, MonitorInfo monitor)
    {
        try
        {
            text = text?.Trim();
            if (string.IsNullOrEmpty(text) || text.Equals("null", StringComparison.OrdinalIgnoreCase))
                return null;

            // First, try to parse the response text directly as JSON — this is the happy path
            // when the model correctly returns a bare JSON object with no surrounding text.
            // If that fails, fall back to extracting the first balanced {...} block, which
            // handles cases where the model wraps the JSON in markdown code fences.
            JsonNode? coordDoc;
            try
            {
                coordDoc = JsonNode.Parse(text);
            }
            catch (Exception)
            {
                var jsonSlice = ExtractFirstJsonObject(text);
                if (jsonSlice == null)
                {
                    Log.Debug("Pointing: no JSON object in response: {Text}", text);
                    return null;
                }
                coordDoc = JsonNode.Parse(jsonSlice);
            }

            if (coordDoc == null) return null;

            // Parse as double to handle both integer and fractional values the model may return.
            double normalizedX = coordDoc["x"]?.GetValue<double>() ?? -1;
            double normalizedY = coordDoc["y"]?.GetValue<double>() ?? -1;
            string label = coordDoc["label"]?.GetValue<string>() ?? "here";

            if (normalizedX < 0 || normalizedY < 0)
            {
                Log.Warning("Pointing: invalid coordinates in response: {Json}", coordDoc.ToJsonString());
                return null;
            }

            normalizedX = Math.Clamp(normalizedX, 0, 1000);
            normalizedY = Math.Clamp(normalizedY, 0, 1000);

            // Convert from 0-1000 normalized space to physical monitor pixels. The normalization is
            // relative to the full image extent, so we use the monitor's physical dimensions directly.
            int physX = (int)Math.Round(normalizedX / 1000.0 * monitor.PhysicalBounds.Width);
            int physY = (int)Math.Round(normalizedY / 1000.0 * monitor.PhysicalBounds.Height);

            Log.Debug(
                "Pointing parsed: {Label} at normalized ({NX:F1},{NY:F1}) → physical ({PX},{PY})",
                label, normalizedX, normalizedY, physX, physY);

            return new PointTarget(physX, physY, label, monitor.Index);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to parse pointing response text: {Text}", text);
            return null;
        }
    }

    /// <summary>
    /// Finds the first balanced {...} block in <paramref name="text"/> by tracking brace depth,
    /// rather than using LastIndexOf('}') which would include trailing text after the object.
    /// Returns null if no balanced block is found.
    /// </summary>
    private static string? ExtractFirstJsonObject(string text)
    {
        int braceStart = text.IndexOf('{');
        if (braceStart < 0) return null;

        int depth = 0;
        for (int i = braceStart; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0)
                    return text[braceStart..(i + 1)];
            }
        }

        // Unbalanced braces — not a valid JSON object.
        return null;
    }
}
