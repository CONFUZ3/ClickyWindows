namespace ClickyWindows;

public class AppSettings
{
    public string ProxyUrl { get; set; } = "https://your-worker.your-subdomain.workers.dev";
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
}

public class AssemblyAISettings
{
    public string SpeechModel { get; set; } = "u3-rt-pro";
    public int MinTurnSilenceMs { get; set; } = 100;
    public int MaxTurnSilenceMs { get; set; } = 1000;
}

public class ClaudeSettings
{
    public string Model { get; set; } = "claude-sonnet-4-6";
    public int MaxHistory { get; set; } = 10;
}

public class ElevenLabsSettings
{
    public string VoiceId { get; set; } = "21m00Tcm4TlvDq8ikWAM";
}
