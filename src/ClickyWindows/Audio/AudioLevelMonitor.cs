namespace ClickyWindows.Audio;

/// <summary>
/// Computes RMS audio level from PCM16 samples with exponential moving average smoothing.
/// </summary>
public class AudioLevelMonitor
{
    private double _smoothedRms;
    private const double Alpha = 0.3; // EMA smoothing factor

    public double SmoothedRms => _smoothedRms;

    /// <summary>
    /// Process a PCM16 byte buffer, compute RMS, and update smoothed value.
    /// Returns the smoothed RMS in [0, 1].
    /// </summary>
    public double Process(byte[] buffer, int bytesRecorded)
    {
        if (bytesRecorded <= 0) return _smoothedRms;

        double sumSquares = 0;
        int sampleCount = bytesRecorded / 2;

        for (int i = 0; i < bytesRecorded - 1; i += 2)
        {
            short sample = (short)(buffer[i] | (buffer[i + 1] << 8));
            double normalized = sample / 32768.0;
            sumSquares += normalized * normalized;
        }

        double rms = sampleCount > 0 ? Math.Sqrt(sumSquares / sampleCount) : 0;
        _smoothedRms = Alpha * rms + (1 - Alpha) * _smoothedRms;
        return _smoothedRms;
    }

    /// <summary>
    /// Convert smoothed RMS to 5 bar levels with per-band variation.
    /// </summary>
    public double[] GetBarLevels()
    {
        var levels = new double[5];
        var rng = Random.Shared;
        for (int i = 0; i < 5; i++)
        {
            // Center bar is tallest, edges shorter, add some noise
            double bandFactor = 1.0 - Math.Abs(i - 2) * 0.15;
            double noise = rng.NextDouble() * 0.1;
            levels[i] = Math.Clamp(_smoothedRms * bandFactor + noise * _smoothedRms, 0, 1);
        }
        return levels;
    }

    public void Reset() => _smoothedRms = 0;
}
