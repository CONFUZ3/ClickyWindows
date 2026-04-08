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

    public async Task ConnectAsync(CancellationToken token)
    {
        _connectionCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        _webSocket = new ClientWebSocket();
        
        var uri = new Uri($"wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1alpha.GenerativeService.BidiGenerateContent?key={_apiKey}");
        
        Log.Information("Connecting to Gemini Live API...");
        await _webSocket.ConnectAsync(uri, _connectionCts.Token);
        Log.Information("Connected to Gemini Live API");

        var setupMsg = new
        {
            setup = new
            {
                model = _settings.Model,
                generationConfig = new
                {
                    responseModalities = new[] { "AUDIO", "TEXT" },
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

        // Receive setup complete first
        var setupBuffer = new byte[32768];
        var setupResult = await _webSocket.ReceiveAsync(new ArraySegment<byte>(setupBuffer), _connectionCts.Token);
        var setupResponse = Encoding.UTF8.GetString(setupBuffer, 0, setupResult.Count);
        Log.Debug("Setup response: {Response}", setupResponse);

        _receiveTask = Task.Run(() => ReceiveLoopAsync(_connectionCts.Token), _connectionCts.Token);
    }

    public async Task SendAudioAsync(byte[] pcmData, CancellationToken token)
    {
        if (_webSocket?.State != WebSocketState.Open) return;

        var base64Audio = Convert.ToBase64String(pcmData);
        var msg = new
        {
            clientContent = new
            {
                turns = new[]
                {
                    new
                    {
                        role = "user",
                        parts = new[]
                        {
                            new
                            {
                                inlineData = new
                                {
                                    mimeType = "audio/pcm;rate=16000",
                                    data = base64Audio
                                }
                            }
                        }
                    }
                },
                turnComplete = false
            }
        };

        await SendJsonAsync(msg, token);
    }

    public async Task SendScreenshotAsync(string base64Jpeg, CancellationToken token)
    {
        if (_webSocket?.State != WebSocketState.Open) return;

        var msg = new
        {
            clientContent = new
            {
                turns = new[]
                {
                    new
                    {
                        role = "user",
                        parts = new[]
                        {
                            new
                            {
                                inlineData = new
                                {
                                    mimeType = "image/jpeg",
                                    data = base64Jpeg
                                }
                            }
                        }
                    }
                },
                turnComplete = false
            }
        };

        await SendJsonAsync(msg, token);
    }

    public async Task CompleteTurnAsync(CancellationToken token)
    {
        if (_webSocket?.State != WebSocketState.Open) return;

        var msg = new
        {
            clientContent = new
            {
                turns = new[]
                {
                    new
                    {
                        role = "user",
                        parts = new object[0]
                    }
                },
                turnComplete = true
            }
        };

        await SendJsonAsync(msg, token);
    }

    private async Task SendJsonAsync(object payload, CancellationToken token)
    {
        if (_webSocket?.State != WebSocketState.Open) return;
        
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        var buffer = new byte[65536];
        var messageBuilder = new List<byte>();

        try
        {
            while (_webSocket?.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Log.Information("Gemini WebSocket closed");
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
            ErrorOccurred?.Invoke(ex);
        }
    }

    private void ProcessMessage(string json)
    {
        try
        {
            var doc = JsonNode.Parse(json);
            if (doc == null) return;

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
        _connectionCts?.Cancel();
        _connectionCts?.Dispose();

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
