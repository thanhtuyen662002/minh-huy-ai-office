using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

public enum GroupBatchSourceDisposition { Readable = 1, Recalled = 2, ObsoleteGeneration = 3, ChangedAfterCutoff = 4 }

// Private source context is not a model proposal, persisted note or future
// authorization. Only the scoped reader constructs it; later consumers must
// fence its exact dependencies again before release or effects.
public sealed class GroupBatchSourceEntry
{
    internal GroupBatchSourceEntry(GroupBatchSourceSnapshot snapshot, string? text)
    {
        MessageId = snapshot.Head.MessageId; Revision = snapshot.Head.Revision;
        CommittedSequence = snapshot.Head.CommittedSequence; Kind = snapshot.Head.Kind;
        ContentSha256 = snapshot.Head.ContentSha256; OccurredAtUtc = snapshot.Head.OccurredAtUtc;
        ExternalMessageId = snapshot.ExternalMessageId; SenderId = snapshot.SenderId;
        ReplyToMessageId = snapshot.ReplyToMessageId; IsHistoricalBackfill = snapshot.Head.IsHistoricalBackfill;
        Disposition = snapshot.Disposition; Text = text;
    }
    public Guid MessageId { get; }
    public long Revision { get; }
    public long CommittedSequence { get; }
    public GroupSourceEventKind Kind { get; }
    public string ContentSha256 { get; }
    public DateTimeOffset OccurredAtUtc { get; }
    public string ExternalMessageId { get; }
    public string SenderId { get; }
    public string? ReplyToMessageId { get; }
    public bool IsHistoricalBackfill { get; }
    public GroupBatchSourceDisposition Disposition { get; }
    public string? Text { get; }
    public override string ToString() => "Group batch source entry (private content).";
}

public sealed class GroupBatchSourceContext
{
    internal GroupBatchSourceContext(GroupBatchClaimHandle handle, long cutoff, bool hasCoverageGap,
        GroupBatchSourceSnapshot[] snapshots, GroupBatchSourceEntry[] items)
    {
        Handle = handle; AllocatedThroughSequence = cutoff; HasCoverageGap = hasCoverageGap;
        Snapshots = Array.AsReadOnly(snapshots.ToArray()); Items = Array.AsReadOnly(items.ToArray());
    }
    internal GroupBatchClaimHandle Handle { get; }
    internal IReadOnlyList<GroupBatchSourceSnapshot> Snapshots { get; }
    public GroupScope Scope => Handle.Receipt.Scope;
    public Guid BatchId => Handle.Receipt.BatchId;
    public long AllocatedThroughSequence { get; }
    public bool HasCoverageGap { get; }
    public IReadOnlyList<GroupBatchSourceEntry> Items { get; }
    public override string ToString() => "Group batch source context (private content).";
}

internal sealed record GroupBatchSourceSnapshot(GroupBatchSourceHead Head, GroupBatchSourceHead CurrentHead,
    string ExternalMessageId, string SenderId, string? ReplyToMessageId, string EventId,
    GroupBatchSourceReceipt Receipt, GroupBatchSourceDisposition Disposition, byte[]? ProtectedContent);

internal sealed record GroupBatchSourceHead(Guid TenantId, Guid CompanyId, Guid BindingId, Guid MessageId,
    long Revision, long CommittedSequence, GroupSourceEventKind Kind, string ContentSha256, string ContentKeyId,
    long SourceVersion, long DeletionGeneration, DateTimeOffset OccurredAtUtc, DateTimeOffset CommittedAtUtc,
    bool IsHistoricalBackfill, byte[]? EventBytes, byte[]? SenderBytes, byte[]? ReplyBytes, bool ReplyIsNull,
    string? EventText, string? SenderText, string? ReplyText);

internal sealed record GroupBatchSourceReceipt(Guid TenantId, Guid CompanyId, Guid BindingId,
    string EventIdentityHash, Guid MessageId, long Revision, string EnvelopeSha256, Guid ServiceId,
    long CredentialEpoch, long ListenerEpoch, DateTimeOffset CommittedAtUtc, byte[]? EventBytes, string? EventText);
