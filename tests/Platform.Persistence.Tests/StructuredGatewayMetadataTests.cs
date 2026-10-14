extern alias RuntimeWorker;

using System.Net;
using System.Text.Json;
using MinhHuyAiOffice.Shared.Contracts;
using RuntimeWorker::MinhHuy.AIOffice.Agent.Worker;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

// Fake HTTP controls only; these do not qualify a model or tokenizer.
public sealed class StructuredGatewayMetadataTests
{
    private static OpenAiCompatibleResponsesOptions Options(string provider = "primary", string model = "configured-model") =>
        new(new("https://provider.example/private-path/responses"), model, "Bearer synthetic-secret", provider);

    [Fact]
    public void Discovery_uses_actual_instances_without_HTTP_and_never_exposes_auth_or_endpoint()
    {
        var calls = 0;
        using var client = new HttpClient(new Handler(_ => { calls++; throw new InvalidOperationException("Unexpected HTTP"); }));
        var original = Options();
        var gateway = new OrderedFailoverAiGateway(new OpenAiCompatibleResponsesAdapter(client, original));
        var metadata = ((IAiStructuredGatewayMetadataSource)gateway).DescribeStructuredGateway();
        var route = Assert.Single(metadata.Routes);
        Assert.Equal(original.Model, route.ConfiguredModel);
        Assert.Equal(original.ProviderId, route.ProviderId);
        Assert.Equal(original.MaxOutputTokens, route.MaxOutputTokens);
        Assert.Equal(original.StructuredTimeout.Ticks, route.PerRouteTimeoutTicks);
        Assert.Equal(StructuredResponsesPolicy.InputByteLimit, route.Limits.InputBytes);
        Assert.Equal(StructuredResponsesPolicy.OperationLimit, route.Limits.OperationsPerAdapter);
        Assert.True(metadata.ModelAccountingUnavailable); Assert.False(metadata.ProvidesWholeOperationDeadline);
        Assert.Equal(1, metadata.MaximumPhysicalInvocations);
        var rotated = new OrderedFailoverAiGateway(new OpenAiCompatibleResponsesAdapter(client,
            original with { AuthorizationHeader = "Bearer rotated-synthetic-secret" })).DescribeStructuredGateway();
        Assert.Equal(metadata.MetadataSha256, rotated.MetadataSha256);
        var json = JsonSerializer.Serialize(metadata);
        foreach (var text in new[] { json, metadata.ToString(), route.ToString(), route.Limits.ToString() })
            foreach (var forbidden in new[] { "synthetic-secret", "AuthorizationHeader", "private-path", "https://provider.example" })
                Assert.DoesNotContain(forbidden, text, StringComparison.Ordinal);
        Assert.Equal(0, calls);
        Assert.Throws<NotSupportedException>(() => ((IList<AiStructuredRouteMetadata>)metadata.Routes).Clear());
    }

    [Theory]
    [InlineData("model")]
    [InlineData("provider")]
    [InlineData("endpoint")]
    [InlineData("output")]
    [InlineData("timeout")]
    [InlineData("backup")]
    [InlineData("order")]
    public void Meaningful_route_drift_changes_fingerprint(string change)
    {
        using var client = new HttpClient(new Handler(_ => throw new InvalidOperationException("Unexpected HTTP")));
        var first = Options(); var second = Options("backup", "other-model");
        var baseline = Describe(first, second);
        var changed = change switch
        {
            "model" => Describe(first with { Model = "changed-model" }, second),
            "provider" => Describe(first with { ProviderId = "changed-provider" }, second),
            "endpoint" => Describe(first with { Endpoint = new("https://other.example/v1/responses") }, second),
            "output" => Describe(first with { MaxOutputTokens = 2048 }, second),
            "timeout" => Describe(first with { StructuredTimeout = TimeSpan.FromSeconds(59) }, second),
            "backup" => Describe(first, null),
            _ => Describe(second, first)
        };
        Assert.NotEqual(baseline.MetadataSha256, changed.MetadataSha256);
        Assert.True(changed.ModelAccountingUnavailable);
        AiStructuredGatewayMetadata Describe(OpenAiCompatibleResponsesOptions primary, OpenAiCompatibleResponsesOptions? backup) =>
            new OrderedFailoverAiGateway(new OpenAiCompatibleResponsesAdapter(client, primary),
                backup is null ? null : new OpenAiCompatibleResponsesAdapter(client, backup)).DescribeStructuredGateway();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("capability")]
    [InlineData("identity")]
    public void Unknown_incompatible_or_mismatched_supplier_is_refused_without_execution(string fault)
    {
        IAiProviderAdapter adapter = fault == "missing" ? new MissingMetadata() : new Supplier(fault);
        var gateway = new OrderedFailoverAiGateway(adapter);
        var error = Assert.Throws<InvalidOperationException>(() => gateway.DescribeStructuredGateway());
        Assert.Equal("AI structured Gateway metadata is unavailable.", error.Message);
    }

