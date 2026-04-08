using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using ClickyWindows.Screen;
using Serilog;

namespace ClickyWindows.AI;

/// <summary>
/// Sends requests directly to the Anthropic Messages API and parses SSE streaming responses.
/// </summary>
public class ClaudeService
{
    private readonly HttpClient _httpClient;
    private readonly AppSettings _settings;

    private const string AnthropicEndpoint = "https://api.anthropic.com/v1/messages";

    public ClaudeService(HttpClient httpClient, AppSettings settings)
    {
        _httpClient = httpClient;
        _settings = settings;
    }

    public async Task<ClaudeResponseResult> GetResponseAsync(
        string userMessage,
        ConversationHistory history,
        IReadOnlyList<(string Base64Jpeg, int Width, int Height, int MonitorIndex, bool IsFocus)> screenshots,
        Func<string, CancellationToken, ValueTask>? onTextChunk = null,
        CancellationToken cancellationToken = default)
    {
        var messages = BuildMessages(userMessage, history, screenshots);
        var requestBody = JsonSerializer.Serialize(new
        {
            model = _settings.Claude.Model,
            max_tokens = _settings.Claude.MaxTokens,
            stream = true,
            system = BuildSystemPrompt(screenshots.Count),
            messages
        });

        Log.Debug("Claude request: {Chars} chars", requestBody.Length);

        var attempts = Math.Max(1, _settings.Claude.RetryCount + 1);
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestCts.CancelAfter(TimeSpan.FromSeconds(_settings.Claude.RequestTimeoutSeconds));

            using var request = BuildRequest(requestBody);
            try
            {
                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    requestCts.Token);

                if (!response.IsSuccessStatusCode)
                {
                    var failure = await BuildHttpFailureAsync(response, cancellationToken);
                    if (ShouldRetry(failure, attempt, attempts))
                    {
                        await DelayBeforeRetryAsync(response, attempt, cancellationToken);
                        continue;
                    }

                    Log.Warning("Claude request failed with status {StatusCode}: {Message}", failure.StatusCode, failure.Message);
                    return new ClaudeResponseResult(ClaudeResponseKind.Failed, string.Empty, false, failure);
                }

                var result = await ReadStreamAsync(response, onTextChunk, cancellationToken);
                if (result.Failure is { IsRetryable: true } && string.IsNullOrEmpty(result.Text) && attempt < attempts)
                {
                    await DelayBeforeRetryAsync(response, attempt, cancellationToken);
                    continue;
                }

                return result;
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                var failure = new ServiceFailure(ServiceFailureKind.Timeout, "Claude request timed out.", IsRetryable: true, Exception: ex);
                if (ShouldRetry(failure, attempt, attempts))
                {
                    await DelayBeforeRetryAsync(null, attempt, cancellationToken);
                    continue;
                }

                return new ClaudeResponseResult(ClaudeResponseKind.Failed, string.Empty, false, failure);
            }
            catch (HttpRequestException ex)
            {
                var failure = new ServiceFailure(ServiceFailureKind.Network, "Claude request failed to reach Anthropic.", IsRetryable: true, Exception: ex);
                if (ShouldRetry(failure, attempt, attempts))
                {
                    await DelayBeforeRetryAsync(null, attempt, cancellationToken);
                    continue;
                }

                return new ClaudeResponseResult(ClaudeResponseKind.Failed, string.Empty, false, failure);
            }
            catch (OperationCanceledException)
            {
                return new ClaudeResponseResult(
                    ClaudeResponseKind.Cancelled,
                    string.Empty,
                    false,
                    new ServiceFailure(ServiceFailureKind.Cancelled, "Claude request cancelled."));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Claude request failed");
                return new ClaudeResponseResult(
                    ClaudeResponseKind.Failed,
                    string.Empty,
                    false,
                    new ServiceFailure(ServiceFailureKind.Unknown, "Claude request failed unexpectedly.", Exception: ex));
            }
        }

        return new ClaudeResponseResult(
            ClaudeResponseKind.Failed,
            string.Empty,
            false,
            new ServiceFailure(ServiceFailureKind.Unknown, "Claude request exhausted all retries."));
    }

    private HttpRequestMessage BuildRequest(string requestBody)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, AnthropicEndpoint);
        request.Headers.Add("x-api-key", CredentialStore.GetKey(CredentialStore.AnthropicTarget));
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");
        return request;
    }

    private async Task<ClaudeResponseResult> ReadStreamAsync(
        HttpResponseMessage response,
        Func<string, CancellationToken, ValueTask>? onTextChunk,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        var text = new StringBuilder();
        var dataLines = new List<string>();
        var currentEvent = "message";
        var sawMessageStop = false;

        while (true)
        {
            string? line;
            try
            {
                line = await reader
                    .ReadLineAsync(cancellationToken)
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(_settings.Claude.StreamIdleTimeoutSeconds), cancellationToken);
            }
            catch (TimeoutException ex)
            {
                return new ClaudeResponseResult(
                    ClaudeResponseKind.Failed,
                    text.ToString(),
                    sawMessageStop,
                    new ServiceFailure(ServiceFailureKind.Timeout, "Claude stream went idle before completion.", IsRetryable: true, Exception: ex));
            }

            if (line == null)
            {
                break;
            }

            if (line.Length == 0)
            {
                var eventResult = await ProcessEventAsync(currentEvent, dataLines, text, onTextChunk, cancellationToken);
                dataLines.Clear();
                currentEvent = "message";

                if (eventResult is { Failure: not null } || eventResult?.SawMessageStop == true)
                {
                    if (eventResult.SawMessageStop)
                    {
                        sawMessageStop = true;
                        break;
                    }

                    return new ClaudeResponseResult(
                        ClaudeResponseKind.Failed,
                        text.ToString(),
                        sawMessageStop,
                        eventResult.Failure);
                }

                continue;
            }

            if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
            {
                currentEvent = line[6..].Trim();
                continue;
            }

            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                dataLines.Add(line[5..].TrimStart());
            }
        }

        if (dataLines.Count > 0)
        {
            var trailingEvent = await ProcessEventAsync(currentEvent, dataLines, text, onTextChunk, cancellationToken);
            if (trailingEvent is { Failure: not null })
            {
                return new ClaudeResponseResult(ClaudeResponseKind.Failed, text.ToString(), sawMessageStop, trailingEvent.Failure);
            }

            sawMessageStop = trailingEvent?.SawMessageStop == true || sawMessageStop;
        }

        var finalText = text.ToString();
        if (sawMessageStop)
        {
            return string.IsNullOrWhiteSpace(finalText)
                ? new ClaudeResponseResult(ClaudeResponseKind.Empty, finalText, true)
                : new ClaudeResponseResult(ClaudeResponseKind.Success, finalText, true);
        }

        return new ClaudeResponseResult(
            ClaudeResponseKind.Incomplete,
            finalText,
            false,
            new ServiceFailure(ServiceFailureKind.Upstream, "Claude stream ended before message_stop.", IsRetryable: string.IsNullOrWhiteSpace(finalText)));
    }

    private async Task<SseEventResult?> ProcessEventAsync(
        string eventType,
        List<string> dataLines,
        StringBuilder text,
        Func<string, CancellationToken, ValueTask>? onTextChunk,
        CancellationToken cancellationToken)
    {
        if (dataLines.Count == 0)
        {
            return null;
        }

        var data = string.Join("\n", dataLines);
        if (data == "[DONE]")
        {
            return new SseEventResult(SawMessageStop: true);
        }

        if (string.Equals(eventType, "ping", StringComparison.OrdinalIgnoreCase))
        {
            Log.Debug("Claude SSE ping");
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : eventType;

            if (string.Equals(type, "message_stop", StringComparison.Ordinal))
            {
                return new SseEventResult(SawMessageStop: true);
            }

            if (string.Equals(type, "error", StringComparison.Ordinal) || string.Equals(eventType, "error", StringComparison.OrdinalIgnoreCase))
            {
                return new SseEventResult(Failure: ParseAnthropicError(root));
            }

            if (string.Equals(type, "content_block_delta", StringComparison.Ordinal) &&
                root.TryGetProperty("delta", out var delta) &&
                delta.TryGetProperty("type", out var deltaType) &&
                string.Equals(deltaType.GetString(), "text_delta", StringComparison.Ordinal) &&
                delta.TryGetProperty("text", out var textElement))
            {
                var chunk = textElement.GetString();
                if (!string.IsNullOrEmpty(chunk))
                {
                    text.Append(chunk);
                    if (onTextChunk != null)
                    {
                        await onTextChunk(chunk, cancellationToken);
                    }
                }
            }

            return null;
        }
        catch (JsonException ex)
        {
            return new SseEventResult(
                Failure: new ServiceFailure(ServiceFailureKind.Upstream, "Claude stream returned invalid JSON.", IsRetryable: text.Length == 0, Exception: ex));
        }
    }

    private async Task<ServiceFailure> BuildHttpFailureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string responseBody;
        try
        {
            responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch
        {
            responseBody = string.Empty;
        }

        var statusCode = (int)response.StatusCode;
        var message = $"Anthropic returned HTTP {statusCode}.";
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.TryGetProperty("error", out var error) &&
                error.TryGetProperty("message", out var errorMessage))
            {
                message = errorMessage.GetString() ?? message;
            }
        }
        catch
        {
            if (!string.IsNullOrWhiteSpace(responseBody))
            {
                message = responseBody.Trim();
            }
        }

        var kind = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ServiceFailureKind.Authentication,
            HttpStatusCode.TooManyRequests => ServiceFailureKind.RateLimited,
            HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => ServiceFailureKind.Timeout,
            HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.UnprocessableContent => ServiceFailureKind.InvalidRequest,
            _ when statusCode >= 500 => ServiceFailureKind.Upstream,
            _ => ServiceFailureKind.Unknown
        };

        if (kind == ServiceFailureKind.Authentication)
        {
            message = "Anthropic authentication failed. Check the stored API key for this machine.";
        }
        else if (kind == ServiceFailureKind.RateLimited)
        {
            message = "Anthropic rate limited the request. The app will retry when allowed.";
        }

        var retryable = response.StatusCode == HttpStatusCode.TooManyRequests || statusCode >= 500;
        return new ServiceFailure(kind, message, statusCode, retryable);
    }

    private static ServiceFailure ParseAnthropicError(JsonElement root)
    {
        if (root.TryGetProperty("error", out var errorElement))
        {
            var type = errorElement.TryGetProperty("type", out var typeElement)
                ? typeElement.GetString()
                : null;
            var message = errorElement.TryGetProperty("message", out var messageElement)
                ? messageElement.GetString() ?? "Claude returned an error event."
                : "Claude returned an error event.";

            return type switch
            {
                "rate_limit_error" => new ServiceFailure(ServiceFailureKind.RateLimited, message, IsRetryable: true),
                "overloaded_error" => new ServiceFailure(ServiceFailureKind.Upstream, message, IsRetryable: true),
                "authentication_error" or "permission_error" => new ServiceFailure(ServiceFailureKind.Authentication, message),
                "invalid_request_error" => new ServiceFailure(ServiceFailureKind.InvalidRequest, message),
                _ => new ServiceFailure(ServiceFailureKind.Upstream, message)
            };
        }

        return new ServiceFailure(ServiceFailureKind.Upstream, "Claude returned an unknown error event.");
    }

    private static bool ShouldRetry(ServiceFailure failure, int attempt, int maxAttempts) =>
        failure.IsRetryable && attempt < maxAttempts;

    private async Task DelayBeforeRetryAsync(HttpResponseMessage? response, int attempt, CancellationToken cancellationToken)
    {
        var retryDelay = response?.Headers.RetryAfter?.Delta;
        if (retryDelay == null && response?.Headers.RetryAfter?.Date is DateTimeOffset retryAt)
        {
            retryDelay = retryAt - DateTimeOffset.UtcNow;
        }

        var delay = retryDelay.GetValueOrDefault(TimeSpan.FromMilliseconds(_settings.Claude.RetryBaseDelayMs * attempt));
        if (delay < TimeSpan.Zero)
        {
            delay = TimeSpan.Zero;
        }

        await Task.Delay(delay, cancellationToken);
    }

    private sealed record SseEventResult(bool SawMessageStop = false, ServiceFailure? Failure = null);

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
