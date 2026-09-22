namespace MinhHuy.AiOffice.Shared.Contracts.Erp;

public sealed record AccountingPostingLine(
    string AccountKey,
    decimal Debit,
    decimal Credit,
    string Description)
{
    public AccountingPostingLine Validate()
    {
        RequireCanonical(AccountKey, nameof(AccountKey));
        RequireCanonical(Description, nameof(Description));
        if (Debit < 0 || Credit < 0)
            throw new InvalidOperationException("Posting amounts cannot be negative.");
        if ((Debit == 0) == (Credit == 0))
            throw new InvalidOperationException("A posting line must contain exactly one positive debit or credit amount.");
        return this;
    }

    private static void RequireCanonical(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim())
            throw new ArgumentException($"{name} must be non-empty canonical text.", name);
    }
}

public sealed record AccountingPostingPreview(
    string TenantId,
    string CompanyId,
    string DataSourceId,
    string ErpInstalledVersion,
    long SchemaSnapshotVersion,
    string PostingCapabilityKey,
    string IdempotencyKey,
    IReadOnlyList<AccountingPostingLine> Lines)
{
    public AccountingPostingPreview Validate(ErpCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        catalog.Validate();
        catalog.AssertAuthority(TenantId, CompanyId, DataSourceId);
        RequireCanonical(PostingCapabilityKey, nameof(PostingCapabilityKey));
        RequireCanonical(IdempotencyKey, nameof(IdempotencyKey));

        if (!StringComparer.Ordinal.Equals(ErpInstalledVersion, catalog.InstalledVersion)
            || SchemaSnapshotVersion != catalog.SchemaSnapshotVersion)
            throw new InvalidOperationException("Posting preview is stale for the authoritative ERP catalog/schema version.");

        if (!catalog.Capabilities.Any(x => StringComparer.Ordinal.Equals(x.Key, PostingCapabilityKey)))
            throw new InvalidOperationException("Posting preview requires an unavailable ERP capability.");

        ArgumentNullException.ThrowIfNull(Lines);
        if (Lines.Count < 2)
            throw new InvalidOperationException("Posting preview requires at least two journal lines.");

        foreach (var line in Lines) line.Validate();
        var debit = Lines.Sum(x => x.Debit);
        var credit = Lines.Sum(x => x.Credit);
        if (debit <= 0 || debit != credit)
            throw new InvalidOperationException("Posting preview must be deterministically balanced before execution.");

        return this;
    }

    private static void RequireCanonical(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim())
            throw new ArgumentException($"{name} must be non-empty canonical text.", name);
    }
}

public enum AccountingReconciliationState
{
    Balanced,
    Mismatch
}

public sealed record AccountingPostingExecutionResult(
    string TenantId,
    string CompanyId,
    string DataSourceId,
    string IdempotencyKey,
    string PostingId,
    IReadOnlyList<AccountingEvidence> Evidence,
    AccountingReconciliationState ReconciliationState)
{
    public AccountingPostingExecutionResult Validate(AccountingPostingPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (!StringComparer.Ordinal.Equals(TenantId, preview.TenantId)
            || !StringComparer.Ordinal.Equals(CompanyId, preview.CompanyId)
            || !StringComparer.Ordinal.Equals(DataSourceId, preview.DataSourceId)
            || !StringComparer.Ordinal.Equals(IdempotencyKey, preview.IdempotencyKey))
            throw new UnauthorizedAccessException("Posting result authority/idempotency identity does not match the authorized preview.");

        RequireCanonical(PostingId, nameof(PostingId));
        ArgumentNullException.ThrowIfNull(Evidence);
        if (Evidence.Count == 0)
            throw new InvalidOperationException("Posting reconciliation requires deterministic ERP evidence.");
        foreach (var item in Evidence) item.Validate();
        return this;
    }

    private static void RequireCanonical(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim())
            throw new ArgumentException($"{name} must be non-empty canonical text.", name);
    }
}
