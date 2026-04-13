namespace ClickyWindows;

public class AppSettings
{
    public HotkeySettings Hotkey { get; set; } = new();
    public AudioSettings Audio { get; set; } = new();
    public GeminiSettings Gemini { get; set; } = new();
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
}
