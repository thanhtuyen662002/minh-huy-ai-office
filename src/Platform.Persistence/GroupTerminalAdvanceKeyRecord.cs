namespace MinhHuy.AIOffice.Platform.Persistence;

// Immutable material identity, not key bytes or a secret reference. The owned
// store enrolls only after fresh authority and original-prefix proof.
public sealed class GroupTerminalAdvanceKeyRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid BindingId { get; set; }
    public string KeyId { get; set; } = "";
    public int FormatVersion { get; set; } = 1;
    public byte[] MaterialFingerprint { get; set; } = [];
    public DateTimeOffset EnrolledAtUtc { get; set; }
    public Guid ServiceId { get; set; }
    public long CredentialEpoch { get; set; }
    public long GrantVersion { get; set; }
    public long SourceVersion { get; set; }
    public long DeletionGeneration { get; set; }
    public long AccountVersion { get; set; }
    public byte[] AuthoritySha256 { get; set; } = [];
}
