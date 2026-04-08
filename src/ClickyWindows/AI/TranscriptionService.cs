using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Serilog;

namespace ClickyWindows.AI;

/// <summary>
/// Represents a single AssemblyAI v3 streaming transcription session.
/// A new instance should be created for each push-to-talk turn.
/// </summary>
public sealed class TranscriptionService : IAsyncDisposable
{
    private readonly string _apiKey;
    private readonly AppSettings _settings;
    private TaskCompletionSource<bool> _readyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _endedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _stateLock = new();

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _sessionCts;
    private Task? _receiveTask;
    private TranscriptionSessionState _state = TranscriptionSessionState.Created;
    private bool _terminateSent;
    private bool _sessionEndedRaised;
    private string? _sessionId;
    private ServiceFailure? _lastFailure;
    private WebSocketCloseStatus? _closeStatus;
    private string? _closeStatusDescription;

    public event Action? SessionReady;
    public event Action<string>? TranscriptFinalized;
    public event Action<string>? PartialTranscript;
    public event Action<Exception>? Error;
    public event Action<TranscriptionSessionEndedEvent>? SessionEnded;

    public TranscriptionSessionState State
    {
        get
        {
            lock (_stateLock)
            {
                return _state;
            }
        }
    }

    public bool IsReady => State == TranscriptionSessionState.Ready;
    public bool IsTerminal => State is TranscriptionSessionState.Closed or TranscriptionSessionState.Faulted;
    public string? SessionId => _sessionId;
    public ServiceFailure? LastFailure => _lastFailure;

    public TranscriptionService(string apiKey, AppSettings settings)
    {
        _apiKey = apiKey;
        _settings = settings;
    }

