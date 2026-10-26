using System.Net;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class BotAiConnectionProbeTests
{
    [Fact]
    public async Task ProbeUsesStoredKeyModelsPathAndNeverReturnsUpstreamBodyOrKey()
    {
        var handler = new Handler(); using var client = new HttpClient(handler);
        var probe = new BotAiConnectionProbe(client, new HashSet<string>(["ai.example.test"], StringComparer.OrdinalIgnoreCase));
        var result = await probe.TestAsync(new("https://ai.example.test/v1", "model", "PRIVATE-KEY"));
        Assert.True(result.Success); Assert.Equal("https://ai.example.test/v1/models", handler.Uri);
        Assert.Equal("Bearer PRIVATE-KEY", handler.Authorization); Assert.DoesNotContain("PRIVATE", result.ToString());
    }
    [Theory]
    [InlineData("http://127.0.0.1:8080")]
    [InlineData("http://169.254.169.254")]
    [InlineData("https://ai.example.test.attacker.test")]
    public async Task ExactOperatorAllowlistIsRequiredBeforeNetworkRequest(string url)
    {
        var handler = new Handler(); using var client = new HttpClient(handler);
        var result = await new BotAiConnectionProbe(client, new HashSet<string>(["ai.example.test"], StringComparer.OrdinalIgnoreCase)).TestAsync(new(url, "model", "PRIVATE-KEY"));
        Assert.Equal("host_not_allowed", result.Code); Assert.Null(handler.Uri);
    }
    [Theory]
    [InlineData(401)]
    [InlineData(302)]
    [InlineData(500)]
    public async Task UpstreamErrorsAndRedirectsReturnOnlyFixedCodes(int status)
    {
        var handler = new Handler { Status = (HttpStatusCode)status }; using var client = new HttpClient(handler);
        var result = await new BotAiConnectionProbe(client, new HashSet<string>(["ai.example.test"])).TestAsync(new("https://ai.example.test", "model", "PRIVATE-KEY"));
        Assert.False(result.Success); Assert.Equal("upstream_rejected", result.Code); Assert.DoesNotContain("PRIVATE", result.ToString());
    }
    private sealed class Handler : HttpMessageHandler
    {
        internal string? Uri; internal string? Authorization; internal HttpStatusCode Status = HttpStatusCode.OK;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Uri = request.RequestUri!.AbsoluteUri; Authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent("PRIVATE-UPSTREAM-BODY") });
        }
    }
}
