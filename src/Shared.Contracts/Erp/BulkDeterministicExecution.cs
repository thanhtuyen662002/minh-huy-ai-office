using System.Security.Cryptography;
using System.Text;

namespace MinhHuy.AIOffice.Shared.Contracts.Erp;

public sealed record BulkExecutionAuthority(
    Guid TenantId,
    Guid CompanyId,
    Guid DataSourceId,
    string Capability,
    string WorkflowVersion,
    string InstalledErpVersion,
    string SchemaSnapshotVersion,
    string CatalogVersion,
    string SecretReference);

public sealed record BulkExecutionItem(
    string ItemIdentity,
    string Operation,
    string PayloadFingerprint,
    string PreviewEvidenceReference)
{
    public string GetExecutionIdentity(BulkExecutionAuthority authority)
        => BulkDeterministicExecutionContract.GetItemExecutionIdentity(authority, this);
}

public sealed record BulkExecutionPlan(
    BulkExecutionAuthority Authority,
    IReadOnlyList<BulkExecutionItem> Items,
    string AuditCorrelationId)
{
    public string PlanIdentity => BulkDeterministicExecutionContract.GetPlanIdentity(this);
}

public enum BulkExecutionItemState
{
    Succeeded,
    Failed,
    Exception
}

public sealed record BulkExecutionItemResult(
    Guid TenantId,
    Guid CompanyId,
    Guid DataSourceId,
    string ItemExecutionIdentity,
    BulkExecutionItemState State,
    string EvidenceReference,
    string ActualResultFingerprint,
    string AuditCorrelationId);

public static class BulkDeterministicExecutionContract
{
    public static string GetPlanIdentity(BulkExecutionPlan plan)
    {
        ValidatePlan(plan);
        var itemIdentities = plan.Items
            .Select(item => GetItemExecutionIdentity(plan.Authority, item))
            .OrderBy(value => value, StringComparer.Ordinal);
        return Hash(string.Join("\n", AuthorityIdentity(plan.Authority), string.Join("\n", itemIdentities)));
    }

    public static string GetItemExecutionIdentity(BulkExecutionAuthority authority, BulkExecutionItem item)
    {
        ValidateAuthority(authority);
        ValidateItem(item);
        return Hash(string.Join(':',
            AuthorityIdentity(authority),
            Canonical(item.ItemIdentity),
            Canonical(item.Operation),
            item.PayloadFingerprint.Trim()));
    }

    public static void ValidatePlan(BulkExecutionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ValidateAuthority(plan.Authority ?? throw new ArgumentException("Bulk authority is required.", nameof(plan)));
        Require(plan.AuditCorrelationId, nameof(plan.AuditCorrelationId));
        ArgumentNullException.ThrowIfNull(plan.Items);
        if (plan.Items.Count == 0)
            throw new InvalidOperationException("Bulk execution requires at least one previewed item.");

        foreach (var item in plan.Items) ValidateItem(item);
        if (plan.Items.GroupBy(item => Canonical(item.ItemIdentity), StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new InvalidOperationException("Bulk plan requires one deterministic entry per item identity.");
    }

    public static void ValidateResult(BulkExecutionPlan plan, BulkExecutionItem item, BulkExecutionItemResult result)
    {
        ValidatePlan(plan);
        ValidateItem(item);
        ArgumentNullException.ThrowIfNull(result);
        if (!plan.Items.Any(candidate => StringComparer.Ordinal.Equals(Canonical(candidate.ItemIdentity), Canonical(item.ItemIdentity))))
            throw new InvalidOperationException("Bulk result item is not part of the authorized plan.");

        var authority = plan.Authority;
        if (result.TenantId != authority.TenantId || result.CompanyId != authority.CompanyId || result.DataSourceId != authority.DataSourceId)
            throw new UnauthorizedAccessException("Bulk result crossed authoritative tenant/company/data-source scope.");
        if (!StringComparer.Ordinal.Equals(result.ItemExecutionIdentity, GetItemExecutionIdentity(authority, item)))
            throw new InvalidOperationException("Bulk result identity does not match the exact version-fenced item operation.");
        Require(result.EvidenceReference, nameof(result.EvidenceReference));
        Require(result.ActualResultFingerprint, nameof(result.ActualResultFingerprint));
        if (!StringComparer.Ordinal.Equals(result.AuditCorrelationId, plan.AuditCorrelationId))
            throw new InvalidOperationException("Bulk result audit correlation does not match the authorized plan.");
    }

    public static IReadOnlyList<BulkExecutionItem> GetRetryableItems(
        BulkExecutionPlan plan,
        IReadOnlyCollection<BulkExecutionItemResult> priorResults)
    {
        ValidatePlan(plan);
        ArgumentNullException.ThrowIfNull(priorResults);
        foreach (var result in priorResults)
        {
            var item = plan.Items.SingleOrDefault(candidate =>
                StringComparer.Ordinal.Equals(GetItemExecutionIdentity(plan.Authority, candidate), result.ItemExecutionIdentity))
                ?? throw new InvalidOperationException("Prior bulk result does not belong to the authorized plan.");
            ValidateResult(plan, item, result);
        }

        var succeeded = priorResults
            .Where(result => result.State == BulkExecutionItemState.Succeeded)
            .Select(result => result.ItemExecutionIdentity)
            .ToHashSet(StringComparer.Ordinal);
        return plan.Items
            .Where(item => !succeeded.Contains(GetItemExecutionIdentity(plan.Authority, item)))
            .ToArray();
    }

    private static void ValidateAuthority(BulkExecutionAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        if (authority.TenantId == Guid.Empty || authority.CompanyId == Guid.Empty || authority.DataSourceId == Guid.Empty)
            throw new ArgumentException("Tenant, company and data-source authority are required.", nameof(authority));
        Require(authority.Capability, nameof(authority.Capability));
        Require(authority.WorkflowVersion, nameof(authority.WorkflowVersion));
        Require(authority.InstalledErpVersion, nameof(authority.InstalledErpVersion));
        Require(authority.SchemaSnapshotVersion, nameof(authority.SchemaSnapshotVersion));
        Require(authority.CatalogVersion, nameof(authority.CatalogVersion));
        Require(authority.SecretReference, nameof(authority.SecretReference));
    }

    private static void ValidateItem(BulkExecutionItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        Require(item.ItemIdentity, nameof(item.ItemIdentity));
        Require(item.Operation, nameof(item.Operation));
        Require(item.PayloadFingerprint, nameof(item.PayloadFingerprint));
        Require(item.PreviewEvidenceReference, nameof(item.PreviewEvidenceReference));
    }

    private static string AuthorityIdentity(BulkExecutionAuthority authority) => string.Join(':',
        authority.TenantId,
        authority.CompanyId,
        authority.DataSourceId,
        Canonical(authority.Capability),
        authority.WorkflowVersion.Trim(),
        authority.InstalledErpVersion.Trim(),
        authority.SchemaSnapshotVersion.Trim(),
        authority.CatalogVersion.Trim());

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string Canonical(string value) => value.Trim().ToUpperInvariant();

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim())
            throw new ArgumentException($"{name} must be non-empty canonical text.", name);
    }
}
