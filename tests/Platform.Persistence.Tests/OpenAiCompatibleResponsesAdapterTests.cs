extern alias RuntimeWorker;

using System.Net;
using System.Text;
using MinhHuyAiOffice.Shared.Contracts;
using RuntimeWorker::MinhHuy.AIOffice.Agent.Worker;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class OpenAiCompatibleResponsesAdapterTests
{
    [Fact]
    public async Task ExecuteAsync_sends_bounded_responses_request_and_returns_usage()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {
                  "model":"model-test",
                  "output":[
                    {
                      "type":"message",
                      "content":[
                        {"type":"output_text","text":"Need ERP balance rows before I can answer."}
                      ]
                    }
                  ],
                  "usage":{"input_tokens":17,"output_tokens":9,"total_tokens":26}
                }
                """,
                Encoding.UTF8,
                "application/json")
        });
        using var client = new HttpClient(handler);
        var options = new OpenAiCompatibleResponsesOptions(
            new Uri("https://provider.example/v1/responses"),
            "model-test",
            "Bearer test-secret",
            "provider-test");
        var adapter = new OpenAiCompatibleResponsesAdapter(client, options);
        var request = new AiGatewayRequest(
            "tenant",
            "company",
            "task",
            "request",
            AiCapability.Reasoning,
            "bounded user question");

        var response = await adapter.ExecuteAsync(request);

        Assert.Equal("Need ERP balance rows before I can answer.", response.Output);
        Assert.Equal("model-test", response.Model);
        Assert.Equal(17, response.InputTokens);
        Assert.Equal(9, response.OutputTokens);
        Assert.Equal("provider-test", response.ProviderId);
        Assert.NotNull(handler.Request);
        Assert.Equal("Bearer test-secret", handler.Request!.Authorization);
        Assert.Contains("\"store\":false", handler.Request.Body, StringComparison.Ordinal);
        Assert.Contains("\"max_output_tokens\":1024", handler.Request.Body, StringComparison.Ordinal);
        Assert.Contains("bounded user question", handler.Request.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("test-secret", handler.Request.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    public async Task ExecuteAsync_classifies_provider_http_failures(
        HttpStatusCode statusCode,
        bool expectedTransient)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(statusCode));
        using var client = new HttpClient(handler);
        var adapter = new OpenAiCompatibleResponsesAdapter(
            client,
            new OpenAiCompatibleResponsesOptions(
                new Uri("https://provider.example/v1/responses"),
                "model-test",
                "Bearer test-secret",
                "provider-test"));
        var request = new AiGatewayRequest(
            "tenant",
            "company",
            "task",
            "request",
            AiCapability.Reasoning,
            "question");

        var exception = await Assert.ThrowsAsync<AiProviderExecutionException>(
            () => adapter.ExecuteAsync(request));

        Assert.Equal(expectedTransient, exception.IsTransient);
    }

    [Fact]
    public async Task Ordered_gateway_uses_primary_without_calling_backup()
    {
        var primary = new RecordingProviderAdapter("direct", responseText: "primary");
        var backup = new RecordingProviderAdapter("backup", responseText: "backup");
        var gateway = new OrderedFailoverAiGateway(primary, backup);

        var response = await gateway.ExecuteAsync(Request());

        Assert.Equal("primary", response.Output);
        Assert.Equal("direct", response.ProviderId);
        Assert.Equal(1, primary.CallCount);
        Assert.Equal(0, backup.CallCount);
    }

    [Fact]
    public async Task Ordered_gateway_uses_backup_after_transient_primary_failure()
    {
        var primary = new RecordingProviderAdapter(
            "direct",
            failure: new AiProviderExecutionException("temporary", true));
        var backup = new RecordingProviderAdapter("backup", responseText: "backup");
        var gateway = new OrderedFailoverAiGateway(primary, backup);

        var response = await gateway.ExecuteAsync(Request());

        Assert.Equal("backup", response.Output);
        Assert.Equal("backup", response.ProviderId);
        Assert.Equal(1, primary.CallCount);
        Assert.Equal(1, backup.CallCount);
    }

    [Fact]
    public async Task Ordered_gateway_does_not_hide_permanent_primary_failure()
    {
        var primaryFailure = new AiProviderExecutionException("bad auth", false);
        var primary = new RecordingProviderAdapter("direct", failure: primaryFailure);
        var backup = new RecordingProviderAdapter("backup", responseText: "backup");
        var gateway = new OrderedFailoverAiGateway(primary, backup);

        var exception = await Assert.ThrowsAsync<AiProviderExecutionException>(
            () => gateway.ExecuteAsync(Request()));

        Assert.Same(primaryFailure, exception);
        Assert.Equal(1, primary.CallCount);
        Assert.Equal(0, backup.CallCount);
    }

    private static AiGatewayRequest Request() => new(
        "tenant",
        "company",
        "task",
        "request",
        AiCapability.Reasoning,
        "question");

    private sealed class RecordingProviderAdapter(
        string providerId,
        string? responseText = null,
        AiProviderExecutionException? failure = null) : IAiProviderAdapter
    {
        public string ProviderId => providerId;

        public int CallCount { get; private set; }

        public bool Supports(AiCapability capability) => capability == AiCapability.Reasoning;

        public Task<AiGatewayResponse> ExecuteAsync(
            AiGatewayRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            if (failure is not null)
            {
                throw failure;
            }

            return Task.FromResult(
                new AiGatewayResponse(
                    request.RequestId,
                    request.Capability,
                    responseText ?? "ok",
                    $"{providerId}-model",
                    2,
                    1)
                {
                    ProviderId = providerId
                });
        }
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public CapturedRequest? Request { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Request = new CapturedRequest(
                request.Headers.Authorization?.ToString()
                    ?? request.Headers.GetValues("Authorization").SingleOrDefault()
                    ?? string.Empty,
                body);
            return responseFactory(request);
        }
    }

    private sealed record CapturedRequest(string Authorization, string Body);
}
