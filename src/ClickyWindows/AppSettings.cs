namespace ClickyWindows;

public class AppSettings
{
    // Selects which realtime voice provider answers each turn: "Gemini" or "OpenAI".
    public string Provider { get; set; } = "Gemini";
    public HotkeySettings Hotkey { get; set; } = new();
    public AudioSettings Audio { get; set; } = new();
    public GeminiSettings Gemini { get; set; } = new();
    public OpenAiSettings OpenAi { get; set; } = new();
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

public class GeminiSettings
{
    public string Model { get; set; } = "models/gemini-3.1-flash-live-preview";
    public string VoiceName { get; set; } = "Aoede";
    public int ConnectTimeoutMs { get; set; } = 5000;
    public string PointingModel { get; set; } = "models/gemini-pro-latest";
    public int HistoryTurns { get; set; } = 6;
    public bool RequireScreenshotBeforeAudio { get; set; } = true;
    public double Temperature { get; set; } = 0.1;
    public string MediaResolution { get; set; } = "MEDIA_RESOLUTION_HIGH";
}

public class OpenAiSettings
{
    // Realtime (voice) model — current GA model id; change here to pin a different snapshot.
    public string Model { get; set; } = "gpt-realtime-2";
    public string VoiceName { get; set; } = "marin";
    public int ConnectTimeoutMs { get; set; } = 5000;
    // Vision model used by OpenAiPointingService (chat completions) to locate UI elements.
    public string PointingModel { get; set; } = "gpt-4o";
    public int HistoryTurns { get; set; } = 6;
    public bool RequireScreenshotBeforeAudio { get; set; } = true;
    public double Temperature { get; set; } = 0.1;
}
