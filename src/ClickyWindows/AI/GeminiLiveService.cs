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
    public event Action<Exception>? ErrorOccurred;
    public event Action? TurnComplete;

    private readonly StringBuilder _textBuffer = new();

    public GeminiLiveService(string apiKey, GeminiSettings settings)
    {
        _apiKey = apiKey;
        _settings = settings;
    }

    public bool IsConnected => _webSocket?.State == WebSocketState.Open;

    public async Task ConnectAsync(CancellationToken token)
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
        
        var uri = new Uri($"wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1alpha.GenerativeService.BidiGenerateContent?key={_apiKey}");
        
        Log.Information("Connecting to Gemini Live API...");
        await _webSocket.ConnectAsync(uri, _connectionCts.Token);
        Log.Information("Connected to Gemini Live API network transport");
        _setupCompleteTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var setupMsg = new
        {
            setup = new
            {
                model = _settings.Model,
                generationConfig = new
                {
                    responseModalities = new[] { "AUDIO" },
                    speechConfig = new
                    {
                        voiceConfig = new
                        {
                            prebuiltVoiceConfig = new
                            {
                                voiceName = _settings.VoiceName
                            }
                        }
                    }
                }
            }
        };

        await SendJsonAsync(setupMsg, _connectionCts.Token);

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

    private async Task SendJsonAsync(object payload, CancellationToken token)
    {
        if (!IsConnected) return;
        
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _webSocket!.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);
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

                var modelTurn = serverContent["modelTurn"];
                if (modelTurn != null)
                {
                    var parts = modelTurn["parts"]?.AsArray();
                    if (parts != null)
                    {
                        foreach (var part in parts)
                        {
                            var textNode = part["text"];
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
                    await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disposing", CancellationToken.None);
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
