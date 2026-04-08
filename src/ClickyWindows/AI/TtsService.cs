using System.Net.Http;
using System.Text;
using System.Text.Json;
using ClickyWindows.Audio;
using NAudio.Wave;
using Serilog;

namespace ClickyWindows.AI;

/// <summary>
/// Streams TTS audio from ElevenLabs via the Cloudflare Worker proxy.
/// Streams audio/mpeg chunks to AudioPlaybackService with pre-buffering.
/// </summary>
public class TtsService
{
    private readonly ProxyClient _proxy;
    private readonly AppSettings _settings;
    private readonly AudioPlaybackService _playback;

    public TtsService(ProxyClient proxy, AppSettings settings, AudioPlaybackService playback)
    {
        _proxy = proxy;
        _settings = settings;
        _playback = playback;
    }

    public async Task SpeakAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        var requestBody = JsonSerializer.Serialize(new
        {
            text,
            voice_id = _settings.ElevenLabs.VoiceId,
            model_id = "eleven_flash_v2_5",
            output_format = "mp3_44100_128"
        });

        var content = new StringContent(requestBody, Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _proxy.PostAsync("/tts", content,
                timeout: TimeSpan.FromSeconds(30),
                cancellationToken: cancellationToken);

            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "TTS request failed");
            return;
        }

        // Download the full MP3 response, then decode to PCM and feed playback
        var mp3Bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (mp3Bytes.Length == 0)
        {
            Log.Warning("TTS returned empty audio");
            return;
        }

        using var mp3Stream = new System.IO.MemoryStream(mp3Bytes);
        using var mp3Reader = new Mp3FileReader(mp3Stream);
        var pcmFormat = mp3Reader.WaveFormat;

        _playback.Initialize(pcmFormat);

        var pcmBuffer = new byte[4096];
        int bytesRead;

        while ((bytesRead = mp3Reader.Read(pcmBuffer, 0, pcmBuffer.Length)) > 0)
        {
            _playback.AddData(pcmBuffer, bytesRead);
            cancellationToken.ThrowIfCancellationRequested();
        }

        _playback.SignalEndOfStream();
        Log.Debug("TTS decoded: {Mp3Bytes} mp3 bytes → PCM", mp3Bytes.Length);
    }
}
