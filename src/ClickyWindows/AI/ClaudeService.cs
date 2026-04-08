using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using ClickyWindows.Screen;
using Serilog;

namespace ClickyWindows.AI;

/// <summary>
/// Sends requests to the Cloudflare Worker proxy's /chat endpoint.
/// Parses SSE streaming response from Claude.
/// </summary>
public class ClaudeService
{
    private readonly ProxyClient _proxy;
    private readonly AppSettings _settings;

    public ClaudeService(ProxyClient proxy, AppSettings settings)
    {
        _proxy = proxy;
        _settings = settings;
    }

    /// <summary>
    /// Stream Claude's response. Yields text chunks as they arrive.
    /// </summary>
    public async IAsyncEnumerable<string> StreamResponseAsync(
        string userMessage,
        ConversationHistory history,
        IReadOnlyList<(string Base64Jpeg, int Width, int Height, int MonitorIndex, bool IsFocus)> screenshots,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var messages = BuildMessages(userMessage, history, screenshots);

        var requestBody = JsonSerializer.Serialize(new
        {
            model = _settings.Claude.Model,
            max_tokens = 1024,
            stream = true,
            system = BuildSystemPrompt(screenshots.Count),
            messages
        });

        Log.Debug("Claude request: {Chars} chars", requestBody.Length);

        var content = new StringContent(requestBody, Encoding.UTF8, "application/json");
        HttpResponseMessage response;

        try
        {
            response = await _proxy.PostAsync("/chat", content,
                timeout: TimeSpan.FromSeconds(65),
                cancellationToken: cancellationToken);

            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Claude request failed");
            yield break;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new System.IO.StreamReader(stream);

        // SSE line-buffered parsing (handles TCP packet boundary splits)
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
        {
            if (!line.StartsWith("data: ")) continue;

            var data = line[6..];
            if (data == "[DONE]") break;

            string? chunk = TryExtractTextDelta(data);
            if (chunk != null)
                yield return chunk;
        }
    }

    private static string? TryExtractTextDelta(string data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;

            // Claude SSE format: {"type":"content_block_delta","delta":{"type":"text_delta","text":"..."}}
            if (!root.TryGetProperty("type", out var type)) return null;
            if (type.GetString() != "content_block_delta") return null;

            if (!root.TryGetProperty("delta", out var delta)) return null;
            if (!delta.TryGetProperty("text", out var text)) return null;

            return text.GetString();
        }
        catch
        {
            return null;
        }
    }

    private static string BuildSystemPrompt(int screenshotCount)
    {
        return """
            You are Clicky, an AI companion that lives next to the user's cursor on their screen.
            You can see their screen and help them with anything they're looking at.

            When referencing specific UI elements or locations on screen, use this exact format:
            [POINT:x,y:label:screenN]
            Where x,y are pixel coordinates in the screenshot, label is a short description, and N is the screen index (0-based).

            Keep responses concise and conversational. You're a helpful companion, not a formal assistant.
            """;
    }

    private static List<object> BuildMessages(
        string userMessage,
        ConversationHistory history,
        IReadOnlyList<(string Base64Jpeg, int Width, int Height, int MonitorIndex, bool IsFocus)> screenshots)
    {
        var messages = new List<object>();

        // Add text-only history (no screenshots in history)
        foreach (var turn in history.GetHistory())
        {
            messages.Add(new
            {
                role = turn.Role,
                content = turn.Content
            });
        }

        // Build current user message with screenshots
        var contentParts = new List<object>();

        foreach (var (base64, width, height, monitorIndex, isFocus) in screenshots)
        {
            contentParts.Add(new
            {
                type = "image",
                source = new
                {
                    type = "base64",
                    media_type = "image/jpeg",
                    data = base64
                }
            });
            contentParts.Add(new
            {
                type = "text",
                text = $"[Screen {monitorIndex} - {width}x{height}{(isFocus ? " - primary focus" : "")}]"
            });
        }

        contentParts.Add(new { type = "text", text = userMessage });

        messages.Add(new { role = "user", content = contentParts });
        return messages;
    }
}
