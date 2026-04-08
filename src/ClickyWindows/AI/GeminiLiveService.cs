using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Serilog;

namespace ClickyWindows.AI;

public class GeminiLiveService : IAsyncDisposable
{
    private readonly string _apiKey;
    private readonly GeminiSettings _settings;
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _connectionCts;
    private Task? _receiveTask;
    private TaskCompletionSource? _setupCompleteTcs;

    public event Action<byte[]>? AudioReceived;
    public event Action<string>? TextChunkReceived;
    public event Action<string>? TextCompleted;
    public event Action<string>? InputTranscriptionReceived;
    public event Action<Exception>? ErrorOccurred;
    public event Action? TurnComplete;

    private readonly StringBuilder _textBuffer = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public GeminiLiveService(string apiKey, GeminiSettings settings)
    {
        _apiKey = apiKey;
        _settings = settings;
    }

    public bool IsConnected => _webSocket?.State == WebSocketState.Open;

    public Task ConnectAsync(CancellationToken token)
        => ConnectAsync(Array.Empty<ConversationHistory.Turn>(), token);

    public async Task ConnectAsync(IReadOnlyList<ConversationHistory.Turn> history, CancellationToken token)
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

        var uri = new Uri($"wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent?key={_apiKey}");

        Log.Information("Connecting to Gemini Live API...");
        await _webSocket.ConnectAsync(uri, _connectionCts.Token);
        Log.Information("Connected to Gemini Live API network transport");
        _setupCompleteTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Embed prior conversation as a system instruction. We tried clientContent with
        // historyConfig.initialHistoryInClientContent, but Gemini 3.1 Flash Live closes
        // the socket with InvalidPayloadData regardless. systemInstruction is the
        // broadly-supported path for providing context to a Live session.
        var systemInstructionText = BuildSystemInstruction(history);

        var setupObj = new JsonObject
        {
            ["model"] = _settings.Model,
            ["generationConfig"] = new JsonObject
            {
                ["responseModalities"] = new JsonArray { "AUDIO" },
                ["speechConfig"] = new JsonObject
                {
                    ["voiceConfig"] = new JsonObject
                    {
                        ["prebuiltVoiceConfig"] = new JsonObject { ["voiceName"] = _settings.VoiceName }
                    }
                }
            },
            ["inputAudioTranscription"] = new JsonObject(),
            ["outputAudioTranscription"] = new JsonObject()
        };

        if (!string.IsNullOrEmpty(systemInstructionText))
        {
            setupObj["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray { new JsonObject { ["text"] = systemInstructionText } }
            };
        }

