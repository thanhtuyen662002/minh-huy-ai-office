using MinhHuyAiOffice.Shared.Contracts;
using Xunit;

namespace MinhHuyAiOffice.Shared.Contracts.Tests;

public sealed class AiGatewayContractsTests
{
    [Fact]
    public async Task ExecuteAsync_DispatchesSingleSupportingAdapterAndValidatesResponse()
    {
        var adapter = new StubAdapter("provider-a", AiCapability.Reasoning);
        var gateway = new ProviderNeutralAiGateway(new[] { adapter });
        var request = Request(AiCapability.Reasoning);

        var response = await gateway.ExecuteAsync(request);

        Assert.Equal(request.RequestId, response.RequestId);
        Assert.Equal(1, adapter.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_FailsClosedWhenCapabilityIsUnsupported()
    {
        var gateway = new ProviderNeutralAiGateway(new[] { new StubAdapter("provider-a", AiCapability.Embedding) });

        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.ExecuteAsync(Request(AiCapability.Vision)));
    }

    [Fact]
    public async Task ExecuteAsync_FailsClosedWhenSelectionIsAmbiguous()
    {
        var gateway = new ProviderNeutralAiGateway(new IAiProviderAdapter[]
        {
            new StubAdapter("provider-a", AiCapability.Reasoning),
            new StubAdapter("provider-b", AiCapability.Reasoning)
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.ExecuteAsync(Request(AiCapability.Reasoning)));
    }

    [Fact]
    public async Task ExecuteAsync_RejectsMismatchedProviderResponse()
    {
        var adapter = new StubAdapter("provider-a", AiCapability.Reasoning, request =>
            new AiGatewayResponse("wrong-request", request.Capability, "ok", "model-a", 1, 1));
        var gateway = new ProviderNeutralAiGateway(new[] { adapter });

        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.ExecuteAsync(Request(AiCapability.Reasoning)));
    }

    [Fact]
    public void Constructor_RejectsDuplicateProviderIdentity()
    {
        var adapters = new IAiProviderAdapter[]
        {
            new StubAdapter("provider-a", AiCapability.Reasoning),
            new StubAdapter("provider-a", AiCapability.Embedding)
        };

        Assert.Throws<InvalidOperationException>(() => new ProviderNeutralAiGateway(adapters));
    }

    [Fact]
    public void StructuredGeneration_RequiresSchema()
    {
        var request = Request(AiCapability.StructuredGeneration) with { ResponseSchema = null };

        Assert.Throws<ArgumentException>(() => request.Validate());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" tenant")]
    [InlineData("tenant ")]
    public void Request_RejectsNonCanonicalTenantIdentity(string tenantId)
    {
        var request = Request(AiCapability.Reasoning) with { TenantId = tenantId };

        Assert.Throws<ArgumentException>(() => request.Validate());
    }

    private static AiGatewayRequest Request(AiCapability capability) => new(
        "tenant-a",
        "company-a",
        "task-a",
        "request-a",
        capability,
        "input",
        capability == AiCapability.StructuredGeneration ? "{\"type\":\"object\"}" : null);

    private sealed class StubAdapter : IAiProviderAdapter
    {
        private readonly AiCapability _capability;
        private readonly Func<AiGatewayRequest, AiGatewayResponse> _responseFactory;

        public StubAdapter(
            string providerId,
            AiCapability capability,
            Func<AiGatewayRequest, AiGatewayResponse>? responseFactory = null)
        {
            ProviderId = providerId;
            _capability = capability;
            _responseFactory = responseFactory ?? (request =>
                new AiGatewayResponse(request.RequestId, request.Capability, "ok", "model-a", 1, 1));
        }

        public string ProviderId { get; }

        public int CallCount { get; private set; }

        public bool Supports(AiCapability capability) => capability == _capability;

        public Task<AiGatewayResponse> ExecuteAsync(AiGatewayRequest request, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(_responseFactory(request));
        }
    }
}
