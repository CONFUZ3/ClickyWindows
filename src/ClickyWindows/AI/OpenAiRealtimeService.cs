using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClickyWindows.Screen;
using Serilog;

namespace ClickyWindows.AI;

/// <summary>
/// Realtime voice provider backed by the OpenAI Realtime API (GA) over a raw WebSocket.
/// Mirrors <see cref="GeminiLiveService"/>'s lifecycle (setup gate → bidirectional streaming →
/// graceful teardown) so <see cref="PushToTalkController"/> drives both identically.
///
/// Push-to-talk maps cleanly onto manual turn control: server VAD is disabled, microphone PCM is
/// appended while the key is held, and <see cref="CompleteTurnAsync"/> commits the buffer and asks
/// for a response on key release — the deterministic equivalent of Gemini's audioStreamEnd.
///
/// Protocol reference: https://developers.openai.com/api/docs/guides/realtime
/// </summary>
public class OpenAiRealtimeService : IRealtimeVoiceService
{
    private readonly string _apiKey;
    private readonly OpenAiSettings _settings;
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _connectionCts;
    private Task? _receiveTask;
    private TaskCompletionSource? _sessionReadyTcs;

    public event Action<byte[]>? AudioReceived;
    public event Action<string>? TextChunkReceived;
    public event Action<string>? TextCompleted;
    public event Action<string>? InputTranscriptionReceived;
    public event Action<Exception>? ErrorOccurred;
    public event Action? TurnComplete;

    private readonly StringBuilder _outputTranscriptBuffer = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public OpenAiRealtimeService(string apiKey, OpenAiSettings settings)
    {
        _apiKey = apiKey;
        _settings = settings;
    }

    public bool IsConnected => _webSocket?.State == WebSocketState.Open;

    // OpenAI Realtime streams audio as pcm16 24kHz mono in both directions.
    public int InputSampleRateHz => 24000;

