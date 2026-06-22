using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using ClickyWindows.AI;
using ClickyWindows.Audio;
using ClickyWindows.Overlay;
using ClickyWindows.Screen;
using Serilog;

namespace ClickyWindows.Input;

public class PushToTalkController : IDisposable
{
    private readonly OverlayManager _overlay;
    private readonly AppSettings _settings;
    private AppState _state = AppState.Idle;
    private readonly object _stateLock = new();
    private bool _isHotkeyHeld;
    private int _turnVersion;

    private IRealtimeVoiceService? _gemini;
    private IPointingService? _flashPointing;
    private readonly AudioPlaybackService _playback;
    private readonly MicrophoneRecorder _recorder;
    private readonly ScreenCaptureService _screenCapture;
    private readonly string _provider;
    private readonly string? _apiKey;
    // Whether the active provider needs the screenshot sent before microphone audio streams.
    private readonly bool _requireScreenshotBeforeAudio;
    private readonly Queue<byte[]> _audioBuffer = new();
    private volatile bool _canStreamAudioToGemini;

    // Saved at screenshot-capture time so OnGeminiInputTranscription can pass them to the Flash pointing call.
    private List<ScreenCapture> _lastScreenCaptures = new();

    private readonly ConversationHistory _history;
    private string? _pendingScreenshot;
    private string? _pendingUserTranscript;
    private string? _pendingAssistantText;

    // Strip POINT tags from text before saving to history — handles both
    // bracketed [POINT:...] and unbracketed POINT:... forms that Gemini may emit.
    private static readonly Regex PointTagRegex = new(
        @"\[?POINT:\d+,\d+:[^:\]\n]+:screen\d+\]?", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string HistoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClickyWindows", "conversation_history.json");

    private CancellationTokenSource? _interactionCts;

    // Signals when the screenshot (and pre-buffered audio) have been sent to Gemini for the
    // current turn. StopRecordingAndProcessAsync awaits this before sending audioStreamEnd so
    // that Gemini always receives context before the turn is closed — even when the hotkey is
    // released while ConnectAsync is still in progress.
    private TaskCompletionSource<bool>? _screenshotReadyTcs;

    // Tracks the async disposal of the previous Gemini session so StartRecordingAsync
    // can await it before opening a new connection, preventing socket resource leaks.
    private volatile Task? _geminiDisposalTask;

    private static readonly string[] NavigationPhrases =
        ["right here!", "click this!", "this one!", "over here!", "found it!"];

    /// <summary>Raised with a user-facing message when a turn fails (e.g. the provider rejected the key).</summary>
    public event Action<string>? ErrorMessageRaised;

    public PushToTalkController(OverlayManager overlay, AppSettings settings)
    {
        _overlay = overlay;
        _settings = settings;
        _recorder = new MicrophoneRecorder();
        _playback = new AudioPlaybackService(settings.Audio.PreBufferMs, settings.Audio.PlaybackBufferSeconds);
        _screenCapture = new ScreenCaptureService();

        _provider = settings.Provider;
        _apiKey = CredentialStore.GetKey(CredentialStore.TargetForProvider(_provider));

        var isOpenAi = AiProviderFactory.IsOpenAi(_provider);
        _requireScreenshotBeforeAudio = isOpenAi
            ? settings.OpenAi.RequireScreenshotBeforeAudio
            : settings.Gemini.RequireScreenshotBeforeAudio;
        var historyTurns = isOpenAi ? settings.OpenAi.HistoryTurns : settings.Gemini.HistoryTurns;
        _history = ConversationHistory.Load(HistoryPath, historyTurns);

        if (!string.IsNullOrEmpty(_apiKey))
            _flashPointing = AiProviderFactory.CreatePointingService(_provider, _apiKey, settings);

        _recorder.LevelsUpdated += levels =>
        {
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                _overlay.SetWaveformLevels(levels));
        };

        _playback.PlaybackCompleted += OnPlaybackCompleted;
    }

