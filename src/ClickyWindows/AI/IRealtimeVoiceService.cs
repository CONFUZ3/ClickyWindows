using ClickyWindows.Screen;

namespace ClickyWindows.AI;

/// <summary>
/// A realtime voice provider that streams microphone audio plus a screenshot to a model over a
/// bidirectional socket and streams back synthesized speech, transcripts, and turn signals.
/// Implemented by <see cref="GeminiLiveService"/> (Gemini Live) and
/// <see cref="OpenAiRealtimeService"/> (OpenAI Realtime). <see cref="PushToTalkController"/>
/// drives every turn purely through this contract so the active provider is interchangeable.
/// </summary>
public interface IRealtimeVoiceService : IAsyncDisposable
{
    /// <summary>Fires with raw PCM16 24kHz mono audio chunks produced by the model.</summary>
    event Action<byte[]>? AudioReceived;

    /// <summary>Fires with incremental assistant text/transcript chunks during a turn.</summary>
    event Action<string>? TextChunkReceived;

    /// <summary>Fires once per turn with the final assistant text.</summary>
    event Action<string>? TextCompleted;

    /// <summary>Fires with the transcription of the user's spoken input.</summary>
    event Action<string>? InputTranscriptionReceived;

    /// <summary>Fires with any error that should abort the turn.</summary>
    event Action<Exception>? ErrorOccurred;

    /// <summary>Fires when the model has finished its turn.</summary>
    event Action? TurnComplete;

    /// <summary>True while the underlying socket is open.</summary>
    bool IsConnected { get; }

    /// <summary>
    /// Sample rate (Hz) this provider expects for microphone PCM passed to <see cref="SendAudioAsync"/>.
    /// Gemini Live uses 16000; OpenAI Realtime uses 24000. The controller configures the mic to match.
    /// </summary>
    int InputSampleRateHz { get; }

    /// <summary>Opens the session, embedding prior history and monitor geometry as context.</summary>
    Task ConnectAsync(IReadOnlyList<ConversationHistory.Turn> history,
                      IReadOnlyList<MonitorInfo> monitors,
                      CancellationToken token);

    /// <summary>Streams a chunk of microphone PCM (mono, 16-bit, <see cref="InputSampleRateHz"/>).</summary>
    Task SendAudioAsync(byte[] pcmData, CancellationToken token);

    /// <summary>Sends the turn's screenshot (base64 JPEG) as visual context.</summary>
    Task SendScreenshotAsync(string base64Jpeg, CancellationToken token);

    /// <summary>Signals end of the user's turn so the model produces its response.</summary>
    Task CompleteTurnAsync(CancellationToken token);
}