        var setupMsg = new JsonObject { ["setup"] = setupObj };
        var setupJson = setupMsg.ToJsonString();
        await _sendLock.WaitAsync(_connectionCts.Token);
        try
        {
            var bytes = Encoding.UTF8.GetBytes(setupJson);
            await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _connectionCts.Token);
        }
        finally { _sendLock.Release(); }

        _receiveTask = Task.Run(() => ReceiveLoopAsync(_connectionCts.Token), _connectionCts.Token);
        
        using var reg = token.Register(() => _setupCompleteTcs.TrySetCanceled());
        try 
        {
            await Task.WhenAny(_setupCompleteTcs.Task, Task.Delay(_settings.ConnectTimeoutMs, token));
            if (!_setupCompleteTcs.Task.IsCompleted)
            {
                throw new TimeoutException("Timed out waiting for Gemini setupComplete");
            }
            await _setupCompleteTcs.Task; // throw if fault/cancelled
            Log.Information("Gemini setup complete");
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

        var base64Audio = Convert.ToBase64String(pcmData);
        var msg = new
        {
            realtimeInput = new
            {
                audio = new
                {
                    mimeType = "audio/pcm;rate=16000",
                    data = base64Audio
                }
            }
        };

        await SendJsonAsync(msg, token);
    }

    public async Task SendScreenshotAsync(string base64Jpeg, CancellationToken token)
    {
        if (!IsConnected) return;

        var msg = new
        {
            realtimeInput = new
            {
                video = new
                {
                    mimeType = "image/jpeg",
                    data = base64Jpeg
                }
            }
        };

        await SendJsonAsync(msg, token);
    }

    public async Task CompleteTurnAsync(CancellationToken token)
    {
        if (!IsConnected) return;

        // Gemini 3.1 Live API VAD handles turn completion. 
        // We just need to send audioStreamEnd to flush the cached audio.
        var msg = new
        {
            realtimeInput = new
            {
                audioStreamEnd = true
            }
        };

        await SendJsonAsync(msg, token);
    }

    private static string BuildSystemInstruction(IReadOnlyList<ConversationHistory.Turn> history)
    {
        var sb = new StringBuilder();
        sb.Append("You are Clicky, a friendly on-screen voice assistant. Keep replies short, natural, and conversational. ");
        sb.Append("You can see the user's screen via screenshots. When pointing to something on screen, emit a tag in the form ");
        sb.Append("[POINT:x,y:short_label:screenN] where x,y are physical pixel coordinates relative to the screenshot's monitor top-left ");
        sb.Append("and N is the 0-based monitor index.");

        if (history.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("Earlier in this conversation (most recent last):");
            foreach (var t in history)
            {
                var speaker = t.Role == "user" ? "User" : "Assistant";
                sb.Append(speaker).Append(": ").AppendLine(t.Content);
            }
            sb.AppendLine();
            sb.Append("Continue the conversation naturally from here. The user's next message follows.");
        }

        return sb.ToString();
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
                    Log.Information("Gemini WebSocket closed: {Status} - {Desc}", result.CloseStatus, result.CloseStatusDescription);
                    _setupCompleteTcs?.TrySetException(new Exception($"WebSocket closed: {result.CloseStatusDescription}"));
                    ErrorOccurred?.Invoke(new Exception($"WebSocket closed: {result.CloseStatusDescription}"));
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
            Log.Error(ex, "Error in Gemini receive loop");
            _setupCompleteTcs?.TrySetException(ex);
            ErrorOccurred?.Invoke(ex);
        }
    }

    private void ProcessMessage(string json)
    {
        try
        {
            var doc = JsonNode.Parse(json);
            if (doc == null) return;

            var error = doc["error"];
            if (error != null)
            {
                Log.Error("Gemini API Error: {ErrorJson}", error.ToJsonString());
                ErrorOccurred?.Invoke(new Exception($"API Error: {error.ToJsonString()}"));
                return;
            }

            var setupComplete = doc["setupComplete"];
            if (setupComplete != null)
            {
                _setupCompleteTcs?.TrySetResult();
                return;
            }

            var serverContent = doc["serverContent"];
            if (serverContent != null)
            {
                var interrupted = serverContent["interrupted"]?.GetValue<bool>() ?? false;
                if (interrupted)
                {
                    _textBuffer.Clear();
                    return; // Ignore
                }

                var inputTx = serverContent["inputTranscription"];
                if (inputTx != null)
                {
                    var txText = inputTx["text"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(txText))
                        InputTranscriptionReceived?.Invoke(txText);
                }

                var outputTx = serverContent["outputTranscription"];
                if (outputTx != null)
                {
                    var txText = outputTx["text"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(txText))
                        _textBuffer.Append(txText);
                }

                var modelTurn = serverContent["modelTurn"];
                if (modelTurn != null)
                {
                    var parts = modelTurn["parts"]?.AsArray();
                    if (parts != null)
                    {
                        foreach (var part in parts)
                        {
                            var textNode = part?["text"];
                            if (textNode != null)
                            {
                                var text = textNode.GetValue<string>();
                                _textBuffer.Append(text);
                                TextChunkReceived?.Invoke(text);
                            }

                            var inlineData = part["inlineData"];
                            if (inlineData != null)
                            {
                                var dataNode = inlineData["data"];
                                if (dataNode != null)
                                {
                                    var base64 = dataNode.GetValue<string>();
                                    var audioBytes = Convert.FromBase64String(base64);
                                    AudioReceived?.Invoke(audioBytes);
                                }
                            }
                        }
                    }
                }
                
                var turnComplete = serverContent["turnComplete"]?.GetValue<bool>() ?? false;
                if (turnComplete)
                {
                    var fullText = _textBuffer.ToString();
                    _textBuffer.Clear();
                    if (!string.IsNullOrWhiteSpace(fullText))
                    {
                        TextCompleted?.Invoke(fullText);
                    }
                    TurnComplete?.Invoke();
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to parse Gemini response");
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
