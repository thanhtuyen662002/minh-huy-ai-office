using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

// Immutable SQL reservation, not an extraction completion or a notification.
public sealed class GroupBatchAllocationRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid BindingId { get; set; }
    public Guid Id { get; set; }
    public Guid OperationId { get; set; }
    public long AfterSequence { get; set; }
    public long AllocatedThroughSequence { get; set; }
    public long ObservedCommittedThroughSequence { get; set; }
    public int RawRevisionCount { get; set; }
    public bool IsHistoricalBackfill { get; set; }
    public long SourceVersion { get; set; }
    public long DeletionGeneration { get; set; }
    public long AccountVersion { get; set; }
    public Guid ServiceId { get; set; }
    public long CredentialEpoch { get; set; }
    public long GrantVersion { get; set; }
    public DateTimeOffset AllocatedAtUtc { get; set; }
}

// Every raw revision has one allocation even when recall/edit supersedes it.
// Head selection, dispositions and terminal progress are separate records.
public sealed class GroupBatchAllocatedRevisionRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid BindingId { get; set; }
    public Guid BatchId { get; set; }
    public long CommittedSequence { get; set; }
    public Guid MessageId { get; set; }
    public long Revision { get; set; }
    public string ContentSha256 { get; set; } = "";
    public GroupSourceEventKind Kind { get; set; }
    public DateTimeOffset CommittedAtUtc { get; set; }
    public bool IsHistoricalBackfill { get; set; }
    public long SourceVersion { get; set; }
    public long DeletionGeneration { get; set; }
}

public sealed record GroupAllocatedRevision(GroupPendingRevisionMetadata Metadata, long SourceVersion, long DeletionGeneration);

public sealed record GroupBatchAllocationReceipt(GroupScope Scope, Guid BatchId, Guid OperationId,
    long AfterSequence, long AllocatedThroughSequence, long ObservedCommittedThroughSequence,
    bool IsHistoricalBackfill, long SourceVersion, long DeletionGeneration, long AccountVersion,
    Guid ServiceId, long CredentialEpoch, long GrantVersion, DateTimeOffset AllocatedAtUtc,
    IReadOnlyList<GroupAllocatedRevision> Revisions, bool WasAlreadyAllocated);
