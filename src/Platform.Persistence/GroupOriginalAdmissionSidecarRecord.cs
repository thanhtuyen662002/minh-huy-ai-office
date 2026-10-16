namespace MinhHuy.AIOffice.Platform.Persistence;

// Immutable original metadata; no source text, schema text, provider response,
// endpoint URL or credential. This row alone grants no provider invocation.
public sealed class GroupOriginalAdmissionSidecarRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid BindingId { get; set; }
    public Guid BatchId { get; set; }
    public int FormatVersion { get; set; }
    public string PlanSha256 { get; set; } = "";
    public string SidecarSha256 { get; set; } = "";
    public byte[] Sidecar { get; set; } = [];
    public Guid OriginalClaimOperationId { get; set; }
    public long OriginalClaimEpoch { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public override string ToString() => "Group original admission provenance metadata.";
}
