using ClickyWindows.Screen;

namespace ClickyWindows.AI;

/// <summary>
/// Decides whether the user's question implies pointing at a UI element and, if so, returns its
/// physical pixel coordinates on the given monitor. Runs as a separate vision call alongside the
/// realtime voice session. Implemented by <see cref="GeminiFlashPointingService"/> and
/// <see cref="OpenAiPointingService"/> so pointing follows the selected provider — an OpenAI user
/// never needs a Gemini key.
/// </summary>
public interface IPointingService
{
    /// <summary>
    /// Returns a <see cref="PointTarget"/> in physical pixels relative to the monitor's top-left,
    /// or null when no pointing is warranted.
    /// </summary>
    Task<PointTarget?> GetPointAsync(
        ScreenCapture screenshot,
        string userTranscript,
        MonitorInfo monitor,
        CancellationToken token);
}
