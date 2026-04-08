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

    // Services (initialized lazily once proxy URL is set)
    private ProxyClient? _proxy;
    private TranscriptionService? _transcription;
    private ClaudeService? _claude;
    private TtsService? _tts;
    private AudioPlaybackService _playback;
    private MicrophoneRecorder _recorder;
    private ScreenCaptureService _screenCapture;
    private ConversationHistory _history;

    private CancellationTokenSource? _interactionCts;
    private string _currentTranscript = "";
    private readonly StringBuilder _currentResponse = new();
    // Set to true once a finalized transcript is received for the current interaction,
    // so OnTranscriptionSessionEnded won't race and idle out a Claude call in progress.
    private volatile bool _transcriptReceived;

    private static readonly string[] NavigationPhrases =
        ["right here!", "click this!", "this one!", "over here!", "found it!"];

    public PushToTalkController(OverlayManager overlay, AppSettings settings)
    {
        _overlay = overlay;
        _settings = settings;
        _recorder = new MicrophoneRecorder();
        _playback = new AudioPlaybackService(settings.Audio.PreBufferMs);
        _screenCapture = new ScreenCaptureService();
        _history = new ConversationHistory(settings.Claude.MaxHistory);

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
        if (string.IsNullOrWhiteSpace(_settings.ProxyUrl) ||
            _settings.ProxyUrl.Contains("your-worker"))
        {
            Log.Warning("Proxy URL not configured — AI features disabled. Edit appsettings.json.");
            return;
        }

        _proxy = new ProxyClient(_settings.ProxyUrl);
        _claude = new ClaudeService(_proxy, _settings);
        _tts = new TtsService(_proxy, _settings, _playback);

        // AssemblyAI key is handled by the proxy — pass empty for now
        // In production, the proxy forwards audio to AssemblyAI
        // For direct use, set the key via environment variable ASSEMBLYAI_API_KEY
        var assemblyKey = Environment.GetEnvironmentVariable("ASSEMBLYAI_API_KEY") ?? "";
        _transcription = new TranscriptionService(assemblyKey, _settings);
        _transcription.TranscriptFinalized += OnTranscriptFinalized;
        _transcription.SessionEnded += OnTranscriptionSessionEnded;
        _transcription.PartialTranscript += _ =>
            Log.Debug("Partial transcript received");
        _transcription.Error += ex =>
            Log.Error(ex, "Transcription error");
    }

    public void OnHotkeyPressed()
    {
        lock (_stateLock)
        {
            if (_state == AppState.Speaking)
            {
                // Echo prevention: stop TTS, then start recording
                Log.Debug("Hotkey pressed during SPEAKING — stopping TTS");
                _playback.Stop();
                // Will transition via OnPlaybackCompleted or directly
            }

            if (_state == AppState.Idle || _state == AppState.Speaking)
            {
                StartRecording();
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

    private void StartRecording()
    {
        Log.Information("State: RECORDING");
        _state = AppState.Recording;
        _interactionCts = new CancellationTokenSource();
        _currentTranscript = "";
        _currentResponse.Clear();
        _transcriptReceived = false;

        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            _overlay.SetState(AppState.Recording));

        _recorder.StartRecording();
        _recorder.DataAvailable += OnAudioData;

        // Connect transcription WebSocket
        _ = ConnectTranscriptionAsync(_interactionCts.Token);
    }

    private async Task ConnectTranscriptionAsync(CancellationToken token)
    {
        if (_transcription == null)
        {
            Log.Warning("Transcription service not initialized (proxy not configured)");
            return;
        }

        try
        {
            await _transcription.ConnectAsync(token);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to connect transcription WebSocket");
        }
    }

    private async void OnAudioData(byte[] buffer, int count)
    {
        if (_transcription == null || _interactionCts?.IsCancellationRequested == true) return;

        try
        {
            await _transcription.SendAudioAsync(buffer, count, _interactionCts?.Token ?? CancellationToken.None);
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

        // If no transcription service configured, or the WebSocket never opened
        // (e.g., bad API key, network failure), SessionEnded will never fire — go idle now.
        if (_transcription == null || !_transcription.HasActiveSession)
        {
            Log.Warning("No active transcription session — returning to idle");
            TransitionToIdle();
            return;
        }

        // Send Terminate to flush any buffered audio; wait for TranscriptFinalized or SessionEnded.
        _ = TerminateTranscriptionAsync();
    }

    private async Task TerminateTranscriptionAsync()
    {
        if (_transcription == null) return;
        try
        {
            await _transcription.TerminateAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to terminate transcription session");
        }
    }

    private void OnTranscriptFinalized(string transcript)
    {
        // Guard: only act when we're actually waiting for a transcript.
        lock (_stateLock)
        {
            if (_state != AppState.Processing) return;
        }

        if (string.IsNullOrWhiteSpace(transcript))
        {
            TransitionToIdle();
            return;
        }

        // Mark before starting async work so OnTranscriptionSessionEnded
        // doesn't race and idle out the in-progress Claude call.
        _transcriptReceived = true;

        _currentTranscript = transcript;
        Log.Information("Transcript received ({Length} chars)", transcript.Length);
        _history.AddUserMessage(transcript);

        _ = RunClaudeAsync(transcript, _interactionCts?.Token ?? CancellationToken.None);
    }

    /// <summary>
    /// Called when the AssemblyAI WebSocket closes (normally, on error, or after Terminate).
    /// If we're still in PROCESSING and no transcript arrived, nothing else will unblock us — go idle.
    /// </summary>
    private void OnTranscriptionSessionEnded()
    {
        bool shouldIdle;
        lock (_stateLock)
        {
            shouldIdle = _state == AppState.Processing && !_transcriptReceived;
        }

        if (shouldIdle)
        {
            Log.Warning("Transcription session ended with no transcript — returning to idle");
            System.Windows.Application.Current?.Dispatcher.Invoke(TransitionToIdle);
        }
    }

    private async Task RunClaudeAsync(string userMessage, CancellationToken token)
    {
        if (_claude == null || _tts == null)
        {
            Log.Warning("AI services not initialized");
            TransitionToIdle();
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
            await foreach (var chunk in _claude.StreamResponseAsync(userMessage, _history, screenshotTuples, token))
            {
                _currentResponse.Append(chunk);

                // Parse POINT tags mid-stream
                var newPoints = PointParser.ParseAll(chunk, monitors);
                pointsFound.AddRange(newPoints);
            }

            var fullResponse = _currentResponse.ToString();

            Log.Information("Claude response: {Chars} chars, {Points} points",
                fullResponse.Length, pointsFound.Count);

            if (string.IsNullOrWhiteSpace(fullResponse))
            {
                // Claude returned nothing (e.g., proxy error) — go back to idle
                TransitionToIdle();
                return;
            }

            _history.AddAssistantMessage(fullResponse);

            // Transition to speaking
            Log.Information("State: SPEAKING");
            _state = AppState.Speaking;
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                _overlay.SetState(AppState.Speaking));

            // Animate to first POINT if present
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

            // Speak the response (strip POINT tags for TTS)
            var ttsText = System.Text.RegularExpressions.Regex.Replace(
                fullResponse, @"\[POINT:[^\]]+\]", "");

            await _tts.SpeakAsync(ttsText.Trim(), token);
        }
        catch (OperationCanceledException)
        {
            Log.Debug("Claude/TTS interaction cancelled");
            TransitionToIdle();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error during Claude/TTS interaction");
            TransitionToIdle();
        }
    }

    private void OnPlaybackCompleted()
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(TransitionToIdle);
    }

    private void TransitionToIdle()
    {
        lock (_stateLock)
        {
            Log.Information("State: IDLE");
            _state = AppState.Idle;
        }
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            _overlay.SetState(AppState.Idle);
            _overlay.SetSpeechBubble("");
        });
    }

    public void Dispose()
    {
        _interactionCts?.Cancel();
        _recorder.Dispose();
        _playback.Dispose();
        _proxy?.Dispose();
    }
}
