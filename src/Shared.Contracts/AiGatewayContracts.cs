namespace MinhHuyAiOffice.Shared.Contracts;

public enum AiCapability
{
    Reasoning,
    StructuredGeneration,
    Embedding,
    Vision,
    Transcription
}

public sealed record AiGatewayRequest(
    string TenantId,
    string CompanyId,
    string TaskId,
    string RequestId,
    AiCapability Capability,
    string Input,
    string? ResponseSchema = null)
{
    public void Validate()
    {
        Require(TenantId, nameof(TenantId));
        Require(CompanyId, nameof(CompanyId));
        Require(TaskId, nameof(TaskId));
        Require(RequestId, nameof(RequestId));
        Require(Input, nameof(Input));

        if (Capability == AiCapability.StructuredGeneration && string.IsNullOrWhiteSpace(ResponseSchema))
        {
            throw new ArgumentException("Structured generation requires a response schema.", nameof(ResponseSchema));
        }
    }

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException($"{name} must be non-empty and canonical.", name);
        }
    }
}

public sealed record AiGatewayResponse(
    string RequestId,
    AiCapability Capability,
    string Output,
    string Model,
    long InputTokens,
    long OutputTokens)
{
    public void ValidateFor(AiGatewayRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();

        if (!string.Equals(RequestId, request.RequestId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("AI gateway response request identity mismatch.");
        }

        if (Capability != request.Capability)
        {
            throw new InvalidOperationException("AI gateway response capability mismatch.");
        }

        if (string.IsNullOrWhiteSpace(Model) || InputTokens < 0 || OutputTokens < 0)
        {
            throw new InvalidOperationException("AI gateway response metadata is invalid.");
        }
    }
}

public interface IAiGateway
{
    Task<AiGatewayResponse> ExecuteAsync(AiGatewayRequest request, CancellationToken cancellationToken = default);
}

public interface IAiProviderAdapter
{
    string ProviderId { get; }

    bool Supports(AiCapability capability);

    Task<AiGatewayResponse> ExecuteAsync(AiGatewayRequest request, CancellationToken cancellationToken = default);
}