    public async Task ConnectAsync(IReadOnlyList<ConversationHistory.Turn> history,
                                   IReadOnlyList<MonitorInfo> monitors,
                                   CancellationToken token)
    {
        if (IsConnected) return;

        if (_connectionCts != null)
        {
            try { _connectionCts.Cancel(); } catch { }
            try { _connectionCts.Dispose(); } catch { }
            _connectionCts = null;
        }
        _connectionCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        _webSocket = new ClientWebSocket();

        // GA auth is a plain bearer token. The old "OpenAI-Beta: realtime=v1" header belongs to the
        // beta interface; the GA endpoint rejects/ignores it. If a future snapshot needs it again,
        // add it here.
        _webSocket.Options.SetRequestHeader("Authorization", $"Bearer {_apiKey}");

        var uri = new Uri($"wss://api.openai.com/v1/realtime?model={_settings.Model}");

        Log.Information("Connecting to OpenAI Realtime API...");
        await _webSocket.ConnectAsync(uri, _connectionCts.Token);
        Log.Information("Connected to OpenAI Realtime API network transport");
        _sessionReadyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Start receiving before sending session.update so the session.updated ack isn't missed.
        _receiveTask = Task.Run(() => ReceiveLoopAsync(_connectionCts.Token), _connectionCts.Token);

        var systemInstructionText = SystemInstructionBuilder.Build(history, monitors);
        Log.Debug("System instruction length: {Len} chars, history turns: {Turns}", systemInstructionText.Length, history.Count);

        // turn_detection = null disables server VAD so the push-to-talk key controls turn boundaries.
        // transcription enables conversation.item.input_audio_transcription.* events, which drive the
        // separate pointing call (the controller fires pointing on InputTranscriptionReceived).
        var sessionUpdate = new
        {
            type = "session.update",
            session = new
            {
                type = "realtime",
                instructions = systemInstructionText,
                // Audio only: the spoken transcript still arrives via response.output_audio_transcript.*.
                // Enabling "text" too makes the model emit a duplicate text stream for the same content.
                output_modalities = new[] { "audio" },
                audio = new
                {
                    input = new
                    {
                        format = new { type = "audio/pcm", rate = 24000 },
                        turn_detection = (object?)null,
                        transcription = new { model = "gpt-4o-mini-transcribe" }
                    },
                    output = new
                    {
                        format = new { type = "audio/pcm", rate = 24000 },
                        voice = _settings.VoiceName
                    }
                }
            }
        };

        await SendJsonAsync(sessionUpdate, _connectionCts.Token);

        using var reg = token.Register(() => _sessionReadyTcs.TrySetCanceled());
        try
        {
            await Task.WhenAny(_sessionReadyTcs.Task, Task.Delay(_settings.ConnectTimeoutMs, token));
            if (!_sessionReadyTcs.Task.IsCompleted)
            {
                throw new TimeoutException("Timed out waiting for OpenAI session.updated");
            }
            await _sessionReadyTcs.Task; // throw if fault/cancelled
            Log.Information("OpenAI Realtime session ready");
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public async Task SendAudioAsync(byte[] pcmData, CancellationToken token)
    {
        if (!IsConnected) return;

        var msg = new
        {
            type = "input_audio_buffer.append",
            audio = Convert.ToBase64String(pcmData)
        };

        await SendJsonAsync(msg, token);
    }

    public async Task SendScreenshotAsync(string base64Jpeg, CancellationToken token)
    {
        if (!IsConnected) return;

        // Add the screenshot as a user image item so it's part of the conversation context the
        // model reasons over when it builds the response on CompleteTurnAsync.
        var msg = new
        {
            type = "conversation.item.create",
            item = new
            {
                type = "message",
                role = "user",
                content = new object[]
                {
                    new
                    {
                        type = "input_image",
                        image_url = $"data:image/jpeg;base64,{base64Jpeg}"
                    }
                }
            }
        };

        await SendJsonAsync(msg, token);
    }

    public async Task CompleteTurnAsync(CancellationToken token)
    {
        if (!IsConnected) return;

        // With server VAD off, commit the captured audio (which also triggers input transcription)
        // and explicitly request the model's response.
        await SendJsonAsync(new { type = "input_audio_buffer.commit" }, token);
        await SendJsonAsync(new { type = "response.create" }, token);
    }

    private async Task SendJsonAsync(object payload, CancellationToken token)
    {
        if (!IsConnected) return;
        await _sendLock.WaitAsync(token);
        try
        {
            if (!IsConnected) return;
            var json = JsonSerializer.Serialize(payload);
            var bytes = Encoding.UTF8.GetBytes(json);
            await _webSocket!.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);
        }
        finally { _sendLock.Release(); }
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        var buffer = new byte[65536];
        var messageBuilder = new List<byte>();

        try
        {
            while (IsConnected && !token.IsCancellationRequested)
            {
                var result = await _webSocket!.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Log.Warning("OpenAI WebSocket closed: {Status} — {Desc}", result.CloseStatus, result.CloseStatusDescription);
                    HandleTransportFailure(new Exception($"WebSocket closed: {result.CloseStatusDescription}"));
                    break;
                }

                messageBuilder.AddRange(buffer.Take(result.Count));

                if (result.EndOfMessage)
                {
                    var messageText = Encoding.UTF8.GetString(messageBuilder.ToArray());
                    messageBuilder.Clear();
                    ProcessMessage(messageText);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in OpenAI receive loop");
            HandleTransportFailure(ex);
        }
    }

    // Before the session is ready, fail the connect gate so ConnectAsync throws and the controller's
    // catch owns the single user-facing message. After it's ready, surface it so the live turn aborts.
    private void HandleTransportFailure(Exception ex)
    {
        if (_sessionReadyTcs?.Task.IsCompleted != true)
            _sessionReadyTcs?.TrySetException(ex);
        else
            ErrorOccurred?.Invoke(ex);
    }

    // OpenAI's error channel is chatty: it reports non-fatal turn-control conditions that must not
    // abort a live turn (e.g. committing a near-silent push-to-talk buffer).
    private static bool IsBenignError(string? code) =>
        code is "input_audio_buffer_commit_empty"
             or "input_audio_buffer_commit_too_small"
             or "response_cancel_not_active"
             or "conversation_already_has_active_response";

    private void ProcessMessage(string json)
    {
        try
        {
            var doc = JsonNode.Parse(json);
            var type = doc?["type"]?.GetValue<string>();
            if (type == null) return;

            switch (type)
            {
                case "session.updated":
                    // Our configuration (audio formats, transcription, voice) is now active.
                    _sessionReadyTcs?.TrySetResult();
                    break;

                case "error":
                    var errNode = doc!["error"];
                    var errText = errNode?.ToJsonString() ?? "unknown error";
                    var errCode = errNode?["code"]?.GetValue<string>();
                    Log.Error("OpenAI Realtime error: {Error}", errText);
                    if (_sessionReadyTcs?.Task.IsCompleted != true)
                        _sessionReadyTcs?.TrySetException(new Exception($"API Error: {errText}"));
                    else if (!IsBenignError(errCode))
                        ErrorOccurred?.Invoke(new Exception($"API Error: {errText}"));
                    break;

                case "response.output_audio.delta":
                    var audioBase64 = doc!["delta"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(audioBase64))
                        AudioReceived?.Invoke(Convert.FromBase64String(audioBase64));
                    break;

                case "response.output_audio_transcript.delta":
                    var textChunk = doc!["delta"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(textChunk))
                    {
                        _outputTranscriptBuffer.Append(textChunk);
                        TextChunkReceived?.Invoke(textChunk);
                    }
                    break;

                case "conversation.item.input_audio_transcription.completed":
                    var transcript = doc!["transcript"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(transcript))
                        InputTranscriptionReceived?.Invoke(transcript);
                    break;

                case "response.done":
                    var fullText = _outputTranscriptBuffer.ToString();
                    _outputTranscriptBuffer.Clear();
                    if (!string.IsNullOrWhiteSpace(fullText))
                        TextCompleted?.Invoke(fullText);
                    TurnComplete?.Invoke();
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to parse OpenAI response");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connectionCts != null)
        {
            try { _connectionCts.Cancel(); } catch { }
            try { _connectionCts.Dispose(); } catch { }
            _connectionCts = null;
        }

        if (_webSocket != null)
        {
            if (_webSocket.State == WebSocketState.Open || _webSocket.State == WebSocketState.CloseReceived)
            {
                try
                {
                    using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disposing", closeCts.Token);
                }
                catch { }
            }
            _webSocket.Dispose();
        }

        if (_receiveTask != null)
        {
            try { await _receiveTask; } catch { }
        }
    }
}
