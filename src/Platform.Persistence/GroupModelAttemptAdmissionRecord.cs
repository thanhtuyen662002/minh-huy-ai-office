namespace MinhHuy.AIOffice.Platform.Persistence;

// Immutable companion for NEW admitted starts only. Stored profile metadata
// never grants current provider eligibility. Legacy starts remain valid
// without this row; recovery must never manufacture a historical companion.
public sealed class GroupModelAttemptAdmissionRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid BindingId { get; set; }
    public Guid RequestId { get; set; }
    public Guid BatchId { get; set; }
    public string InputSha256 { get; set; } = "";
    public string PlanSha256 { get; set; } = "";
    public string SidecarSha256 { get; set; } = "";
    public int ChunkOrdinal { get; set; }
    public Guid ChunkOperationId { get; set; }
    public int FormatVersion { get; set; }
    public string SnapshotSha256 { get; set; } = "";
    public byte[] Snapshot { get; set; } = [];
    public DateTimeOffset PreparedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public override string ToString() => "Group model attempt admission metadata.";
}
