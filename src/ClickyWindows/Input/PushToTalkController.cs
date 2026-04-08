using System.Net.Http;
using System.Text;
using System.Windows;
using ClickyWindows.AI;
using ClickyWindows.Audio;
using ClickyWindows.Overlay;
using ClickyWindows.Screen;
using Serilog;

namespace ClickyWindows.Input;

/// <summary>
/// State machine: IDLE → RECORDING → PROCESSING → SPEAKING → IDLE
/// Echo prevention: recording blocked while SPEAKING (stops TTS first).
/// </summary>
public class PushToTalkController : IDisposable
{
    private readonly OverlayManager _overlay;
    private readonly AppSettings _settings;
    private AppState _state = AppState.Idle;
    private readonly object _stateLock = new();
    private readonly object _audioBufferLock = new();
    private readonly SemaphoreSlim _audioSendLock = new(1, 1);

    // Services (initialized on startup after keys are confirmed present)
    private HttpClient? _httpClient;
    private ClaudeService? _claude;
    private TtsService? _tts;
    private readonly AudioPlaybackService _playback;
    private readonly MicrophoneRecorder _recorder;
    private readonly ScreenCaptureService _screenCapture;
    private readonly ConversationHistory _history;
    private readonly string? _assemblyAiApiKey;

    private CancellationTokenSource? _interactionCts;
    private Task? _interactionTask;
    private Task? _transcriptionConnectTask;
    private TranscriptionService? _transcriptionSession;
    private string _currentTranscript = "";
    private readonly StringBuilder _currentResponse = new();
    private volatile bool _transcriptReceived;
    private bool _recordingReleased;
    private bool _transcriptionTerminationRequested;
    private int _turnCounter;
    private int _activeTurnId;
    private readonly Queue<byte[]> _pendingAudioBuffers = new();

    private static readonly string[] NavigationPhrases =
        ["right here!", "click this!", "this one!", "over here!", "found it!"];

