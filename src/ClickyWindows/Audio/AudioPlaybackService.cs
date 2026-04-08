using NAudio.Wave;
using Serilog;

namespace ClickyWindows.Audio;

/// <summary>
/// Plays audio using NAudio WasapiOut (shared mode, ~10-30ms latency).
/// Pre-buffers 200-300ms before starting playback to prevent underrun.
/// </summary>
public class AudioPlaybackService : IDisposable
{
    private WasapiOut? _wasapiOut;
    private BufferedWaveProvider? _buffer;
    private readonly int _preBufferMs;
    private bool _playbackStarted;
    private int _totalBytesBuffered;
    private CancellationTokenSource? _drainCts;

    public event Action? PlaybackCompleted;

    public AudioPlaybackService(int preBufferMs = 250)
    {
        _preBufferMs = preBufferMs;
    }

    public void Initialize(WaveFormat format)
    {
        Stop();

        _buffer = new BufferedWaveProvider(format)
        {
            BufferDuration = TimeSpan.FromSeconds(30),
            DiscardOnBufferOverflow = false
        };

        _wasapiOut = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, _preBufferMs);
        _wasapiOut.Init(_buffer);
        _wasapiOut.PlaybackStopped += OnPlaybackStopped;
        _playbackStarted = false;
        _totalBytesBuffered = 0;
    }

    /// <summary>
    /// Add audio data chunk. Starts playback once pre-buffer threshold is met.
    /// </summary>
    public void AddData(byte[] data, int count)
    {
        if (_buffer == null) return;

        _buffer.AddSamples(data, 0, count);
        _totalBytesBuffered += count;

        if (!_playbackStarted)
        {
            // Calculate how many bytes = preBufferMs
            int bytesPerMs = _buffer.WaveFormat.AverageBytesPerSecond / 1000;
            int threshold = bytesPerMs * _preBufferMs;

            if (_totalBytesBuffered >= threshold)
            {
                _playbackStarted = true;
                _wasapiOut?.Play();
                Log.Debug("Playback started after {Ms}ms pre-buffer", _preBufferMs);
            }
        }
    }

    /// <summary>Signal that no more data will arrive; flush remaining buffer.</summary>
    public void SignalEndOfStream()
    {
        if (!_playbackStarted && _buffer != null && _buffer.BufferedBytes > 0)
        {
            _playbackStarted = true;
            _wasapiOut?.Play();
            Log.Debug("Playback started (end-of-stream flush)");
        }

        // BufferedWaveProvider plays silence when empty — WasapiOut never stops on its own.
        // Poll until the buffer drains, then stop to fire PlaybackCompleted.
        if (_playbackStarted)
        {
            _drainCts = new CancellationTokenSource();
            _ = WaitForDrainThenStopAsync(_drainCts.Token);
        }
    }

    private async Task WaitForDrainThenStopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && _buffer != null && _buffer.BufferedBytes > 0)
                await Task.Delay(50, token);

            if (!token.IsCancellationRequested)
            {
                await Task.Delay(150, token); // flush device ring buffer
                Stop();
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Stop()
    {
        _drainCts?.Cancel();
        _drainCts = null;
        _wasapiOut?.Stop();
        _wasapiOut?.Dispose();
        _wasapiOut = null;
        _buffer = null;
        _playbackStarted = false;
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
            Log.Error(e.Exception, "Playback stopped with error");
        PlaybackCompleted?.Invoke();
    }

    public void Dispose() => Stop();
}
