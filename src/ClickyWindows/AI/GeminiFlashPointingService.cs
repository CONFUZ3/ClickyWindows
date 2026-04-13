using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClickyWindows.Screen;
using Serilog;

namespace ClickyWindows.AI;

/// <summary>
/// Calls the Gemini Flash REST API (generateContent) to determine whether the user's
/// question requires pointing at a UI element, and if so, returns its physical pixel coordinates.
///
/// This runs in parallel with the Gemini Live audio session. The Live model handles
/// speech recognition, conversation, and TTS; this service handles visual grounding
/// independently using a model optimized for vision tasks rather than real-time audio.
/// </summary>
public class GeminiFlashPointingService
{
    private readonly string _apiKey;
    private readonly string _modelName;

    // Reuse a single HttpClient across all calls — avoids port exhaustion and respects keep-alive.
    private readonly HttpClient _httpClient = new();

    public GeminiFlashPointingService(string apiKey, string modelName)
    {
        _apiKey = apiKey;
        _modelName = modelName;
    }

    /// <summary>
    /// Given a screenshot and the user's transcribed question, asks Gemini Flash whether
    /// a UI element should be pointed at. Returns a <see cref="PointTarget"/> in physical
    /// pixel coords relative to the monitor's top-left, or null if no pointing is needed.
    /// </summary>
    public async Task<PointTarget?> GetPointAsync(
        ScreenCapture screenshot,
        string userTranscript,
        MonitorInfo monitor,
        CancellationToken token)
    {
        var prompt = BuildPrompt(userTranscript);

        var requestBody = new
        {
            contents = new[]
            {
                new
                {
                    role = "user",
                    parts = new object[]
                    {
                        new
                        {
                            inline_data = new
                            {
                                mime_type = "image/jpeg",
                                data = screenshot.Base64Jpeg
                            }
                        },
                        new { text = prompt }
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(requestBody);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        // e.g. https://generativelanguage.googleapis.com/v1beta/models/gemini-pro-latest:generateContent?key=...
        var url = $"https://generativelanguage.googleapis.com/v1beta/{_modelName}:generateContent?key={_apiKey}";

        Log.Debug("Sending Flash pointing request for transcript: {Transcript}", userTranscript);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.PostAsync(url, content, token);
        }
        catch (HttpRequestException ex)
        {
            Log.Warning(ex, "Flash pointing HTTP request failed");
            return null;
        }

        var responseBody = await response.Content.ReadAsStringAsync(token);

        if (!response.IsSuccessStatusCode)
        {
            Log.Warning("Flash pointing API returned {Status}: {Body}", response.StatusCode, responseBody);
            return null;
        }

        return ParseResponse(responseBody, screenshot, monitor);
    }

    private static string BuildPrompt(string userTranscript)
    {
        // Use string concatenation to avoid escaping conflicts between interpolation
        // braces and the literal JSON braces in the example object.
        // Gemini's vision model natively outputs coordinates in a 0-1000 normalized space
        // (not pixel coordinates). Asking for pixel values causes systematic inaccuracy
        // because the model maps its internal 0-1000 range to whatever bound it's given.
        // By matching Gemini's native format, coordinates are reliably convertible to
        // physical pixels via: physX = (x / 1000.0) * monitorPhysicalWidth.
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

    private static PointTarget? ParseResponse(string responseBody, ScreenCapture screenshot, MonitorInfo monitor)
    {
        try
        {
            var doc = JsonNode.Parse(responseBody);
            var text = doc?["candidates"]?[0]?["content"]?["parts"]?[0]?["text"]?.GetValue<string>()?.Trim();

            if (string.IsNullOrEmpty(text) || text.Equals("null", StringComparison.OrdinalIgnoreCase))
                return null;

            // First, try to parse the response text directly as JSON — this is the happy path
            // when the model correctly returns a bare JSON object with no surrounding text.
            // If that fails, fall back to extracting the first balanced {...} block, which
            // handles cases where the model wraps the JSON in markdown code fences.
            JsonNode? coordDoc = null;
            try
            {
                coordDoc = JsonNode.Parse(text);
            }
            catch (Exception)
            {
                var jsonSlice = ExtractFirstJsonObject(text);
                if (jsonSlice == null)
                {
                    Log.Debug("Flash pointing: no JSON object in response: {Text}", text);
                    return null;
                }
                coordDoc = JsonNode.Parse(jsonSlice);
            }

            if (coordDoc == null) return null;

            // Parse as double to handle both integer and fractional values Gemini may return.
            double normalizedX = coordDoc["x"]?.GetValue<double>() ?? -1;
            double normalizedY = coordDoc["y"]?.GetValue<double>() ?? -1;
            string label = coordDoc["label"]?.GetValue<string>() ?? "here";

            if (normalizedX < 0 || normalizedY < 0)
            {
                Log.Warning("Flash pointing: invalid coordinates in response: {Json}", coordDoc.ToJsonString());
                return null;
            }

            // Gemini returns 0-1000 normalized coordinates. Clamp before converting.
            normalizedX = Math.Clamp(normalizedX, 0, 1000);
            normalizedY = Math.Clamp(normalizedY, 0, 1000);

            // Convert from Gemini's 0-1000 normalized space to physical monitor pixels.
            // The normalization is relative to the full image extent, so we use the
            // monitor's physical dimensions directly — no need to go through the
            // downscaled screenshot dimensions.
            int physX = (int)Math.Round(normalizedX / 1000.0 * monitor.PhysicalBounds.Width);
            int physY = (int)Math.Round(normalizedY / 1000.0 * monitor.PhysicalBounds.Height);

            Log.Debug(
                "Flash pointing parsed: {Label} at normalized ({NX:F1},{NY:F1}) → physical ({PX},{PY})",
                label, normalizedX, normalizedY, physX, physY);

            return new PointTarget(physX, physY, label, monitor.Index);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to parse Flash pointing response: {Body}", responseBody);
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
