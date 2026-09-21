namespace MinhHuyAiOffice.Shared.Contracts;

public sealed record AiModelProfile(
    string ProviderId,
    string ModelId,
    IReadOnlySet<AiCapability> Capabilities,
    int Tier,
    decimal MaxCostPerMillionTokens,
    int TypicalLatencyMs,
    bool IsHealthy = true)
{
    public void Validate()
    {
        Require(ProviderId, nameof(ProviderId));
        Require(ModelId, nameof(ModelId));
        ArgumentNullException.ThrowIfNull(Capabilities);
        if (Capabilities.Count == 0 || Tier < 0 || MaxCostPerMillionTokens < 0 || TypicalLatencyMs < 0)
            throw new ArgumentException("AI model profile metadata is invalid.");
    }

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
            throw new ArgumentException($"{name} must be non-empty and canonical.", name);
    }
}

public sealed record AiRouteRequest(
    string TenantId,
    string CompanyId,
    string TaskId,
    AiCapability Capability,
    int MinimumTier = 0,
    decimal? MaxCostPerMillionTokens = null,
    int? MaxTypicalLatencyMs = null,
    string? ExcludedProviderId = null)
{
    public void Validate()
    {
        Require(TenantId, nameof(TenantId));
        Require(CompanyId, nameof(CompanyId));
        Require(TaskId, nameof(TaskId));
        if (MinimumTier < 0 || MaxCostPerMillionTokens < 0 || MaxTypicalLatencyMs < 0)
            throw new ArgumentException("AI route constraints cannot be negative.");
        if (ExcludedProviderId is not null)
            Require(ExcludedProviderId, nameof(ExcludedProviderId));
    }

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
            throw new ArgumentException($"{name} must be non-empty and canonical.", name);
    }
}

public sealed record AiRouteSelection(string ProviderId, string ModelId, int Tier, string Reason);

public interface IAiModelRouter
{
    AiRouteSelection Select(AiRouteRequest request);
}

public sealed class DeterministicAiModelRouter : IAiModelRouter
{
    private readonly IReadOnlyList<AiModelProfile> _models;

    public DeterministicAiModelRouter(IEnumerable<AiModelProfile> models)
    {
        ArgumentNullException.ThrowIfNull(models);
        _models = models.ToArray();
        if (_models.Count == 0) throw new ArgumentException("At least one AI model profile is required.", nameof(models));
        foreach (var model in _models) model.Validate();
        if (_models.GroupBy(m => (m.ProviderId, m.ModelId)).Any(g => g.Count() > 1))
            throw new InvalidOperationException("Provider/model identities must be unique.");
    }

    public AiRouteSelection Select(AiRouteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();

        var candidates = _models
            .Where(m => m.IsHealthy)
            .Where(m => m.Capabilities.Contains(request.Capability))
            .Where(m => m.Tier >= request.MinimumTier)
            .Where(m => request.MaxCostPerMillionTokens is null || m.MaxCostPerMillionTokens <= request.MaxCostPerMillionTokens)
            .Where(m => request.MaxTypicalLatencyMs is null || m.TypicalLatencyMs <= request.MaxTypicalLatencyMs)
            .Where(m => request.ExcludedProviderId is null || !string.Equals(m.ProviderId, request.ExcludedProviderId, StringComparison.Ordinal))
            .OrderBy(m => m.Tier)
            .ThenBy(m => m.MaxCostPerMillionTokens)
            .ThenBy(m => m.TypicalLatencyMs)
            .ThenBy(m => m.ProviderId, StringComparer.Ordinal)
            .ThenBy(m => m.ModelId, StringComparer.Ordinal)
            .ToArray();

        if (candidates.Length == 0)
            throw new InvalidOperationException("No healthy AI model satisfies the requested capability and routing constraints.");

        var selected = candidates[0];
        return new AiRouteSelection(selected.ProviderId, selected.ModelId, selected.Tier, "capability-tier-budget-latency-health");
    }
}
