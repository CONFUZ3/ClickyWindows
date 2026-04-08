using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Serilog;

namespace ClickyWindows.AI;

/// <summary>
/// AssemblyAI v3 WebSocket streaming transcription.
/// Endpoint: wss://streaming.assemblyai.com/v3/ws
/// speech_model is REQUIRED (no default) — use "u3-rt-pro".
/// </summary>
public class TranscriptionService : IAsyncDisposable
{
    private readonly string _apiKey;
    private readonly AppSettings _settings;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;

    public event Action<string>? TranscriptFinalized;
    public event Action<string>? PartialTranscript;
    public event Action<Exception>? Error;
    public event Action? SessionEnded;

    /// <summary>True if the WebSocket is open and the receive loop is actively running.</summary>
    public bool HasActiveSession =>
        _ws?.State == WebSocketState.Open &&
        _receiveTask != null &&
        !_receiveTask.IsCompleted;

    public TranscriptionService(string apiKey, AppSettings settings)
    {
        _apiKey = apiKey;
        _settings = settings;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ws = new ClientWebSocket();
        _ws.Options.SetRequestHeader("Authorization", _apiKey);

        var ai = _settings.AssemblyAI;
        var uri = new Uri(
            $"wss://streaming.assemblyai.com/v3/ws" +
            $"?speech_model={ai.SpeechModel}" +
            $"&min_turn_silence={ai.MinTurnSilenceMs}" +
            $"&max_turn_silence={ai.MaxTurnSilenceMs}" +
            $"&encoding=pcm_s16le" +
            $"&sample_rate={_settings.Audio.SampleRate}");

        Log.Information("Connecting to AssemblyAI v3: {Uri}", uri);
        await _ws.ConnectAsync(uri, _cts.Token);
        Log.Information("AssemblyAI WebSocket connected");

        _receiveTask = Task.Run(() => ReceiveLoop(_cts.Token), _cts.Token);
    }

    /// <summary>Send PCM16 audio chunk to AssemblyAI.</summary>
    public async Task SendAudioAsync(byte[] pcmData, int count, CancellationToken cancellationToken = default)
    {
        if (_ws?.State != WebSocketState.Open) return;

        var segment = new ArraySegment<byte>(pcmData, 0, count);
        await _ws.SendAsync(segment, WebSocketMessageType.Binary, true, cancellationToken);
    }

    /// <summary>Send Terminate message to end the session cleanly.</summary>
    public async Task TerminateAsync()
    {
        if (_ws?.State != WebSocketState.Open) return;

        var terminate = JsonSerializer.Serialize(new { type = "Terminate" });
        var bytes = Encoding.UTF8.GetBytes(terminate);
        await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        Log.Debug("Sent Terminate to AssemblyAI");
    }

    private async Task ReceiveLoop(CancellationToken cancellationToken)
    {
        var buffer = new byte[16384];

        try
        {
            while (!cancellationToken.IsCancellationRequested && _ws?.State == WebSocketState.Open)
            {
                var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Log.Information("AssemblyAI WebSocket closed by server");
                    break;
                }

                if (result.MessageType != WebSocketMessageType.Text) continue;

                var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
                HandleMessage(text);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error(ex, "AssemblyAI receive loop error");
            Error?.Invoke(ex);
        }
        finally
        {
            // Always signal session end so callers can escape PROCESSING state
            SessionEnded?.Invoke();
        }
    }

    private void HandleMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeProp)) return;
            var type = typeProp.GetString();

            switch (type)
            {
                case "Begin":
                    Log.Information("AssemblyAI session started: {SessionId}",
                        root.TryGetProperty("id", out var sid) ? sid.GetString() : "?");
                    break;

                case "SpeechStarted":
                    Log.Debug("Speech started");
                    break;

                case "Turn":
                    HandleTurn(root);
                    break;

                case "Termination":
                    Log.Information("AssemblyAI session terminated");
                    break;

                default:
                    Log.Debug("AssemblyAI message: {Type}", type);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to parse AssemblyAI message");
        }
    }

    private void HandleTurn(JsonElement root)
    {
        bool endOfTurn = root.TryGetProperty("end_of_turn", out var eot) && eot.GetBoolean();

        string transcript = "";
        if (root.TryGetProperty("transcript", out var t))
            transcript = t.GetString() ?? "";

        Log.Debug("AssemblyAI Turn: end_of_turn={EndOfTurn}, {Length} chars",
            endOfTurn, transcript.Length);

        if (endOfTurn && !string.IsNullOrWhiteSpace(transcript))
        {
            TranscriptFinalized?.Invoke(transcript);
        }
        else if (!string.IsNullOrWhiteSpace(transcript))
        {
            PartialTranscript?.Invoke(transcript);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();

        if (_receiveTask != null)
        {
            try { await _receiveTask; } catch { }
        }

        _ws?.Dispose();
    }
}
