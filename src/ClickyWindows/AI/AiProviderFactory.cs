namespace ClickyWindows.AI;

/// <summary>
/// Builds the realtime voice and pointing services for the configured provider.
/// Keeps the provider-switch logic in one place so the controller stays provider-agnostic.
/// </summary>
public static class AiProviderFactory
{
    public static bool IsOpenAi(string provider) =>
        provider?.Trim().Equals("openai", StringComparison.OrdinalIgnoreCase) == true;

    public static IRealtimeVoiceService CreateVoiceService(string provider, string apiKey, AppSettings settings) =>
        IsOpenAi(provider)
            ? new OpenAiRealtimeService(apiKey, settings.OpenAi)
            : new GeminiLiveService(apiKey, settings.Gemini);

    public static IPointingService CreatePointingService(string provider, string apiKey, AppSettings settings) =>
        IsOpenAi(provider)
            ? new OpenAiPointingService(apiKey, settings.OpenAi.PointingModel)
            : new GeminiFlashPointingService(apiKey, settings.Gemini.PointingModel);
}
