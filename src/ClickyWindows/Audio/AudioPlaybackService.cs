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
    private readonly int _bufferSeconds;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _playbackStarted;
    private int _totalBytesBuffered;
    private CancellationTokenSource? _drainCts;

    public event Action? PlaybackCompleted;

    public AudioPlaybackService(int preBufferMs = 250, int bufferSeconds = 45)
    {
        _preBufferMs = preBufferMs;
        _bufferSeconds = bufferSeconds;
    }

    public void Initialize(WaveFormat format)
    {
        Stop();

        _buffer = new BufferedWaveProvider(format)
        {
            BufferDuration = TimeSpan.FromSeconds(_bufferSeconds),
            DiscardOnBufferOverflow = false
        };

        _wasapiOut = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, _preBufferMs);
        _wasapiOut.Init(_buffer);
        _wasapiOut.PlaybackStopped += OnPlaybackStopped;
        _playbackStarted = false;
        _totalBytesBuffered = 0;
    }

    /// <summary>
    /// Add audio data chunk with backpressure. Starts playback once pre-buffer threshold is met.
    /// </summary>
    public async Task AddDataAsync(byte[] data, int count, CancellationToken cancellationToken = default)
    {
        if (_buffer == null) return;

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            while (_buffer != null && _buffer.BufferedBytes + count > _buffer.BufferLength)
            {
                await Task.Delay(20, cancellationToken);
            }

            if (_buffer == null)
            {
                return;
            }

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
        finally
        {
            _writeLock.Release();
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
        // Null the field BEFORE stopping so OnPlaybackStopped's reference check
        // fails for this instance and PlaybackCompleted is not raised spuriously.
        var wasapi = _wasapiOut;
        _wasapiOut = null;
        _buffer = null;
        _playbackStarted = false;
        wasapi?.Stop();
        wasapi?.Dispose();
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
            Log.Error(e.Exception, "Playback stopped with error");
        // Only raise PlaybackCompleted for the currently active WasapiOut instance.
        // Stale events from a disposed instance (or a manually stopped one) are ignored.
        if (ReferenceEquals(sender, _wasapiOut))
            PlaybackCompleted?.Invoke();
    }

    public void Dispose() => Stop();
}