    public void OnHotkeyPressed()
    {
        CancellationTokenSource? oldCts = null;
        IRealtimeVoiceService? oldGemini = null;
        int turnVersion;
        CancellationToken token;

        lock (_stateLock)
        {
            // Ignore key-repeat events while already in the Recording state.
            if (_state == AppState.Recording)
                return;

            _isHotkeyHeld = true;
            turnVersion = ++_turnVersion;

            if (_state == AppState.Speaking || _state == AppState.Processing)
            {
                // Capture old resources; they will be cleaned up OUTSIDE the lock so
                // that disposal callbacks cannot re-acquire _stateLock and deadlock.
                oldCts = _interactionCts;
                oldGemini = _gemini;
                _interactionCts = null;
                _gemini = null;
            }

            _state = AppState.Recording;
            _interactionCts = new CancellationTokenSource();
            token = _interactionCts.Token;
        }

        // All I/O and disposal happen outside the lock.
        if (oldCts != null)
        {
            oldCts.Cancel();
            oldCts.Dispose();
        }
        if (oldCts != null || oldGemini != null)
        {
            _recorder.DataAvailable -= OnAudioData;
            _recorder.StopRecording();
            _playback.Stop();
        }
        if (oldGemini != null)
            DisposeGeminiInstance(oldGemini);

        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            _overlay.SetState(AppState.Recording));

