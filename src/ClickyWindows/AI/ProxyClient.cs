using System.Net.Http;
using System.Net.Http.Headers;
using Serilog;

namespace ClickyWindows.AI;

/// <summary>
/// HttpClient wrapper with per-service timeouts.
/// API keys never touch this client — they live in the Cloudflare Worker proxy.
/// </summary>
public class ProxyClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;

    public ProxyClient(string baseUrl)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _httpClient = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan // we use per-request timeouts
        };
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<HttpResponseMessage> PostAsync(
        string path,
        HttpContent content,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout.HasValue)
            cts.CancelAfter(timeout.Value);

        var url = $"{_baseUrl}/{path.TrimStart('/')}";
        Log.Debug("POST {Url}", url);

        int attempt = 0;
        while (true)
        {
            try
            {
                var response = await _httpClient.PostAsync(url, content, cts.Token);
                return response;
            }
            catch (Exception ex) when (attempt < 2 && !cts.IsCancellationRequested)
            {
                attempt++;
                Log.Warning(ex, "Request to {Url} failed (attempt {Attempt}), retrying in {Delay}ms",
                    url, attempt, attempt * 500);
                await Task.Delay(attempt * 500, cancellationToken);
            }
        }
    }

    public void Dispose() => _httpClient.Dispose();
}
