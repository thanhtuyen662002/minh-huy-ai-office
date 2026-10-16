namespace MinhHuy.AIOffice.Platform.Persistence;

// Append-only quarantine metadata. No source content or replacement authority.
public sealed class GroupOriginalInvalidationRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid BindingId { get; set; }
    public Guid BatchId { get; set; }
    public int FormatVersion { get; set; }
    public string PlanSha256 { get; set; } = "";
    public Guid OriginalClaimOperationId { get; set; }
    public long OriginalClaimEpoch { get; set; }
    public Guid ClaimOperationId { get; set; }
    public long ClaimEpoch { get; set; }
    public string ObservedDependenciesSha256 { get; set; } = "";
    public string CheckpointSha256 { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public override string ToString() => "Group original mismatch checkpoint (no continuation authority).";
}
