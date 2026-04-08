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

    private GeminiLiveService? _gemini;
    private readonly AudioPlaybackService _playback;
    private readonly MicrophoneRecorder _recorder;
    private readonly ScreenCaptureService _screenCapture;
    private readonly string? _geminiApiKey;

    private CancellationTokenSource? _interactionCts;

    private static readonly string[] NavigationPhrases =
        ["right here!", "click this!", "this one!", "over here!", "found it!"];

    public PushToTalkController(OverlayManager overlay, AppSettings settings)
    {
        _overlay = overlay;
        _settings = settings;
        _recorder = new MicrophoneRecorder();
        _playback = new AudioPlaybackService(settings.Audio.PreBufferMs, settings.Audio.PlaybackBufferSeconds);
        _screenCapture = new ScreenCaptureService();

        _geminiApiKey = CredentialStore.GetKey(CredentialStore.GeminiTarget);

        _recorder.LevelsUpdated += levels =>
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                _overlay.SetWaveformLevels(levels));
        };

        _playback.PlaybackCompleted += OnPlaybackCompleted;
    }

    public void OnHotkeyPressed()
    {
        lock (_stateLock)
        {
            if (_state == AppState.Speaking)
            {
                CancelCurrentTurnLocked();
            }

            if (_state == AppState.Idle || _state == AppState.Speaking)
            {
                _ = StartRecordingAsync();
            }
        }
    }

    public void OnHotkeyReleased()
    {
        lock (_stateLock)
        {
            if (_state == AppState.Recording)
            {
                _ = StopRecordingAndProcessAsync();
            }
        }
    }

    private async Task StartRecordingAsync()
    {
        if (string.IsNullOrEmpty(_geminiApiKey))
        {
            Log.Warning("Gemini API key not configured");
            return;
        }

        lock (_stateLock)
        {
            _state = AppState.Recording;
        }

        _interactionCts?.Dispose();
        _interactionCts = new CancellationTokenSource();

        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            _overlay.SetState(AppState.Recording));

        try
        {
            if (_gemini == null)
            {
                _gemini = new GeminiLiveService(_geminiApiKey, _settings.Gemini);
                _gemini.AudioReceived += OnGeminiAudioReceived;
                _gemini.TextCompleted += OnGeminiTextCompleted;
                _gemini.TextChunkReceived += OnGeminiTextChunkReceived;
                _gemini.TurnComplete += OnGeminiTurnComplete;
                _gemini.ErrorOccurred += OnGeminiError;
            }

            await _gemini.ConnectAsync(_interactionCts.Token);

            var monitors = _overlay.GetMonitors();
            var captures = _screenCapture.CaptureAll(monitors);
            if (captures.Any())
            {
                await _gemini.SendScreenshotAsync(captures.First().Base64Jpeg, _interactionCts.Token);
            }

            // Gemini audio output is typically 24kHz 16-bit mono
            _playback.Initialize(new NAudio.Wave.WaveFormat(24000, 16, 1));
            
            _recorder.DataAvailable += OnAudioData;
            _recorder.StartRecording();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to start Gemini recording");
            TransitionToIdle();
        }
    }

    private async void OnAudioData(byte[] buffer, int count)
    {
        try
        {
            if (_gemini != null && _state == AppState.Recording)
            {
                var copy = new byte[count];
                Buffer.BlockCopy(buffer, 0, copy, 0, count);
                await _gemini.SendAudioAsync(copy, _interactionCts?.Token ?? CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error streaming audio to Gemini");
        }
    }

    private async Task StopRecordingAndProcessAsync()
    {
        lock (_stateLock)
        {
            _state = AppState.Processing;
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                _overlay.SetState(AppState.Processing));
        }

        _recorder.DataAvailable -= OnAudioData;
        _recorder.StopRecording();

        if (_gemini != null)
        {
            try
            {
                await _gemini.CompleteTurnAsync(_interactionCts?.Token ?? CancellationToken.None);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to complete turn");
                TransitionToIdle();
            }
        }
    }

    private async void OnGeminiAudioReceived(byte[] pcmData)
    {
        try
        {
            lock (_stateLock)
            {
                if (_state == AppState.Processing)
                {
                    _state = AppState.Speaking;
                    System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                        _overlay.SetState(AppState.Speaking));
                }
            }
            
            await _playback.AddDataAsync(pcmData, pcmData.Length, _interactionCts?.Token ?? CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error playing audio from Gemini");
        }
    }

    private void OnGeminiTextChunkReceived(string textChunk)
    {
        Log.Verbose("Gemini partial text: {Text}", textChunk);
    }

    private void OnGeminiTextCompleted(string text)
    {
        Log.Information("Gemini text completed: {Text}", text);

        var monitors = _overlay.GetMonitors();
        var points = PointParser.ParseAll(text, monitors);

        if (points.Count > 0)
        {
            var pt = points[0];
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
    }

    private void OnGeminiTurnComplete()
    {
        Log.Information("Gemini turn completed");
        _playback.SignalEndOfStream();
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
        Log.Error(ex, "Gemini service reported an error");
        TransitionToIdle();
    }

    private void TransitionToIdle()
    {
        lock (_stateLock)
        {
            _state = AppState.Idle;
        }

        _recorder.DataAvailable -= OnAudioData;
        _recorder.StopRecording();
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

        if (_gemini != null)
        {
            _ = _gemini.DisposeAsync().AsTask();
            _gemini = null;
        }
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            CancelCurrentTurnLocked();
        }
        _interactionCts?.Dispose();
        _recorder.Dispose();
        _playback.Dispose();
    }
}