    [Fact]
    public async Task Same_gateway_instances_describe_actual_primary_and_backup_wire_budgets_but_do_not_qualify_echoed_alias()
    {
        var sent = new List<(string Model, int Output)>();
        using var client = new HttpClient(new Handler(async message =>
        {
            using var body = JsonDocument.Parse(await message.Content!.ReadAsStringAsync());
            sent.Add((body.RootElement.GetProperty("model").GetString()!, body.RootElement.GetProperty("max_output_tokens").GetInt32()));
            if (sent.Count == 1) return new(HttpStatusCode.ServiceUnavailable);
            return new(HttpStatusCode.OK) { Content = new StringContent("""
                {"status":"completed","model":"echoed-dated-alias","output":[{"type":"message","status":"completed","role":"assistant","content":[{"type":"output_text","text":"{\"note\":\"synthetic\"}"}]}],"usage":{"input_tokens":3,"output_tokens":2,"total_tokens":5}}
                """, System.Text.Encoding.UTF8, "application/json") };
        }));
        var gateway = new OrderedFailoverAiGateway(new OpenAiCompatibleResponsesAdapter(client, Options() with { MaxOutputTokens = 123 }),
            new OpenAiCompatibleResponsesAdapter(client, Options("backup", "backup-configured-model") with { MaxOutputTokens = 456 }));
        var metadata = gateway.DescribeStructuredGateway();
        Assert.Equal(2, metadata.MaximumPhysicalInvocations); Assert.False(metadata.ProvidesWholeOperationDeadline);
        Assert.Empty(sent);
        var request = new AiGatewayRequest("synthetic-tenant", "synthetic-company", "synthetic-task", "synthetic-request",
            AiCapability.StructuredGeneration, "synthetic input", """{"type":"object","properties":{"note":{"type":"string"}},"required":["note"],"additionalProperties":false}""");
        var response = await gateway.ExecuteAsync(request);
        Assert.Equal(new[] { ("configured-model", 123), ("backup-configured-model", 456) }, sent);
        Assert.Equal("backup", response.ProviderId); Assert.Equal("echoed-dated-alias", response.Model);
        Assert.Equal("backup-configured-model", metadata.Routes[1].ConfiguredModel);
        Assert.True(metadata.ModelAccountingUnavailable);
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> call) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken token) => call(message);
    }
    private sealed class MissingMetadata : IAiProviderAdapter
    {
        public string ProviderId => "missing";
        public bool Supports(AiCapability capability) => true;
        public Task<AiGatewayResponse> ExecuteAsync(AiGatewayRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Execution must not happen");
    }
    private sealed class Supplier(string fault) : IAiProviderAdapter, IAiStructuredProviderMetadataSource
    {
        public string ProviderId => "primary";
        public bool Supports(AiCapability capability) => fault != "capability";
        public AiStructuredRouteMetadata DescribeStructuredRoute() => new("wrong-identity", "model", new('A', 64), 1024,
            TimeSpan.FromSeconds(60), new("synthetic", 1, 1, 1, 1, 1, 1, 1));
        public Task<AiGatewayResponse> ExecuteAsync(AiGatewayRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Execution must not happen");
    }
}
