using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using ClickyWindows.Audio;
using NAudio.Wave;
using Serilog;

namespace ClickyWindows.AI;

/// <summary>
/// Streams TTS audio directly from the ElevenLabs API.
/// API key is read from Windows Credential Manager via CredentialStore.
/// </summary>
public class TtsService
{
    private readonly HttpClient _httpClient;
    private readonly AppSettings _settings;
    private readonly AudioPlaybackService _playback;

    public TtsService(HttpClient httpClient, AppSettings settings, AudioPlaybackService playback)
    {
        _httpClient = httpClient;
        _settings = settings;
        _playback = playback;
    }

    public async Task<SpeechResult> SpeakAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new SpeechResult(SpeechResultKind.Success);
        }

        var voiceId = _settings.ElevenLabs.VoiceId;
        var outputFormat = _settings.ElevenLabs.OutputFormat;
        var url = $"https://api.elevenlabs.io/v1/text-to-speech/{voiceId}/stream?output_format={Uri.EscapeDataString(outputFormat)}";

        var requestBody = JsonSerializer.Serialize(new
        {
            text,
            model_id = _settings.ElevenLabs.ModelId,
            voice_settings = new
            {
                stability = 0.5,
                similarity_boost = 0.75,
                style = 0.0,
                use_speaker_boost = true
            }
        });

        var attempts = Math.Max(1, _settings.ElevenLabs.RetryCount + 1);
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestCts.CancelAfter(TimeSpan.FromSeconds(_settings.ElevenLabs.RequestTimeoutSeconds));

            using var request = BuildRequest(url, requestBody);
            try
            {
                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    requestCts.Token);

                if (!response.IsSuccessStatusCode)
                {
                    var failure = await BuildFailureAsync(response, cancellationToken);
                    if (failure.IsRetryable && attempt < attempts)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(_settings.ElevenLabs.RetryBaseDelayMs * attempt), cancellationToken);
                        continue;
                    }

                    Log.Warning("TTS request failed with status {StatusCode}: {Message}", failure.StatusCode, failure.Message);
                    return new SpeechResult(SpeechResultKind.Failed, failure);
                }

                if (IsPcmOutputFormat(outputFormat))
                {
                    await StreamPcmAsync(response, outputFormat, cancellationToken);
                }
                else
                {
                    await StreamCompressedAsync(response, cancellationToken);
                }

                return new SpeechResult(SpeechResultKind.Success);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                if (attempt < attempts)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(_settings.ElevenLabs.RetryBaseDelayMs * attempt), cancellationToken);
                    continue;
                }

                return new SpeechResult(
                    SpeechResultKind.Failed,
                    new ServiceFailure(ServiceFailureKind.Timeout, "ElevenLabs request timed out.", IsRetryable: true, Exception: ex));
            }
            catch (OperationCanceledException)
            {
                return new SpeechResult(
                    SpeechResultKind.Cancelled,
                    new ServiceFailure(ServiceFailureKind.Cancelled, "Text-to-speech cancelled."));
            }
            catch (HttpRequestException ex)
            {
                if (attempt < attempts)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(_settings.ElevenLabs.RetryBaseDelayMs * attempt), cancellationToken);
                    continue;
                }

                return new SpeechResult(
                    SpeechResultKind.Failed,
                    new ServiceFailure(ServiceFailureKind.Network, "Failed to reach ElevenLabs.", IsRetryable: true, Exception: ex));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "TTS request failed");
                return new SpeechResult(
                    SpeechResultKind.Failed,
                    new ServiceFailure(ServiceFailureKind.Unknown, "Text-to-speech failed unexpectedly.", Exception: ex));
            }
        }

        return new SpeechResult(
            SpeechResultKind.Failed,
            new ServiceFailure(ServiceFailureKind.Unknown, "Text-to-speech exhausted all retries."));
    }

    private HttpRequestMessage BuildRequest(string url, string requestBody)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("xi-api-key", CredentialStore.GetKey(CredentialStore.ElevenLabsTarget));
        request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");
        return request;
    }

    private async Task StreamPcmAsync(HttpResponseMessage response, string outputFormat, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var waveFormat = BuildPcmWaveFormat(outputFormat);
        _playback.Initialize(waveFormat);

        var buffer = new byte[8192];
        int bytesRead;
        var totalBytes = 0;
        while ((bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
        {
            await _playback.AddDataAsync(buffer, bytesRead, cancellationToken);
            totalBytes += bytesRead;
        }

        if (totalBytes == 0)
        {
            throw new InvalidOperationException("TTS returned empty PCM audio.");
        }

        _playback.SignalEndOfStream();
        Log.Debug("TTS streamed {Bytes} bytes of PCM audio", totalBytes);
    }

    private async Task StreamCompressedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var memory = new MemoryStream();
        await responseStream.CopyToAsync(memory, cancellationToken);
        memory.Position = 0;

        if (memory.Length == 0)
        {
            throw new InvalidOperationException("TTS returned empty compressed audio.");
        }

        using var mp3Reader = new Mp3FileReader(memory);
        _playback.Initialize(mp3Reader.WaveFormat);

        var pcmBuffer = new byte[8192];
        int bytesRead;
        while ((bytesRead = mp3Reader.Read(pcmBuffer, 0, pcmBuffer.Length)) > 0)
        {
            await _playback.AddDataAsync(pcmBuffer, bytesRead, cancellationToken);
        }

        _playback.SignalEndOfStream();
        Log.Debug("TTS decoded {Bytes} bytes of compressed audio", memory.Length);
    }

    private static bool IsPcmOutputFormat(string outputFormat) =>
        outputFormat.StartsWith("pcm_", StringComparison.OrdinalIgnoreCase);

    private static WaveFormat BuildPcmWaveFormat(string outputFormat)
    {
        var parts = outputFormat.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && int.TryParse(parts[1], out var sampleRate))
        {
            return new WaveFormat(sampleRate, 16, 1);
        }

        return new WaveFormat(44100, 16, 1);
    }

    private static async Task<ServiceFailure> BuildFailureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch
        {
            body = string.Empty;
        }

        var statusCode = (int)response.StatusCode;
        var message = string.IsNullOrWhiteSpace(body) ? $"ElevenLabs returned HTTP {statusCode}." : body.Trim();
        var kind = statusCode switch
        {
            401 or 403 => ServiceFailureKind.Authentication,
            429 => ServiceFailureKind.RateLimited,
            >= 500 => ServiceFailureKind.Upstream,
            >= 400 => ServiceFailureKind.InvalidRequest,
            _ => ServiceFailureKind.Unknown
        };
        if (kind == ServiceFailureKind.Authentication)
        {
            message = "ElevenLabs authentication failed. Check the stored API key for this machine.";
        }
        else if (kind == ServiceFailureKind.RateLimited)
        {
            message = "ElevenLabs rate limited the request. Reduce concurrency or retry shortly.";
        }
        var retryable = statusCode == 429 || statusCode >= 500;
        return new ServiceFailure(kind, message, statusCode, retryable);
    }
}
