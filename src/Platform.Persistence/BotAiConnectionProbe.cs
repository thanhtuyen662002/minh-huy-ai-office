using System.Net.Http.Headers;

namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed record BotAiProbeResult(bool Success, string Code);

public sealed class BotAiConnectionProbe(HttpClient client, IReadOnlySet<string> allowedHosts)
{
    public const string AnthropicHost = "api.anthropic.com";

    public async Task<BotAiProbeResult> TestAsync(BotAiConfig settings, CancellationToken ct = default)
    {
        var uri = new Uri(BotChannelJson.BaseUrl(settings.BaseUrl) + "/models");
        // Operator-managed exact host allowlist; UI cannot authorize arbitrary internal network probes.
        if (!allowedHosts.Contains(uri.IdnHost)) return new(false, "host_not_allowed");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (settings.ApiKey is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
            // Claude's native model list wants x-api-key; its chat endpoint (used by the bots) takes the Bearer key.
            if (string.Equals(uri.IdnHost, AnthropicHost, StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.TryAddWithoutValidation("x-api-key", settings.ApiKey);
                request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            }
        }
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            return response.IsSuccessStatusCode ? new(true, "connected") : new(false, "upstream_rejected");
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or InvalidOperationException)
        { return new(false, "connection_failed"); }
    }
}
