namespace MinhHuy.AiOffice.Shared.Contracts;

public sealed record AiUsageScope(
    string TenantId,
    string CompanyId,
    string TaskId,
    string AgentId,
    string RequestId);

public sealed record AiUsageAmount(
    long InputTokenEquivalent,
    long OutputTokenEquivalent,
    long TotalTokenEquivalent,
    decimal ProviderCostUsd);

public sealed record AiUsageEntry(
    string EntryId,
    AiUsageScope Scope,
    string ProviderId,
    string ModelId,
    AiCapability Capability,
    AiUsageAmount Usage,
    DateTimeOffset OccurredAtUtc,
    string? ParentEntryId = null);

public sealed record AiUsageAggregate(
    AiUsageScope Scope,
    long InputTokenEquivalent,
    long OutputTokenEquivalent,
    long TotalTokenEquivalent,
    decimal ProviderCostUsd,
    int CallCount);

public static class AiUsageLedger
{
    public static AiUsageEntry Validate(AiUsageEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ValidateRequired(entry.EntryId, nameof(entry.EntryId));
        ValidateScope(entry.Scope);
        ValidateRequired(entry.ProviderId, nameof(entry.ProviderId));
        ValidateRequired(entry.ModelId, nameof(entry.ModelId));

        if (entry.Usage.InputTokenEquivalent < 0 || entry.Usage.OutputTokenEquivalent < 0 || entry.Usage.TotalTokenEquivalent < 0)
            throw new ArgumentOutOfRangeException(nameof(entry.Usage), "Token-equivalent usage cannot be negative.");
        if (entry.Usage.TotalTokenEquivalent != entry.Usage.InputTokenEquivalent + entry.Usage.OutputTokenEquivalent)
            throw new ArgumentException("Total token-equivalent usage must equal input plus output.", nameof(entry));
        if (entry.Usage.ProviderCostUsd < 0)
            throw new ArgumentOutOfRangeException(nameof(entry.Usage), "Provider cost cannot be negative.");
        if (entry.OccurredAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Usage timestamps must be UTC.", nameof(entry));
        if (entry.ParentEntryId is not null && string.IsNullOrWhiteSpace(entry.ParentEntryId))
            throw new ArgumentException("Parent entry identity cannot be blank.", nameof(entry));

        return entry;
    }

    public static AiUsageAggregate AggregateExecutionTree(AiUsageScope scope, IEnumerable<AiUsageEntry> entries)
    {
        ValidateScope(scope);
        ArgumentNullException.ThrowIfNull(entries);
        var materialized = entries.Select(Validate).ToArray();

        if (materialized.Select(x => x.EntryId).Distinct(StringComparer.Ordinal).Count() != materialized.Length)
            throw new InvalidOperationException("Duplicate usage entry identity would double-charge the execution tree.");

        foreach (var entry in materialized)
        {
            if (!SameExecution(scope, entry.Scope))
                throw new InvalidOperationException("Cross-tenant/company/task/agent usage cannot be aggregated.");
            if (entry.ParentEntryId is not null && materialized.All(x => !string.Equals(x.EntryId, entry.ParentEntryId, StringComparison.Ordinal)))
                throw new InvalidOperationException("Usage entry references a parent outside the execution tree.");
        }

        return new AiUsageAggregate(
            scope,
            materialized.Sum(x => x.Usage.InputTokenEquivalent),
            materialized.Sum(x => x.Usage.OutputTokenEquivalent),
            materialized.Sum(x => x.Usage.TotalTokenEquivalent),
            materialized.Sum(x => x.Usage.ProviderCostUsd),
            materialized.Length);
    }

    private static bool SameExecution(AiUsageScope expected, AiUsageScope actual) =>
        string.Equals(expected.TenantId, actual.TenantId, StringComparison.Ordinal) &&
        string.Equals(expected.CompanyId, actual.CompanyId, StringComparison.Ordinal) &&
        string.Equals(expected.TaskId, actual.TaskId, StringComparison.Ordinal) &&
        string.Equals(expected.AgentId, actual.AgentId, StringComparison.Ordinal);

    private static void ValidateScope(AiUsageScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ValidateRequired(scope.TenantId, nameof(scope.TenantId));
        ValidateRequired(scope.CompanyId, nameof(scope.CompanyId));
        ValidateRequired(scope.TaskId, nameof(scope.TaskId));
        ValidateRequired(scope.AgentId, nameof(scope.AgentId));
        ValidateRequired(scope.RequestId, nameof(scope.RequestId));
    }

    private static void ValidateRequired(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
            throw new ArgumentException($"{name} must be a canonical non-empty identifier.", name);
    }
}
