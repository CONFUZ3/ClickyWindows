using System.Net.WebSockets;

namespace ClickyWindows.AI;

public enum ServiceFailureKind
{
    None,
    Authentication,
    RateLimited,
    Timeout,
    Network,
    InvalidRequest,
    Upstream,
    Cancelled,
    Unknown
}

public sealed record ServiceFailure(
    ServiceFailureKind Kind,
    string Message,
    int? StatusCode = null,
    bool IsRetryable = false,
    Exception? Exception = null);

public enum ClaudeResponseKind
{
    Success,
    Empty,
    Incomplete,
    Failed,
    Cancelled
}

public sealed record ClaudeResponseResult(
    ClaudeResponseKind Kind,
    string Text,
    bool SawMessageStop,
    ServiceFailure? Failure = null);

public enum SpeechResultKind
{
    Success,
    Failed,
    Cancelled
}

public sealed record SpeechResult(
    SpeechResultKind Kind,
    ServiceFailure? Failure = null);

public enum TranscriptionSessionState
{
    Created,
    Connecting,
    Ready,
    Closing,
    Closed,
    Faulted
}

public enum TranscriptionEndKind
{
    Completed,
    Cancelled,
    ConnectFailed,
    ConnectTimeout,
    SessionStartTimeout,
    Authentication,
    RateLimited,
    Network,
    ServerClosed,
    Faulted
}

public sealed record TranscriptionSessionEndedEvent(
    TranscriptionEndKind Kind,
    string? SessionId,
    WebSocketCloseStatus? CloseStatus = null,
    string? CloseStatusDescription = null,
    ServiceFailure? Failure = null);
