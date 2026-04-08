namespace ClickyWindows;

public class AppSettings
{
    public HotkeySettings Hotkey { get; set; } = new();
    public AudioSettings Audio { get; set; } = new();
    public AssemblyAISettings AssemblyAI { get; set; } = new();
    public ClaudeSettings Claude { get; set; } = new();
    public ElevenLabsSettings ElevenLabs { get; set; } = new();
}

public class HotkeySettings
{
    public string Key { get; set; } = "Menu";
    public string Modifiers { get; set; } = "Control";
}

public class AudioSettings
{
    public int SampleRate { get; set; } = 16000;
    public int PreBufferMs { get; set; } = 250;
    public int PlaybackBufferSeconds { get; set; } = 45;
}

public class AssemblyAISettings
{
    public string SpeechModel { get; set; } = "u3-rt-pro";
    public int MinTurnSilenceMs { get; set; } = 100;
    public int MaxTurnSilenceMs { get; set; } = 1000;
    public int ConnectTimeoutMs { get; set; } = 5000;
    public int SessionReadyTimeoutMs { get; set; } = 4000;
    public int RetryCount { get; set; } = 2;
    public int RetryBaseDelayMs { get; set; } = 500;
    public int InactivityTimeoutSeconds { get; set; } = 20;
}

public class ClaudeSettings
{
    public string Model { get; set; } = "claude-sonnet-4-6";
    public int MaxHistory { get; set; } = 10;
    public int MaxTokens { get; set; } = 1024;
    public int RequestTimeoutSeconds { get; set; } = 120;
    public int StreamIdleTimeoutSeconds { get; set; } = 45;
    public int RetryCount { get; set; } = 2;
    public int RetryBaseDelayMs { get; set; } = 500;
}

public class ElevenLabsSettings
{
    public string VoiceId { get; set; } = "21m00Tcm4TlvDq8ikWAM";
    public string ModelId { get; set; } = "eleven_flash_v2_5";
    public string OutputFormat { get; set; } = "pcm_44100";
    public int RequestTimeoutSeconds { get; set; } = 45;
    public int RetryCount { get; set; } = 2;
    public int RetryBaseDelayMs { get; set; } = 500;
}
