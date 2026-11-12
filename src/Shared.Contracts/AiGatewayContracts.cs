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
    public const int MaximumImages = 6;

    /// <summary>Pictures the model should look at with <see cref="Input"/> (Trợ lý attachments). Empty for text-only requests.</summary>
    public IReadOnlyList<AiImageInput> Images { get; init; } = [];

    public void Validate()
    {
        if (Images is null || Images.Count > MaximumImages
            || Images.Any(image => image is null || image.Content is not { Length: > 0 } || string.IsNullOrWhiteSpace(image.MediaType)))
        {
            throw new ArgumentException($"At most {MaximumImages} non-empty pictures are allowed.", nameof(Images));
        }

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
    public string? ProviderId { get; init; }

    /// <summary>Input tokens the provider served from its prompt cache (a subset of <see cref="InputTokens"/>; 0 when not reported).</summary>
    public long CachedInputTokens { get; init; }

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

        if (string.IsNullOrWhiteSpace(Output) || string.IsNullOrWhiteSpace(Model) || InputTokens < 0 || OutputTokens < 0)
        {
            throw new InvalidOperationException("AI gateway response metadata is invalid.");
        }

        if (ProviderId is not null
            && (string.IsNullOrWhiteSpace(ProviderId)
                || !string.Equals(ProviderId, ProviderId.Trim(), StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("AI gateway provider identity is invalid.");
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

public sealed class ProviderNeutralAiGateway : IAiGateway
{
    private readonly IReadOnlyList<IAiProviderAdapter> _adapters;

    public ProviderNeutralAiGateway(IEnumerable<IAiProviderAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        _adapters = adapters.ToArray();

        if (_adapters.Any(adapter => adapter is null))
        {
            throw new ArgumentException("AI provider adapters cannot contain null entries.", nameof(adapters));
        }

        var duplicateProvider = _adapters
            .GroupBy(adapter => adapter.ProviderId, StringComparer.Ordinal)
            .FirstOrDefault(group => string.IsNullOrWhiteSpace(group.Key) || group.Count() > 1);
        if (duplicateProvider is not null)
        {
            throw new InvalidOperationException("AI provider identifiers must be non-empty and unique.");
        }
    }

    public async Task<AiGatewayResponse> ExecuteAsync(
        AiGatewayRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();

        var candidates = _adapters.Where(adapter => adapter.Supports(request.Capability)).ToArray();
        if (candidates.Length != 1)
        {
            throw new InvalidOperationException(
                candidates.Length == 0
                    ? $"No AI provider supports capability {request.Capability}."
                    : $"AI provider selection for capability {request.Capability} is ambiguous.");
        }

        var response = await candidates[0].ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(response);
        response.ValidateFor(request);
        return response;
    }
}

/// <summary>One picture for a model request: its media type (image/jpeg, image/png, image/webp, image/gif) and bytes.</summary>
public sealed record AiImageInput(string MediaType, byte[] Content);
