namespace MinhHuy.AiOffice.Shared.Contracts.Erp;

public sealed record AccountingInvestigationRequest(
    string TenantId,
    string CompanyId,
    string DataSourceId,
    string Question,
    IReadOnlyList<string> RequiredCapabilityKeys)
{
    public AccountingInvestigationRequest Validate(ErpCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        catalog.Validate();
        catalog.AssertAuthority(TenantId, CompanyId, DataSourceId);
        RequireCanonical(Question, nameof(Question));
        ArgumentNullException.ThrowIfNull(RequiredCapabilityKeys);

        var available = catalog.Capabilities.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in RequiredCapabilityKeys)
        {
            RequireCanonical(key, nameof(RequiredCapabilityKeys));
            if (!seen.Add(key))
            {
                throw new InvalidOperationException("Required accounting capability keys must be unique.");
            }

            if (!available.Contains(key))
            {
                throw new InvalidOperationException($"Accounting investigation requires unavailable ERP capability: {key}");
            }
        }

        return this;
    }

    private static void RequireCanonical(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim())
        {
            throw new ArgumentException($"{name} must be non-empty canonical text.", name);
        }
    }
}

public sealed record AccountingEvidence(
    string SourceObjectKey,
    string EvidenceId,
    string Summary)
{
    public AccountingEvidence Validate()
    {
        RequireCanonical(SourceObjectKey, nameof(SourceObjectKey));
        RequireCanonical(EvidenceId, nameof(EvidenceId));
        RequireCanonical(Summary, nameof(Summary));
        return this;
    }

    private static void RequireCanonical(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim())
        {
            throw new ArgumentException($"{name} must be non-empty canonical text.", name);
        }
    }
}

public sealed record AccountingInvestigationResult(
    string TenantId,
    string CompanyId,
    string DataSourceId,
    string Answer,
    IReadOnlyList<AccountingEvidence> Evidence,
    string AuditCorrelationId)
{
    public AccountingInvestigationResult Validate(AccountingInvestigationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!StringComparer.Ordinal.Equals(TenantId, request.TenantId)
            || !StringComparer.Ordinal.Equals(CompanyId, request.CompanyId)
            || !StringComparer.Ordinal.Equals(DataSourceId, request.DataSourceId))
        {
            throw new UnauthorizedAccessException("Accounting investigation result authority does not match the authorized request.");
        }

        RequireCanonical(Answer, nameof(Answer));
        RequireCanonical(AuditCorrelationId, nameof(AuditCorrelationId));
        ArgumentNullException.ThrowIfNull(Evidence);
        if (Evidence.Count == 0)
        {
            throw new InvalidOperationException("Accounting investigation answers require deterministic ERP evidence.");
        }

        var evidenceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in Evidence)
        {
            item.Validate();
            if (!evidenceIds.Add(item.EvidenceId))
            {
                throw new InvalidOperationException("Accounting evidence identifiers must be unique.");
            }
        }

        return this;
    }

    private static void RequireCanonical(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim())
        {
            throw new ArgumentException($"{name} must be non-empty canonical text.", name);
        }
    }
}