        _ = StartRecordingAsync(turnVersion, token);
    }

    public void OnHotkeyReleased()
    {
        bool shouldStop = false;
        lock (_stateLock)
        {
            _isHotkeyHeld = false;
            if (_state == AppState.Recording)
                shouldStop = true;
        }

        if (shouldStop)
            _ = StopRecordingAndProcessAsync();
    }

    private async Task StartRecordingAsync(int turnVersion, CancellationToken token)
    {
        if (string.IsNullOrEmpty(_apiKey))
        {
            Log.Warning("{Provider} API key not configured", _provider);
            TransitionToIdle();
            return;
        }

        try
        {
            // Await the previous session's disposal so the old WebSocket is fully
            // torn down before we open a new one. Bounded by the new turn's token.
            var prevDisposal = Interlocked.Exchange(ref _geminiDisposalTask, null);
            if (prevDisposal != null)
            {
                try { await prevDisposal.WaitAsync(token); }
                catch (OperationCanceledException) { throw; }
                catch { /* disposal errors are non-fatal */ }
            }

            lock (_stateLock)
            {
                if (_state != AppState.Recording || turnVersion != _turnVersion) return;
            }

            var gemini = AiProviderFactory.CreateVoiceService(_provider, _apiKey, _settings);
            AttachGeminiHandlers(gemini);

            // Assign under lock so any concurrent interrupt sees the new instance.
            lock (_stateLock)
            {
                if (_state != AppState.Recording || turnVersion != _turnVersion)
                {
                    // Turn was superseded while we were creating the instance.
                    DetachGeminiHandlers(gemini);
                    _ = gemini.DisposeAsync().AsTask();
                    return;
                }
                _gemini = gemini;
            }

            _playback.Initialize(new NAudio.Wave.WaveFormat(24000, 16, 1));
            lock (_audioBuffer) { _audioBuffer.Clear(); }

            // Create fresh coordination signal before starting recording so
            // StopRecordingAndProcessAsync can await it regardless of timing.
            var screenshotReadyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _screenshotReadyTcs = screenshotReadyTcs;

            _recorder.DataAvailable -= OnAudioData; // prevent double-subscription
            _recorder.DataAvailable += OnAudioData;
            _recorder.StartRecording(gemini.InputSampleRateHz);

            _pendingScreenshot = null;
            _pendingUserTranscript = null;
            _pendingAssistantText = null;
            _canStreamAudioToGemini = !_requireScreenshotBeforeAudio;

            // Fetch monitors before ConnectAsync so the session's system instruction
            // can include exact screen dimensions for accurate POINT coordinate bounds.
            var monitors = _overlay.GetMonitors();

            // History and monitor geometry are embedded into systemInstruction inside ConnectAsync.
            await gemini.ConnectAsync(_history.GetHistory(), monitors, token);

            lock (_stateLock)
            {
                // Proceed even when the hotkey was released during ConnectAsync (state == Processing).
                // StopRecordingAndProcessAsync holds off on audioStreamEnd until screenshotReadyTcs
                // completes, so Gemini always receives context before the turn is closed.
                if (turnVersion != _turnVersion || (_state != AppState.Recording && _state != AppState.Processing))
                {
                    screenshotReadyTcs.TrySetResult(false);
                    return;
                }
            }

            var captures = _screenCapture.CaptureAll(monitors);
            // Save captures so OnGeminiInputTranscription can pass them to the Flash pointing call.
            _lastScreenCaptures = captures;
            if (captures.Any())
            {
                // Send the focused monitor's screenshot — the same one Flash pointing uses.
                // captures.First() could be a background monitor; IsFocus tracks the primary.
                var focusedCapture = captures.FirstOrDefault(c => c.IsFocus) ?? captures.First();
                _pendingScreenshot = focusedCapture.Base64Jpeg;
                await gemini.SendScreenshotAsync(_pendingScreenshot, token);
                _canStreamAudioToGemini = true;
                await FlushBufferedAudioAsync(gemini, token);
            }
            else if (_requireScreenshotBeforeAudio)
            {
                // If capture fails, avoid deadlocking turn audio and continue with audio-only.
                _canStreamAudioToGemini = true;
                await FlushBufferedAudioAsync(gemini, token);
            }
            screenshotReadyTcs.TrySetResult(true);
        }
        catch (OperationCanceledException)
        {
            // Normal path when turn is interrupted — no transition needed; the
            // interrupt that cancelled the token already set up the next state.
            _screenshotReadyTcs?.TrySetResult(false);
        }
        catch (Exception ex)
        {
            _screenshotReadyTcs?.TrySetResult(false);
            Log.Error(ex, "Failed to start {Provider} recording", _provider);
            // The most common cause is a rejected/misconfigured key. Surface it instead of failing
            // silently back to idle (which looks like the app is dead).
            ErrorMessageRaised?.Invoke(BuildConnectionErrorMessage());
            TransitionToIdle();
        }
    }

    private string BuildConnectionErrorMessage() =>
        AiProviderFactory.IsOpenAi(_provider)
            ? "Couldn't reach OpenAI. Check your API key and internet connection, and that the key has Realtime API access."
            : "Couldn't reach Gemini. Check your API key and internet connection. As of June 19, 2026 Gemini rejects unrestricted API keys — in Google AI Studio, restrict your key to the Gemini API, or create a new key.";

    private async void OnAudioData(byte[] buffer, int count)
    {
        try
        {
            // Capture both references at entry so they can't be nulled mid-method.
            var gemini = _gemini;
            var cts = _interactionCts;

            if (gemini == null) return;
            lock (_stateLock)
            {
                if (_state != AppState.Recording) return;
            }

            var copy = new byte[count];
            Buffer.BlockCopy(buffer, 0, copy, 0, count);

            CancellationToken token;
            try { token = cts?.Token ?? CancellationToken.None; }
            catch (ObjectDisposedException) { return; }

            if (!gemini.IsConnected || !_canStreamAudioToGemini)
            {
                lock (_audioBuffer) { _audioBuffer.Enqueue(copy); }
            }
            else
            {
                // Flush any audio buffered before the WebSocket and screenshot were ready.
                await FlushBufferedAudioAsync(gemini, token);
                await gemini.SendAudioAsync(copy, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error streaming audio to Gemini");
        }
    }

    private async Task StopRecordingAndProcessAsync()
    {
        int capturedTurnVersion;
        lock (_stateLock)
        {
            if (_state != AppState.Recording)
                return;

            _state = AppState.Processing;
            capturedTurnVersion = _turnVersion;
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                _overlay.SetState(AppState.Processing));
        }

        _recorder.DataAvailable -= OnAudioData;
        _recorder.StopRecording();

        // Capture local ref so a concurrent interrupt can null _gemini safely.
        var gemini = _gemini;
        if (gemini == null || !gemini.IsConnected)
        {
            TransitionToIdle();
            return;
        }

        try
        {
            var cts = _interactionCts;
            CancellationToken token;
            try { token = cts?.Token ?? CancellationToken.None; }
            catch (ObjectDisposedException) { token = CancellationToken.None; }

            // If ConnectAsync was still in progress when the hotkey was released, StartRecordingAsync
            // may still be sending the screenshot + buffered audio. Wait for it so that Gemini
            // receives context before we signal end-of-turn with audioStreamEnd.
            var screenshotTcs = _screenshotReadyTcs;
            if (screenshotTcs != null && !screenshotTcs.Task.IsCompleted)
            {
                try { await screenshotTcs.Task.WaitAsync(TimeSpan.FromSeconds(5), token); }
                catch { /* proceed regardless — audioStreamEnd must be sent */ }
            }

            // Recover automatically if Gemini doesn't respond within the timeout.
            // This guards against the rare case where Gemini receives an empty turn
            // (e.g., near-zero audio) and never fires turnComplete.
            _ = EnforceProcessingTimeoutAsync(capturedTurnVersion, token);

            await gemini.CompleteTurnAsync(token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to complete turn");
            TransitionToIdle();
        }
    }

    private async Task EnforceProcessingTimeoutAsync(int capturedTurnVersion, CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15), token);
        }
        catch (OperationCanceledException)
        {
            return; // Normal: the state exited Processing before the timeout fired.
        }

        lock (_stateLock)
        {
            if (_state != AppState.Processing || _turnVersion != capturedTurnVersion) return;
        }

        Log.Warning("No response from Gemini within 15s in Processing state — recovering to Idle");
        TransitionToIdle();
    }

    private async void OnGeminiAudioReceived(byte[] pcmData)
    {
        try
        {
            bool shouldPlay = false;
            lock (_stateLock)
            {
                if (_state == AppState.Processing)
                {
                    _state = AppState.Speaking;
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                        _overlay.SetState(AppState.Speaking));
                    shouldPlay = true;
                }
                else if (_state == AppState.Speaking)
                {
                    shouldPlay = true;
                }
            }

            if (!shouldPlay) return;

            var cts = _interactionCts;
            CancellationToken token;
            try { token = cts?.Token ?? CancellationToken.None; }
            catch (ObjectDisposedException) { return; }

            if (token.IsCancellationRequested) return;

            await _playback.AddDataAsync(pcmData, pcmData.Length, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error(ex, "Error playing audio from Gemini");
        }
    }

    private void OnGeminiInputTranscription(string transcript)
    {
        _pendingUserTranscript = SelectBestTranscript(_pendingUserTranscript, transcript);
        Log.Information("User transcript: {Text}", _pendingUserTranscript ?? transcript);

        // Fire the Flash pointing call immediately in parallel with Live's audio response.
        // We capture turnVersion and captures by value so a concurrent new turn can't affect this call.
        var capturedTurnVersion = _turnVersion;
        var capturedCaptures = _lastScreenCaptures;
        var monitors = _overlay.GetMonitors();
        _ = Task.Run(() => RunFlashPointingAsync(transcript, capturedCaptures, monitors, capturedTurnVersion));
    }

    private async Task RunFlashPointingAsync(
        string userTranscript,
        List<ScreenCapture> screenCaptures,
        IReadOnlyList<MonitorInfo> monitors,
        int capturedTurnVersion)
    {
        try
        {
            // Use the primary (focus) monitor's capture, falling back to the first available.
            var primaryCapture = screenCaptures.FirstOrDefault(c => c.IsFocus)
                              ?? screenCaptures.FirstOrDefault();
            if (primaryCapture == null || _flashPointing == null) return;

            var primaryMonitor = monitors.Count > primaryCapture.MonitorIndex
                ? monitors[primaryCapture.MonitorIndex]
                : monitors.FirstOrDefault();
            if (primaryMonitor == null) return;

            CancellationToken token;
            try { token = _interactionCts?.Token ?? CancellationToken.None; }
            catch (ObjectDisposedException) { return; }

            var pointTarget = await _flashPointing.GetPointAsync(
                primaryCapture, userTranscript, primaryMonitor, token);

            if (pointTarget == null) return;

            // Drop stale results if the user started a new turn while Flash was in flight.
            lock (_stateLock)
            {
                if (_turnVersion != capturedTurnVersion) return;
            }

            var phrase = NavigationPhrases[Random.Shared.Next(NavigationPhrases.Length)];
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                _overlay.SetSpeechBubble(phrase);
                _overlay.StartFlightTo(
                    primaryMonitor.PhysicalBounds.Left + pointTarget.X,
                    primaryMonitor.PhysicalBounds.Top + pointTarget.Y,
                    primaryMonitor);
            });

            Log.Information("Flash pointing: ({X},{Y}) — {Label}", pointTarget.X, pointTarget.Y, pointTarget.Label);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warning(ex, "Flash pointing call failed — triangle will not move for this turn");
        }
    }

    private void OnGeminiTextChunkReceived(string textChunk)
    {
        Log.Verbose("Gemini partial text: {Text}", textChunk);
    }

    private void OnGeminiTextCompleted(string text)
    {
        Log.Information("Gemini text completed: {Text}", text);
        // Strip any stray POINT tags the Live model might emit before saving to history.
        _pendingAssistantText = PointTagRegex.Replace(text, "").Trim();
    }

    private void OnGeminiTurnComplete()
    {
        bool shouldSignalEndOfStream;
        bool shouldTransitionToIdle;
        lock (_stateLock)
        {
            // Ignore stale callbacks from a previous disposed session.
            if (_state != AppState.Processing && _state != AppState.Speaking)
                return;

            Log.Information("Gemini turn completed");
            shouldSignalEndOfStream = _state == AppState.Speaking;
            shouldTransitionToIdle = _state == AppState.Processing;
        }

        if (shouldSignalEndOfStream)
            _playback.SignalEndOfStream();

        if (shouldTransitionToIdle)
            TransitionToIdle();

        if (_pendingAssistantText != null)
        {
            var userText = _pendingUserTranscript ?? "User spoke (transcript unavailable).";
            _history.AddUserMessage(userText, _pendingScreenshot);
            _history.AddAssistantMessage(_pendingAssistantText);
            _history.Save(HistoryPath);
            _pendingAssistantText = null;
            _pendingUserTranscript = null;
            _pendingScreenshot = null;
        }
    }

    private void OnPlaybackCompleted()
    {
        lock (_stateLock)
        {
            if (_state != AppState.Speaking) return;
        }
        TransitionToIdle();
    }

    private void OnGeminiError(Exception ex)
    {
        lock (_stateLock)
        {
            // Ignore stale errors from prior sessions while a fresh recording is active.
            if (_state == AppState.Recording || _state == AppState.Idle)
            {
                Log.Warning(ex, "Ignoring stale Gemini error while state is {State}", _state);
                return;
            }
        }

        Log.Error(ex, "{Provider} service reported an error", _provider);
        ErrorMessageRaised?.Invoke(BuildConnectionErrorMessage());
        TransitionToIdle();
    }

    private void TransitionToIdle()
    {
        CancellationTokenSource? oldCts;
        IRealtimeVoiceService? oldGemini;

        lock (_stateLock)
        {
            if (_state == AppState.Idle) return; // idempotent
            _state = AppState.Idle;
            _turnVersion++;
            oldCts = _interactionCts;
            oldGemini = _gemini;
            _interactionCts = null;
            _gemini = null;
            // _isHotkeyHeld is physical key state — never cleared here
        }
        _canStreamAudioToGemini = false;

        // Cleanup OUTSIDE lock to prevent deadlock with playback callbacks.
        _recorder.DataAvailable -= OnAudioData;
        _recorder.StopRecording();
        lock (_audioBuffer) { _audioBuffer.Clear(); }
        oldCts?.Cancel();
        oldCts?.Dispose();
        _playback.Stop();
        if (oldGemini != null)
            DisposeGeminiInstance(oldGemini);

        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _overlay.SetState(AppState.Idle);
            _overlay.SetSpeechBubble("");
        });
    }

    private void AttachGeminiHandlers(IRealtimeVoiceService gemini)
    {
        gemini.AudioReceived += OnGeminiAudioReceived;
        gemini.TextCompleted += OnGeminiTextCompleted;
        gemini.TextChunkReceived += OnGeminiTextChunkReceived;
        gemini.InputTranscriptionReceived += OnGeminiInputTranscription;
        gemini.TurnComplete += OnGeminiTurnComplete;
        gemini.ErrorOccurred += OnGeminiError;
    }

    private void DetachGeminiHandlers(IRealtimeVoiceService gemini)
    {
        gemini.AudioReceived -= OnGeminiAudioReceived;
        gemini.TextCompleted -= OnGeminiTextCompleted;
        gemini.TextChunkReceived -= OnGeminiTextChunkReceived;
        gemini.InputTranscriptionReceived -= OnGeminiInputTranscription;
        gemini.TurnComplete -= OnGeminiTurnComplete;
        gemini.ErrorOccurred -= OnGeminiError;
    }

    private void DisposeGeminiInstance(IRealtimeVoiceService gemini)
    {
        try { DetachGeminiHandlers(gemini); }
        catch (Exception ex) { Log.Warning(ex, "Failed to detach Gemini event handlers cleanly"); }
        _geminiDisposalTask = gemini.DisposeAsync().AsTask();
    }

    private async Task FlushBufferedAudioAsync(IRealtimeVoiceService gemini, CancellationToken token)
    {
        while (true)
        {
            byte[]? queued = null;
            lock (_audioBuffer)
            {
                if (_audioBuffer.Count > 0)
                    queued = _audioBuffer.Dequeue();
            }

            if (queued == null) break;
            await gemini.SendAudioAsync(queued, token);
        }
    }

    private static string? SelectBestTranscript(string? existingTranscript, string incomingTranscript)
    {
        if (string.IsNullOrWhiteSpace(incomingTranscript))
            return existingTranscript;

        var trimmedIncoming = incomingTranscript.Trim();
        if (string.IsNullOrWhiteSpace(existingTranscript))
            return trimmedIncoming;

        var trimmedExisting = existingTranscript.Trim();
        if (trimmedIncoming.Length >= trimmedExisting.Length ||
            trimmedIncoming.StartsWith(trimmedExisting, StringComparison.OrdinalIgnoreCase))
        {
            return trimmedIncoming;
        }

        return trimmedExisting;
    }

    public void Dispose()
    {
        CancellationTokenSource? oldCts;
        IRealtimeVoiceService? oldGemini;

        lock (_stateLock)
        {
            _state = AppState.Idle;
            oldCts = _interactionCts;
            oldGemini = _gemini;
            _interactionCts = null;
            _gemini = null;
        }

        _recorder.DataAvailable -= OnAudioData;
        _recorder.StopRecording();
        oldCts?.Cancel();
        oldCts?.Dispose();
        _playback.Stop();
        if (oldGemini != null)
            DisposeGeminiInstance(oldGemini);

        _recorder.Dispose();
        _playback.Dispose();
    }
}
