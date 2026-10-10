namespace MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

/// <summary>Metadata only, projected by a trusted scoped SQL reader; never an authorization receipt.</summary>
public sealed record GroupPendingRevisionMetadata(GroupScope Scope, Guid MessageId, long Revision,
    long CommittedSequence, string ContentSha256, GroupSourceEventKind Kind,
    DateTimeOffset CommittedAtUtc, bool IsHistoricalBackfill);

/// <summary>
/// An ordered raw-revision allocation prefix. SQL must persist the complete ledger and cursor atomically.
/// This is not a frozen current-head context, completed extraction or terminal-progress receipt.
/// </summary>
public sealed class GroupBatchAllocationPrefix
{
    public const int MaximumRawRevisions = 500;
    public const int MaximumCandidateRows = MaximumRawRevisions + 1;
    private GroupBatchAllocationPrefix(GroupScope scope, long afterSequence, long committedThrough,
        IReadOnlyList<GroupPendingRevisionMetadata> rows, GroupPendingRevisionMetadata? firstUnallocated)
    {
        Scope = scope;
        AfterSequence = afterSequence;
        ObservedCommittedThrough = committedThrough;
        Rows = rows;
        FirstUnallocated = firstUnallocated;
    }

    public GroupScope Scope { get; }
    public long AfterSequence { get; }
    public long ObservedCommittedThrough { get; }
    public long AllocatedThrough => Rows[^1].CommittedSequence;
    public bool HasUnallocatedSuffix => AllocatedThrough < ObservedCommittedThrough;
    public bool IsHistoricalBackfill => Rows[0].IsHistoricalBackfill;
    public IReadOnlyList<GroupPendingRevisionMetadata> Rows { get; }
    public GroupPendingRevisionMetadata? FirstUnallocated { get; }

    /// <summary>
    /// Input must be the exact bounded SQL prefix after the scheduled cursor, including its sentinel.
    /// Never sort a malformed/gapped input into apparent progress or use MAX(identity) as coverage.
    /// </summary>
    public static GroupBatchAllocationPrefix Select(GroupScope scope, long afterSequence, long committedThrough,
        IReadOnlyList<GroupPendingRevisionMetadata> candidates)
    {
        ArgumentNullException.ThrowIfNull(scope);
        scope.Validate();
        if (afterSequence < 0 || committedThrough <= afterSequence || candidates is null)
            throw Invalid();
        var expectedCount = (int)Math.Min(committedThrough - afterSequence, MaximumCandidateRows);
        if (candidates.Count != expectedCount) throw Invalid();
        var copy = candidates.ToArray();
        var revisions = new HashSet<(Guid, long)>();
        for (var index = 0; index < copy.Length; index++)
        {
            var row = copy[index];
            if (row is null || row.Scope != scope || row.MessageId == Guid.Empty || row.Revision <= 0
                || row.CommittedSequence != afterSequence + index + 1 || !Enum.IsDefined(row.Kind)
                || row.CommittedAtUtc.Offset != TimeSpan.Zero || !revisions.Add((row.MessageId, row.Revision)))
                throw Invalid();
            GroupWorkflowValidation.RequireSha256(row.ContentSha256);
        }

        var messageIds = new HashSet<Guid>();
        var selected = new List<GroupPendingRevisionMetadata>();
        foreach (var row in copy)
        {
            if (selected.Count == MaximumRawRevisions
                || row.IsHistoricalBackfill != copy[0].IsHistoricalBackfill
                || (!messageIds.Contains(row.MessageId) && messageIds.Count == FrozenGroupBatch.MaximumMessages)) break;
            messageIds.Add(row.MessageId);
            selected.Add(row);
        }
        var next = selected.Count < copy.Length ? copy[selected.Count] : null;
        return new(scope, afterSequence, committedThrough, selected.AsReadOnly(), next);
    }

    private static InvalidOperationException Invalid() => new("Group allocation prefix is invalid.");
}
