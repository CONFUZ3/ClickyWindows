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
public class GeminiFlashPointingService : IPointingService
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
        var prompt = PointingHelper.BuildPrompt(userTranscript);

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

        return ParseResponse(responseBody, monitor);
    }

    private static PointTarget? ParseResponse(string responseBody, MonitorInfo monitor)
    {
        try
        {
            var doc = JsonNode.Parse(responseBody);
            var text = doc?["candidates"]?[0]?["content"]?["parts"]?[0]?["text"]?.GetValue<string>();
            return PointingHelper.ParseModelText(text, monitor);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to parse Flash pointing response: {Body}", responseBody);
            return null;
        }
    }
}
