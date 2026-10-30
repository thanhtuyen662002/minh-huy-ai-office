extern alias RuntimeWorker;

using System.Net;
using System.Text;
using MinhHuyAiOffice.Shared.Contracts;
using RuntimeWorker::MinhHuy.AIOffice.Agent.Worker;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class CompanyAiSettingsGatewayTests
{
    private const string ChatReply = """
        {"model":"gemini-test","choices":[{"index":0,"message":{"role":"assistant","content":"Tồn kho đủ."}}],
         "usage":{"prompt_tokens":12,"completion_tokens":4,"total_tokens":16}}
        """;

    [Fact]
    public async Task Uses_company_shared_settings_through_chat_completions()
    {
        using var fixture = new BotChannelFixture();
        await using (var db = fixture.Database())
        {
            db.BotAiSettings.Add(new()
            {
                TenantId = fixture.Authority.TenantId,
                CompanyId = fixture.Authority.CompanyId,
                BaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai/",
                Model = "gemini-test",
                ProtectedApiKey = await fixture.Protector.ProtectAsync(fixture.Authority.TenantId, fixture.Authority.CompanyId, "company-key"),
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }
        var handler = new StubHandler(_ => Json(ChatReply));
        using var client = new HttpClient(handler);
        var fallback = new CountingGateway();
        await using var database = fixture.Database();

        var response = await new CompanyAiSettingsGateway(database, fixture.Protector, client, fallback).ExecuteAsync(Request(fixture));

        Assert.Equal("Tồn kho đủ.", response.Output);
        Assert.Equal("gemini-test", response.Model);
        Assert.Equal((12, 4), (response.InputTokens, response.OutputTokens));
        Assert.Equal(CompanyAiSettingsGateway.ProviderId, response.ProviderId);
        Assert.Equal(0, fallback.Calls);
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/openai/chat/completions", handler.Uri);
        Assert.Equal("Bearer company-key", handler.Authorization);
        Assert.Contains("\"model\":\"gemini-test\"", handler.Body, StringComparison.Ordinal);
        Assert.Contains("\"role\":\"user\"", handler.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("company-key", handler.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Falls_back_to_the_server_gateway_when_the_company_has_no_key()
    {
        using var fixture = new BotChannelFixture();
        await using (var db = fixture.Database())
        {
            db.BotAiSettings.Add(new() { TenantId = fixture.Authority.TenantId, CompanyId = fixture.Authority.CompanyId, Model = "gemini-test", UpdatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        var handler = new StubHandler(_ => throw new InvalidOperationException("must not call the company provider"));
        using var client = new HttpClient(handler);
        var fallback = new CountingGateway();
        await using var database = fixture.Database();

        var response = await new CompanyAiSettingsGateway(database, fixture.Protector, client, fallback).ExecuteAsync(Request(fixture));

        Assert.Equal(1, fallback.Calls);
        Assert.Equal("server", response.ProviderId);
        await using var empty = fixture.Database();
        var error = await Assert.ThrowsAsync<AiProviderExecutionException>(() =>
            new CompanyAiSettingsGateway(empty, fixture.Protector, client, null).ExecuteAsync(Request(fixture)));
        Assert.False(error.IsTransient);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    public async Task Chat_adapter_classifies_provider_failures(HttpStatusCode status, bool transient)
    {
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(status)));
        var adapter = new OpenAiCompatibleChatCompletionsAdapter(client, new Uri("https://provider.example/v1/chat/completions"), "m", "Bearer k", "p");

        var error = await Assert.ThrowsAsync<AiProviderExecutionException>(() =>
            adapter.ExecuteAsync(new AiGatewayRequest("tenant", "company", "task", "request", AiCapability.Reasoning, "q")));

        Assert.Equal(transient, error.IsTransient);
    }

    [Fact]
    public async Task Chat_adapter_rejects_a_reply_without_text()
    {
        using var client = new HttpClient(new StubHandler(_ => Json("""{"choices":[{"message":{"role":"assistant","content":""}}]}""")));
        var adapter = new OpenAiCompatibleChatCompletionsAdapter(client, new Uri("https://provider.example/v1/chat/completions"), "m", "Bearer k", "p");

        var error = await Assert.ThrowsAsync<AiProviderExecutionException>(() =>
            adapter.ExecuteAsync(new AiGatewayRequest("tenant", "company", "task", "request", AiCapability.Reasoning, "q")));

        Assert.False(error.IsTransient);
    }

    private static AiGatewayRequest Request(BotChannelFixture fixture) => new(
        fixture.Authority.TenantId.ToString("D"), fixture.Authority.CompanyId.ToString("D"), "task", "request", AiCapability.Reasoning, "Còn bao nhiêu hàng?");

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class CountingGateway : IAiGateway
    {
        public int Calls { get; private set; }

        public Task<AiGatewayResponse> ExecuteAsync(AiGatewayRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new AiGatewayResponse(request.RequestId, request.Capability, "server answer", "server-model", 1, 1) { ProviderId = "server" });
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public string Uri { get; private set; } = "";
        public string Authorization { get; private set; } = "";
        public string Body { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri!.ToString();
            Authorization = request.Headers.GetValues("Authorization").Single();
            Body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            return respond(request);
        }
    }
}
