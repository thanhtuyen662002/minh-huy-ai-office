namespace MinhHuy.AIOffice.Platform.Persistence;

// Append-only completion metadata. Neither a mutable object nor a matching
// SHA carrier authorizes an INSERT; only the future owned terminal store may
// stage this after reconstructing the complete current graph.
public sealed class GroupBatchTerminalReceiptRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid BindingId { get; set; }
    public Guid BatchId { get; set; }
    public Guid OperationId { get; set; }
    public int ManifestVersion { get; set; } = 1;
    public byte[] Manifest { get; set; } = [];
    public byte[] ManifestSha256 { get; set; } = [];
    public long AfterSequence { get; set; }
    public long ThroughSequence { get; set; }
    public int RawRevisionCount { get; set; }
    public int SelectedMessageCount { get; set; }
    public int ContributorCount { get; set; }
    public int NoteCount { get; set; }
    public Guid ClaimOperationId { get; set; }
    public Guid ClaimOwnerId { get; set; }
    public long ClaimEpoch { get; set; }
    public Guid ServiceId { get; set; }
    public long CredentialEpoch { get; set; }
    public long GrantVersion { get; set; }
    public long SourceVersion { get; set; }
    public long DeletionGeneration { get; set; }
    public long AccountVersion { get; set; }
    public DateTimeOffset CommittedAtUtc { get; set; }
}

// The scheduled and committed ingest markers remain in GroupSourceStates.
// This separate cursor is advanced only across authenticated terminal intervals.
public sealed class GroupTerminalFrontierStateRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid BindingId { get; set; }
    public long ThroughSequence { get; set; }
    public Guid? LastTerminalBatchId { get; set; }
    public long Version { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
