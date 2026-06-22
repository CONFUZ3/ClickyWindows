using NAudio.Wave;
using Serilog;

namespace ClickyWindows.Audio;

/// <summary>
/// Records microphone input using NAudio WaveInEvent as mono PCM16. The sample rate is chosen
/// per voice provider (Gemini Live = 16kHz, OpenAI Realtime = 24kHz) and passed to StartRecording.
/// </summary>
public class MicrophoneRecorder : IDisposable
{
    private WaveInEvent? _waveIn;
    private readonly AudioLevelMonitor _levelMonitor = new();
    private bool _recording;

    public event Action<byte[], int>? DataAvailable;
    public event Action<double[]>? LevelsUpdated;

    public AudioLevelMonitor LevelMonitor => _levelMonitor;

    public void StartRecording(int sampleRateHz = 16000)
    {
        if (_recording) return;

        _waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(sampleRateHz, 16, 1), // PCM16, mono
            BufferMilliseconds = 100
        };

        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.RecordingStopped += OnRecordingStopped;

        _levelMonitor.Reset();
        _recording = true;
        _waveIn.StartRecording();
        Log.Debug("Microphone recording started");
    }

    public void StopRecording()
    {
        if (!_recording || _waveIn == null) return;
        _recording = false;
        _waveIn.StopRecording();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0) return;

        var buffer = new byte[e.BytesRecorded];
        Buffer.BlockCopy(e.Buffer, 0, buffer, 0, e.BytesRecorded);

        _levelMonitor.Process(buffer, e.BytesRecorded);
        DataAvailable?.Invoke(buffer, e.BytesRecorded);
        LevelsUpdated?.Invoke(_levelMonitor.GetBarLevels());
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
            Log.Error(e.Exception, "Recording stopped with error");
        else
            Log.Debug("Microphone recording stopped");

        if (sender is WaveInEvent stopped)
        {
            stopped.Dispose();
            if (ReferenceEquals(_waveIn, stopped))
                _waveIn = null;
        }
    }

    public void Dispose()
    {
        StopRecording();
        _waveIn?.Dispose();
    }
}
