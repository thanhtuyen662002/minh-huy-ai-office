namespace MinhHuy.AIOffice.Platform.Persistence;

// Append-only metadata carrier. SQL shape/FKs/hash do not prove a MAC or
// authorize advancement. The future owned store must authenticate every field.
public sealed class GroupTerminalAdvanceRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid BindingId { get; set; }
    public long Version { get; set; }
    public Guid BatchId { get; set; }
    public Guid TerminalOperationId { get; set; }
    public long AfterSequence { get; set; }
    public long ThroughSequence { get; set; }
    public byte[] TerminalManifestSha256 { get; set; } = [];
    public DateTimeOffset TerminalCommittedAtUtc { get; set; }
    public long? PreviousVersion { get; set; }
    public long? PreviousThroughSequence { get; set; }
    public byte[]? PreviousFingerprint { get; set; }
    public DateTimeOffset? PreviousAdvancedAtUtc { get; set; }
    public int EnvelopeVersion { get; set; } = 1;
    public byte[] Envelope { get; set; } = [];
    public byte[] Fingerprint { get; set; } = [];
    public string KeyId { get; set; } = "";
    public Guid ServiceId { get; set; }
    public long CredentialEpoch { get; set; }
    public long GrantVersion { get; set; }
    public long SourceVersion { get; set; }
    public long DeletionGeneration { get; set; }
    public long AccountVersion { get; set; }
    public byte[] AuthoritySha256 { get; set; } = [];
    public DateTimeOffset AdvancedAtUtc { get; set; }
}