    public PushToTalkController(OverlayManager overlay, AppSettings settings)
    {
        _overlay = overlay;
        _settings = settings;
        _recorder = new MicrophoneRecorder();
        _playback = new AudioPlaybackService(settings.Audio.PreBufferMs, settings.Audio.PlaybackBufferSeconds);
        _screenCapture = new ScreenCaptureService();
        _history = new ConversationHistory(settings.Claude.MaxHistory);
        _assemblyAiApiKey = CredentialStore.GetKey(CredentialStore.AssemblyAITarget);

        _recorder.LevelsUpdated += levels =>
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                _overlay.SetWaveformLevels(levels));
        };

        _playback.PlaybackCompleted += OnPlaybackCompleted;

        InitializeServices();
    }

    private void InitializeServices()
    {
        if (!CredentialStore.HasAllKeys())
        {
            Log.Warning(
                "API keys not configured for direct mode ({MissingKeys}) — AI features disabled.",
                string.Join(", ", CredentialStore.GetMissingKeyNames()));
            return;
        }

        _httpClient = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        _claude = new ClaudeService(_httpClient, _settings);
        _tts = new TtsService(_httpClient, _settings, _playback);
    }

    public void OnHotkeyPressed()
    {
        lock (_stateLock)
        {
            if (_state == AppState.Speaking)
            {
                Log.Debug("Hotkey pressed during SPEAKING — stopping TTS");
                CancelCurrentTurnLocked();
            }

            if (_state == AppState.Idle || _state == AppState.Speaking)
            {
                StartRecordingLocked();
            }
        }
    }

    public void OnHotkeyReleased()
    {
        lock (_stateLock)
        {
            if (_state == AppState.Recording)
                StopRecordingAndProcess();
        }
    }

    private void StartRecordingLocked()
    {
        if (_claude == null || _tts == null)
        {
            Log.Warning("AI services not initialized");
            return;
        }

        var turnId = Interlocked.Increment(ref _turnCounter);
        _activeTurnId = turnId;

        Log.Information("State: RECORDING");
        _state = AppState.Recording;
        _interactionCts?.Dispose();
        _interactionCts = new CancellationTokenSource();
        _interactionTask = null;
        _currentTranscript = "";
        _currentResponse.Clear();
        _transcriptReceived = false;
        _recordingReleased = false;
        _transcriptionTerminationRequested = false;
        ClearPendingAudio();

        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            _overlay.SetState(AppState.Recording));

        var transcriptionSession = CreateTranscriptionSession();
        _transcriptionSession = transcriptionSession;

        _recorder.StartRecording();
        _recorder.DataAvailable += OnAudioData;

        _transcriptionConnectTask = RunTranscriptionSessionAsync(turnId, transcriptionSession, _interactionCts.Token);
    }

    private TranscriptionService CreateTranscriptionSession()
    {
        var session = new TranscriptionService(_assemblyAiApiKey ?? string.Empty, _settings);
        session.TranscriptFinalized += transcript => OnTranscriptFinalized(session, transcript);
        session.SessionEnded += endedEvent => OnTranscriptionSessionEnded(session, endedEvent);
        session.PartialTranscript += _ => Log.Debug("Partial transcript received");
        session.Error += ex => Log.Error(ex, "Transcription error");
        return session;
    }

    private async Task RunTranscriptionSessionAsync(int turnId, TranscriptionService session, CancellationToken token)
    {
        try
        {
            var connected = await session.ConnectAsync(token);
            if (!IsActiveTurn(turnId, session))
            {
                await DisposeSessionAsync(session);
                return;
            }

            if (!connected)
            {
                await HandleTranscriptionUnavailableAsync(turnId, session, session.LastFailure?.Message ?? "Failed to connect to AssemblyAI.");
                return;
            }

            await FlushBufferedAudioAsync(turnId, session, token);
            if (_recordingReleased)
            {
                await FinalizeTranscriptionAsync(turnId, session, token);
            }
        }
        catch (OperationCanceledException)
        {
            // Turn cancellation is expected during interruption or shutdown.
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to connect transcription WebSocket");
            if (IsActiveTurn(turnId, session))
            {
                await HandleTranscriptionUnavailableAsync(turnId, session, "Failed to connect to AssemblyAI.");
            }
        }
    }

    private async void OnAudioData(byte[] buffer, int count)
    {
        var session = _transcriptionSession;
        var token = _interactionCts?.Token ?? CancellationToken.None;
        var turnId = _activeTurnId;
        if (session == null || token.IsCancellationRequested)
        {
            return;
        }

        try
        {
            if (session.IsReady)
            {
                await SendAudioChunkAsync(turnId, session, buffer, count, token);
            }
            else
            {
                BufferAudio(buffer, count);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to send audio to transcription");
        }
    }

    private void StopRecordingAndProcess()
    {
        Log.Information("State: PROCESSING");
        _state = AppState.Processing;

        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            _overlay.SetState(AppState.Processing));

        _recorder.DataAvailable -= OnAudioData;
        _recorder.StopRecording();
        _recordingReleased = true;

        if (_transcriptionSession == null)
        {
            Log.Warning("No transcription session for current turn — returning to idle");
            TransitionToIdle(_activeTurnId);
            return;
        }

        if (_transcriptionSession.IsReady)
        {
            _ = FinalizeTranscriptionAsync(_activeTurnId, _transcriptionSession, _interactionCts?.Token ?? CancellationToken.None);
            return;
        }

        if (_transcriptionSession.IsTerminal)
        {
            Log.Warning("Transcription session ended before ready — returning to idle");
            TransitionToIdle(_activeTurnId);
            return;
        }

        Log.Debug("Waiting for AssemblyAI session to become ready before flushing buffered audio");
    }

    private async Task FinalizeTranscriptionAsync(int turnId, TranscriptionService session, CancellationToken token)
    {
        if (!IsActiveTurn(turnId, session) || _transcriptionTerminationRequested)
        {
            return;
        }

        _transcriptionTerminationRequested = true;

        try
        {
            await FlushBufferedAudioAsync(turnId, session, token);
            await session.TerminateAsync(token);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to terminate transcription session");
            TransitionToIdle(turnId);
        }
    }

    private void OnTranscriptFinalized(TranscriptionService session, string transcript)
    {
        var turnId = _activeTurnId;
        if (!IsActiveTurn(turnId, session))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(transcript))
        {
            TransitionToIdle(turnId);
            return;
        }

        if (_transcriptReceived)
        {
            return;
        }

        _transcriptReceived = true;
        _currentTranscript = transcript;
        Log.Information("Transcript received ({Length} chars)", transcript.Length);
        _history.AddUserMessage(transcript);

        _interactionTask = RunClaudeAsync(turnId, transcript, _interactionCts?.Token ?? CancellationToken.None);
    }

    private void OnTranscriptionSessionEnded(TranscriptionService session, TranscriptionSessionEndedEvent endedEvent)
    {
        var turnId = _activeTurnId;
        if (!ReferenceEquals(session, _transcriptionSession))
        {
            _ = DisposeSessionAsync(session);
            return;
        }

        LogSessionEnded(endedEvent);
        _ = DisposeSessionAsync(session);
        _transcriptionSession = null;

        bool shouldIdle;
        lock (_stateLock)
        {
            shouldIdle = (_state == AppState.Recording || _state == AppState.Processing) && !_transcriptReceived;
        }

        if (shouldIdle && turnId == _activeTurnId)
        {
            Log.Warning("Transcription session ended with no transcript — returning to idle");
            TransitionToIdle(turnId);
        }
    }

    private async Task RunClaudeAsync(int turnId, string userMessage, CancellationToken token)
    {
        if (_claude == null || _tts == null)
        {
            Log.Warning("AI services not initialized");
            TransitionToIdle(turnId);
            return;
        }

        var monitors = _overlay.GetMonitors();
        var captures = _screenCapture.CaptureAll(monitors);
        var screenshotTuples = captures
            .Select(c => (c.Base64Jpeg, c.Width, c.Height, c.MonitorIndex, c.IsFocus))
            .ToList();

        _currentResponse.Clear();
        var pointsFound = new List<PointTarget>();

        try
        {
            var claudeResult = await _claude.GetResponseAsync(
                userMessage,
                _history,
                screenshotTuples,
                (chunk, cancellationToken) =>
                {
                    if (!IsActiveTurn(turnId))
                    {
                        return ValueTask.CompletedTask;
                    }

                    _currentResponse.Append(chunk);
                    var newPoints = PointParser.ParseAll(chunk, monitors);
                    pointsFound.AddRange(newPoints);
                    return ValueTask.CompletedTask;
                },
                token);

            if (!IsActiveTurn(turnId))
            {
                return;
            }

            if (claudeResult.Kind == ClaudeResponseKind.Cancelled)
            {
                TransitionToIdle(turnId);
                return;
            }

            if (claudeResult.Kind is ClaudeResponseKind.Failed or ClaudeResponseKind.Incomplete)
            {
                Log.Warning("Claude failed: {Message}", claudeResult.Failure?.Message ?? "unknown error");
                TransitionToIdle(turnId);
                return;
            }

            var fullResponse = claudeResult.Text;

            Log.Information("Claude response: {Chars} chars, {Points} points",
                fullResponse.Length, pointsFound.Count);

            if (string.IsNullOrWhiteSpace(fullResponse))
            {
                TransitionToIdle(turnId);
                return;
            }

            _history.AddAssistantMessage(fullResponse);

            Log.Information("State: SPEAKING");
            _state = AppState.Speaking;
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                _overlay.SetState(AppState.Speaking));

            if (pointsFound.Count > 0)
            {
                var pt = pointsFound[0];
                var monitor = monitors.Count > pt.ScreenIndex ? monitors[pt.ScreenIndex] : monitors[0];
                var phrase = NavigationPhrases[Random.Shared.Next(NavigationPhrases.Length)];

                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    _overlay.SetSpeechBubble(phrase);
                    _overlay.StartFlightTo(
                        monitor.PhysicalBounds.Left + pt.X,
                        monitor.PhysicalBounds.Top + pt.Y,
                        monitor);
                });
            }

            var ttsText = System.Text.RegularExpressions.Regex.Replace(
                fullResponse, @"\[POINT:[^\]]+\]", "");
            var ttsTextTrimmed = ttsText.Trim();
            if (string.IsNullOrWhiteSpace(ttsTextTrimmed))
            {
                TransitionToIdle(turnId);
                return;
            }

            var speechResult = await _tts.SpeakAsync(ttsTextTrimmed, token);
            if (!IsActiveTurn(turnId))
            {
                return;
            }

            if (speechResult.Kind is SpeechResultKind.Cancelled or SpeechResultKind.Failed)
            {
                if (speechResult.Failure != null)
                {
                    Log.Warning("TTS failed: {Message}", speechResult.Failure.Message);
                }

                TransitionToIdle(turnId);
            }
        }
        catch (OperationCanceledException)
        {
            Log.Debug("Claude/TTS interaction cancelled");
            TransitionToIdle(turnId);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error during Claude/TTS interaction");
            TransitionToIdle(turnId);
        }
    }

    private void OnPlaybackCompleted()
    {
        int turnId;
        lock (_stateLock)
        {
            if (_state != AppState.Speaking)
            {
                return;
            }

            turnId = _activeTurnId;
        }

        TransitionToIdle(turnId);
    }

    private async Task SendAudioChunkAsync(int turnId, TranscriptionService session, byte[] buffer, int count, CancellationToken token)
    {
        await _audioSendLock.WaitAsync(token);
        try
        {
            if (!IsActiveTurn(turnId, session))
            {
                return;
            }

            await session.SendAudioAsync(buffer, count, token);
        }
        finally
        {
            _audioSendLock.Release();
        }
    }

    private async Task FlushBufferedAudioAsync(int turnId, TranscriptionService session, CancellationToken token)
    {
        while (true)
        {
            byte[]? bufferedChunk;
            lock (_audioBufferLock)
            {
                if (_pendingAudioBuffers.Count == 0)
                {
                    break;
                }

                bufferedChunk = _pendingAudioBuffers.Dequeue();
            }

            await SendAudioChunkAsync(turnId, session, bufferedChunk, bufferedChunk.Length, token);
        }
    }

    private void BufferAudio(byte[] buffer, int count)
    {
        var copy = new byte[count];
        Buffer.BlockCopy(buffer, 0, copy, 0, count);

        lock (_audioBufferLock)
        {
            _pendingAudioBuffers.Enqueue(copy);
        }
    }

    private void ClearPendingAudio()
    {
        lock (_audioBufferLock)
        {
            _pendingAudioBuffers.Clear();
        }
    }

    private async Task HandleTranscriptionUnavailableAsync(int turnId, TranscriptionService session, string reason)
    {
        if (!IsActiveTurn(turnId, session))
        {
            await DisposeSessionAsync(session);
            return;
        }

        Log.Warning("Transcription unavailable: {Reason}", reason);
        _recorder.DataAvailable -= OnAudioData;
        _recorder.StopRecording();
        await DisposeSessionAsync(session);
        _transcriptionSession = null;
        TransitionToIdle(turnId);
    }

    private bool IsActiveTurn(int turnId) => turnId == _activeTurnId;

    private bool IsActiveTurn(int turnId, TranscriptionService session) =>
        IsActiveTurn(turnId) && ReferenceEquals(session, _transcriptionSession);

    private void TransitionToIdle(int turnId)
    {
        if (!IsActiveTurn(turnId))
        {
            return;
        }

        lock (_stateLock)
        {
            if (turnId != _activeTurnId)
            {
                return;
            }

            Log.Information("State: IDLE");
            _state = AppState.Idle;
        }

        _recordingReleased = false;
        _transcriptionTerminationRequested = false;
        _recorder.DataAvailable -= OnAudioData;
        _recorder.StopRecording();
        ClearPendingAudio();
        _interactionCts?.Cancel();

        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            _overlay.SetState(AppState.Idle);
            _overlay.SetSpeechBubble("");
        });
    }

    private void CancelCurrentTurnLocked()
    {
        _interactionCts?.Cancel();
        _interactionCts?.Dispose();
        _interactionCts = null;
        _recorder.DataAvailable -= OnAudioData;
        _recorder.StopRecording();
        _playback.Stop();
        ClearPendingAudio();
        _recordingReleased = false;
        _transcriptionTerminationRequested = false;

        if (_transcriptionSession != null)
        {
            var session = _transcriptionSession;
            _transcriptionSession = null;
            _ = DisposeSessionAsync(session);
        }
    }

    private static async Task DisposeSessionAsync(TranscriptionService session)
    {
        try
        {
            await session.DisposeAsync();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Ignored transcription session dispose failure");
        }
    }

    private static void LogSessionEnded(TranscriptionSessionEndedEvent endedEvent)
    {
        if (endedEvent.Kind == TranscriptionEndKind.Completed)
        {
            Log.Information(
                "AssemblyAI session ended: {SessionId} ({CloseStatus} {Description})",
                endedEvent.SessionId ?? "(unknown)",
                endedEvent.CloseStatus,
                endedEvent.CloseStatusDescription);
            return;
        }

        Log.Warning(
            "AssemblyAI session ended with {Kind}: {SessionId} {Message}",
            endedEvent.Kind,
            endedEvent.SessionId ?? "(unknown)",
            endedEvent.Failure?.Message ?? endedEvent.CloseStatusDescription ?? "(no details)");
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            CancelCurrentTurnLocked();
        }

        _interactionCts?.Cancel();
        _recorder.Dispose();
        _playback.Dispose();
        _httpClient?.Dispose();
        _audioSendLock.Dispose();
    }
}