    public async Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            SetFailure(new ServiceFailure(ServiceFailureKind.Authentication, "AssemblyAI API key is missing."));
            TransitionToState(TranscriptionSessionState.Faulted);
            RaiseSessionEnded(new TranscriptionSessionEndedEvent(
                TranscriptionEndKind.Authentication,
                _sessionId,
                Failure: _lastFailure));
            _endedTcs.TrySetResult(true);
            return false;
        }

        var attempts = Math.Max(1, _settings.AssemblyAI.RetryCount + 1);
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ResetAttemptState();
            TransitionToState(TranscriptionSessionState.Connecting);

            var connected = await TryConnectOnceAsync(attempt, cancellationToken);
            if (connected)
            {
                return true;
            }

            if (attempt >= attempts || !ShouldRetry(_lastFailure))
            {
                RaiseSessionEnded(BuildEndEventForFailure());
                _endedTcs.TrySetResult(true);
                return false;
            }

            var delayMs = _settings.AssemblyAI.RetryBaseDelayMs * attempt;
            Log.Warning(
                "Retrying AssemblyAI connect in {DelayMs}ms (attempt {Attempt}/{Attempts}) due to {Reason}",
                delayMs,
                attempt + 1,
                attempts,
                _lastFailure?.Message ?? "unknown failure");
            await Task.Delay(delayMs, cancellationToken);
        }

        RaiseSessionEnded(BuildEndEventForFailure());
        _endedTcs.TrySetResult(true);
        return false;
    }

    public async Task SendAudioAsync(byte[] pcmData, int count, CancellationToken cancellationToken = default)
    {
        if (!IsReady || _ws?.State != WebSocketState.Open)
        {
            return;
        }

        var segment = new ArraySegment<byte>(pcmData, 0, count);
        await _ws.SendAsync(segment, WebSocketMessageType.Binary, true, cancellationToken);
    }

    public async Task TerminateAsync(CancellationToken cancellationToken = default)
    {
        if (_terminateSent || _ws?.State != WebSocketState.Open)
        {
            return;
        }

        var terminate = JsonSerializer.Serialize(new { type = "Terminate" });
        var bytes = Encoding.UTF8.GetBytes(terminate);
        await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken);
        _terminateSent = true;
        TransitionToState(TranscriptionSessionState.Closing);
        Log.Debug("Sent Terminate to AssemblyAI session {SessionId}", _sessionId ?? "(pending)");
    }

    public Task WaitUntilEndedAsync(CancellationToken cancellationToken = default) =>
        cancellationToken.CanBeCanceled
            ? _endedTcs.Task.WaitAsync(cancellationToken)
            : _endedTcs.Task;

    private async Task<bool> TryConnectOnceAsync(int attempt, CancellationToken cancellationToken)
    {
        _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ws = new ClientWebSocket();
        _ws.Options.SetRequestHeader("Authorization", _apiKey);

        var uri = BuildUri();
        Log.Information("Connecting to AssemblyAI v3 (attempt {Attempt}): {Uri}", attempt, uri);

        try
        {
            using var connectTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(_sessionCts.Token);
            connectTimeoutCts.CancelAfter(TimeSpan.FromMilliseconds(_settings.AssemblyAI.ConnectTimeoutMs));

            await _ws.ConnectAsync(uri, connectTimeoutCts.Token);
            Log.Information("AssemblyAI WebSocket connected");

            _receiveTask = Task.Run(() => ReceiveLoop(_sessionCts.Token), CancellationToken.None);

            using var readyTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readyTimeoutCts.CancelAfter(TimeSpan.FromMilliseconds(_settings.AssemblyAI.SessionReadyTimeoutMs));
            var ready = await _readyTcs.Task.WaitAsync(readyTimeoutCts.Token);
            if (ready)
            {
                return true;
            }
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            var timedOut = _ws?.State == WebSocketState.Open
                ? "AssemblyAI session did not send Begin before timeout."
                : "AssemblyAI connection attempt timed out.";
            SetFailure(new ServiceFailure(ServiceFailureKind.Timeout, timedOut, IsRetryable: true, Exception: ex));
            TransitionToState(TranscriptionSessionState.Faulted);
        }
        catch (WebSocketException ex)
        {
            SetFailure(ClassifyConnectionException(ex));
            TransitionToState(TranscriptionSessionState.Faulted);
        }
        catch (HttpRequestException ex)
        {
            SetFailure(ClassifyConnectionException(ex));
            TransitionToState(TranscriptionSessionState.Faulted);
        }
        catch (Exception ex)
        {
            SetFailure(new ServiceFailure(ServiceFailureKind.Unknown, "AssemblyAI connection failed.", IsRetryable: false, Exception: ex));
            TransitionToState(TranscriptionSessionState.Faulted);
        }

        await CloseSocketAsync("connect failed", cancellationToken);
        return false;
    }

    private async Task ReceiveLoop(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && _ws?.State == WebSocketState.Open)
            {
                var (messageType, text) = await ReceiveMessageAsync(cancellationToken);
                if (messageType == WebSocketMessageType.Close)
                {
                    _closeStatus = _ws.CloseStatus;
                    _closeStatusDescription = _ws.CloseStatusDescription;
                    Log.Information(
                        "AssemblyAI WebSocket closed by server: {CloseStatus} {Description}",
                        _closeStatus,
                        _closeStatusDescription);
                    break;
                }

                if (messageType != WebSocketMessageType.Text || string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                HandleMessage(text);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetFailure(new ServiceFailure(ServiceFailureKind.Cancelled, "AssemblyAI session cancelled."));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "AssemblyAI receive loop error");
            SetFailure(new ServiceFailure(ServiceFailureKind.Network, "AssemblyAI receive loop failed.", IsRetryable: true, Exception: ex));
            Error?.Invoke(ex);
        }
        finally
        {
            if (State != TranscriptionSessionState.Faulted)
            {
                TransitionToState(TranscriptionSessionState.Closed);
            }

            await CloseSocketAsync("receive loop ended", CancellationToken.None);
            _readyTcs.TrySetResult(false);
            RaiseSessionEnded(BuildSessionEndedEvent());
            _endedTcs.TrySetResult(true);
        }
    }

    private async Task<(WebSocketMessageType MessageType, string? Text)> ReceiveMessageAsync(CancellationToken cancellationToken)
    {
        if (_ws == null)
        {
            return (WebSocketMessageType.Close, null);
        }

        var buffer = new byte[16 * 1024];
        using var messageStream = new MemoryStream();

        while (true)
        {
            var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return (result.MessageType, null);
            }

            if (result.Count > 0)
            {
                messageStream.Write(buffer, 0, result.Count);
            }

            if (result.EndOfMessage)
            {
                if (result.MessageType != WebSocketMessageType.Text)
                {
                    return (result.MessageType, null);
                }

                return (result.MessageType, Encoding.UTF8.GetString(messageStream.ToArray()));
            }
        }
    }

    private void HandleMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeProp))
            {
                return;
            }

            var type = typeProp.GetString();
            switch (type)
            {
                case "Begin":
                    HandleBegin(root);
                    break;

                case "SpeechStarted":
                    Log.Debug("AssemblyAI speech started for session {SessionId}", _sessionId ?? "(pending)");
                    break;

                case "Turn":
                    HandleTurn(root);
                    break;

                case "Termination":
                    Log.Information("AssemblyAI session terminated: {SessionId}", _sessionId ?? "(pending)");
                    break;

                case "Error":
                    HandleServerError(root);
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

    private void HandleBegin(JsonElement root)
    {
        _sessionId = root.TryGetProperty("id", out var idProp) ? idProp.GetString() : _sessionId;
        Log.Information("AssemblyAI session started: {SessionId}", _sessionId ?? "(unknown)");
        TransitionToState(TranscriptionSessionState.Ready);
        _readyTcs.TrySetResult(true);
        SessionReady?.Invoke();
    }

    private void HandleTurn(JsonElement root)
    {
        var endOfTurn = root.TryGetProperty("end_of_turn", out var eot) && eot.GetBoolean();
        var transcript = root.TryGetProperty("transcript", out var transcriptProp)
            ? transcriptProp.GetString() ?? string.Empty
            : string.Empty;

        Log.Debug(
            "AssemblyAI Turn: session={SessionId}, end_of_turn={EndOfTurn}, {Length} chars",
            _sessionId ?? "(pending)",
            endOfTurn,
            transcript.Length);

        if (string.IsNullOrWhiteSpace(transcript))
        {
            return;
        }

        if (endOfTurn)
        {
            TranscriptFinalized?.Invoke(transcript);
        }
        else
        {
            PartialTranscript?.Invoke(transcript);
        }
    }

    private void HandleServerError(JsonElement root)
    {
        var message = root.TryGetProperty("message", out var messageProp)
            ? messageProp.GetString()
            : "AssemblyAI returned an error event.";
        var failure = new ServiceFailure(ServiceFailureKind.Upstream, message ?? "AssemblyAI returned an error event.");
        SetFailure(failure);
        TransitionToState(TranscriptionSessionState.Faulted);
        Log.Warning("AssemblyAI session error: {Message}", failure.Message);
    }

    private Uri BuildUri()
    {
        var ai = _settings.AssemblyAI;
        return new Uri(
            $"wss://streaming.assemblyai.com/v3/ws" +
            $"?speech_model={Uri.EscapeDataString(ai.SpeechModel)}" +
            $"&min_turn_silence={ai.MinTurnSilenceMs}" +
            $"&max_turn_silence={ai.MaxTurnSilenceMs}" +
            $"&encoding=pcm_s16le" +
            $"&sample_rate={_settings.Audio.SampleRate}" +
            $"&inactivity_timeout={ai.InactivityTimeoutSeconds}");
    }

    private void ResetAttemptState()
    {
        _readyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _terminateSent = false;
        _sessionId = null;
        _lastFailure = null;
        _closeStatus = null;
        _closeStatusDescription = null;
    }

    private void TransitionToState(TranscriptionSessionState nextState)
    {
        lock (_stateLock)
        {
            _state = nextState;
        }
    }

    private void SetFailure(ServiceFailure failure)
    {
        _lastFailure = failure;
        if (failure.Kind != ServiceFailureKind.Cancelled)
        {
            Log.Warning(failure.Exception, "AssemblyAI failure: {Message}", failure.Message);
        }
    }

    private static bool ShouldRetry(ServiceFailure? failure) =>
        failure is { IsRetryable: true };

    private ServiceFailure ClassifyConnectionException(Exception ex)
    {
        var message = ex.Message;
        var lowerMessage = message.ToLowerInvariant();
        if (lowerMessage.Contains("401") || lowerMessage.Contains("unauthorized"))
        {
            return new ServiceFailure(ServiceFailureKind.Authentication, "AssemblyAI authentication failed.", IsRetryable: false, Exception: ex);
        }

        if (lowerMessage.Contains("429") || lowerMessage.Contains("too many"))
        {
            return new ServiceFailure(ServiceFailureKind.RateLimited, "AssemblyAI rate limited or rejected the session.", IsRetryable: true, Exception: ex);
        }

        if (lowerMessage.Contains("host") || lowerMessage.Contains("dns") || lowerMessage.Contains("remote server") || lowerMessage.Contains("connect"))
        {
            return new ServiceFailure(ServiceFailureKind.Network, "AssemblyAI connection failed.", IsRetryable: true, Exception: ex);
        }

        return new ServiceFailure(ServiceFailureKind.Unknown, "AssemblyAI connection failed.", IsRetryable: false, Exception: ex);
    }

    private async Task CloseSocketAsync(string reason, CancellationToken cancellationToken)
    {
        if (_ws == null)
        {
            return;
        }

        try
        {
            if (_ws.State == WebSocketState.Open || _ws.State == WebSocketState.CloseReceived)
            {
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Ignored AssemblyAI socket close failure");
        }
        finally
        {
            _ws.Dispose();
            _ws = null;
            _sessionCts?.Dispose();
            _sessionCts = null;
        }
    }

    private TranscriptionSessionEndedEvent BuildEndEventForFailure()
    {
        var kind = _lastFailure?.Kind switch
        {
            ServiceFailureKind.Authentication => TranscriptionEndKind.Authentication,
            ServiceFailureKind.RateLimited => TranscriptionEndKind.RateLimited,
            ServiceFailureKind.Timeout when State == TranscriptionSessionState.Faulted => _ws?.State == WebSocketState.Open
                ? TranscriptionEndKind.SessionStartTimeout
                : TranscriptionEndKind.ConnectTimeout,
            ServiceFailureKind.Network => TranscriptionEndKind.Network,
            ServiceFailureKind.Cancelled => TranscriptionEndKind.Cancelled,
            _ => TranscriptionEndKind.ConnectFailed
        };

        return new TranscriptionSessionEndedEvent(kind, _sessionId, Failure: _lastFailure);
    }

    private TranscriptionSessionEndedEvent BuildSessionEndedEvent()
    {
        if (_lastFailure?.Kind == ServiceFailureKind.Cancelled)
        {
            return new TranscriptionSessionEndedEvent(
                TranscriptionEndKind.Cancelled,
                _sessionId,
                _closeStatus,
                _closeStatusDescription,
                _lastFailure);
        }

        if (_lastFailure != null)
        {
            var failureKind = _lastFailure.Kind switch
            {
                ServiceFailureKind.Authentication => TranscriptionEndKind.Authentication,
                ServiceFailureKind.RateLimited => TranscriptionEndKind.RateLimited,
                ServiceFailureKind.Timeout => TranscriptionEndKind.SessionStartTimeout,
                ServiceFailureKind.Network => TranscriptionEndKind.Network,
                _ => TranscriptionEndKind.Faulted
            };

            return new TranscriptionSessionEndedEvent(
                failureKind,
                _sessionId,
                _closeStatus,
                _closeStatusDescription,
                _lastFailure);
        }

        if (_closeStatus.HasValue)
        {
            return new TranscriptionSessionEndedEvent(
                TranscriptionEndKind.Completed,
                _sessionId,
                _closeStatus,
                _closeStatusDescription);
        }

        return new TranscriptionSessionEndedEvent(TranscriptionEndKind.Completed, _sessionId);
    }

    private void RaiseSessionEnded(TranscriptionSessionEndedEvent endedEvent)
    {
        if (_sessionEndedRaised)
        {
            return;
        }

        _sessionEndedRaised = true;
        SessionEnded?.Invoke(endedEvent);
    }

    public async ValueTask DisposeAsync()
    {
        _sessionCts?.Cancel();

        if (_receiveTask != null)
        {
            try
            {
                await _receiveTask;
            }
            catch
            {
                // Receive loop errors are surfaced through SessionEnded and logs.
            }
        }

        await CloseSocketAsync("dispose", CancellationToken.None);
        _endedTcs.TrySetResult(true);
    }
}
