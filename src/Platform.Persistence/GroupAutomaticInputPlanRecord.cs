namespace MinhHuy.AIOffice.Platform.Persistence;

// One immutable original selection per batch. Metadata only; private source,
// history content, provider response and credentials are never stored here.
public sealed class GroupAutomaticInputPlanRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid BindingId { get; set; }
    public Guid BatchId { get; set; }
    public int FormatVersion { get; set; }
    public string PlanSha256 { get; set; } = "";
    public byte[] Plan { get; set; } = [];
    public Guid OriginalClaimOperationId { get; set; }
    public long OriginalClaimEpoch { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public override string ToString() => "Group original input plan metadata.";
}
