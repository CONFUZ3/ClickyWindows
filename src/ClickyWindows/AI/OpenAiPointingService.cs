using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClickyWindows.Screen;
using Serilog;

namespace ClickyWindows.AI;

/// <summary>
/// Pointing provider for OpenAI. Calls the Chat Completions API with the screenshot and the user's
/// transcript using a vision-capable model, and parses the reply into physical pixel coordinates.
/// The OpenAI counterpart to <see cref="GeminiFlashPointingService"/>; both share
/// <see cref="PointingHelper"/> for the prompt and coordinate parsing.
/// </summary>
public class OpenAiPointingService : IPointingService
{
    private readonly string _apiKey;
    private readonly string _modelName;

    // Reuse a single HttpClient across all calls — avoids port exhaustion and respects keep-alive.
    private readonly HttpClient _httpClient = new();

    public OpenAiPointingService(string apiKey, string modelName)
    {
        _apiKey = apiKey;
        _modelName = modelName;
    }

    public async Task<PointTarget?> GetPointAsync(
        ScreenCapture screenshot,
        string userTranscript,
        MonitorInfo monitor,
        CancellationToken token)
    {
        var prompt = PointingHelper.BuildPrompt(userTranscript);

        var requestBody = new
        {
            model = _modelName,
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = prompt },
                        new
                        {
                            type = "image_url",
                            image_url = new { url = $"data:image/jpeg;base64,{screenshot.Base64Jpeg}" }
                        }
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(requestBody);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
        {
            Content = content
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        Log.Debug("Sending OpenAI pointing request for transcript: {Transcript}", userTranscript);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, token);
        }
        catch (HttpRequestException ex)
        {
            Log.Warning(ex, "OpenAI pointing HTTP request failed");
            return null;
        }

        var responseBody = await response.Content.ReadAsStringAsync(token);

        if (!response.IsSuccessStatusCode)
        {
            Log.Warning("OpenAI pointing API returned {Status}: {Body}", response.StatusCode, responseBody);
            return null;
        }

        return ParseResponse(responseBody, monitor);
    }

    private static PointTarget? ParseResponse(string responseBody, MonitorInfo monitor)
    {
        try
        {
            var doc = JsonNode.Parse(responseBody);
            var text = doc?["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
            return PointingHelper.ParseModelText(text, monitor);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to parse OpenAI pointing response: {Body}", responseBody);
            return null;
        }
    }
}
