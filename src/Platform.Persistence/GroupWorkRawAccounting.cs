using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

public enum GroupWorkRawRelation { SelectedHead = 1, SupersededBySelectedHead = 2 }

// Immutable accounting for a raw allocation row and its selected head. This
// does not say every raw body was model input or the whole batch is complete.
public sealed class GroupWorkRawDispositionRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid BindingId { get; set; }
    public Guid BatchId { get; set; }
    public long CommittedSequence { get; set; }
    public Guid MessageId { get; set; }
    public long RawRevision { get; set; }
    public long SelectedMessageRevision { get; set; }
    public Guid OperationId { get; set; }
    public GroupWorkSourceOutcome Outcome { get; set; }
    public GroupWorkRawRelation Relation { get; set; }
}

internal static class GroupWorkRawAccounting
{
    internal static GroupWorkRawDispositionRecord[] Build(GroupBatchSourceContext context,
        GroupBatchAllocationReceipt allocation, IReadOnlyDictionary<Guid, (long Revision, GroupWorkSourceOutcome Outcome)> selected,
        Guid operation)
    {
        var scope = context.Scope;
        if (operation == Guid.Empty || allocation.Scope != scope || allocation.BatchId != context.BatchId
            || allocation.AllocatedThroughSequence != context.AllocatedThroughSequence || allocation.AfterSequence < 0
            || allocation.Revisions.Count is < 1 or > GroupBatchAllocationPrefix.MaximumRawRevisions
            || allocation.AllocatedThroughSequence - allocation.AfterSequence != allocation.Revisions.Count
            || selected.Count is < 1 or > FrozenGroupBatch.MaximumMessages || selected.Count != context.Items.Count
            || context.Items.Any(item => !selected.TryGetValue(item.MessageId, out var value)
                || value.Revision != item.Revision || !Enum.IsDefined(value.Outcome))) throw Unavailable();
        var rows = new List<GroupWorkRawDispositionRecord>();
        for (var index = 0; index < allocation.Revisions.Count; index++)
        {
            var raw = allocation.Revisions[index].Metadata;
            if (raw.Scope != scope || raw.CommittedSequence != allocation.AfterSequence + index + 1
                || raw.MessageId == Guid.Empty || raw.Revision <= 0) throw Unavailable();
            if (!selected.TryGetValue(raw.MessageId, out var head)) continue;
            rows.Add(new()
            {
                TenantId = scope.TenantId,
                CompanyId = scope.CompanyId,
                BindingId = scope.SourceBindingId,
                BatchId = context.BatchId,
                CommittedSequence = raw.CommittedSequence,
                MessageId = raw.MessageId,
                RawRevision = raw.Revision,
                SelectedMessageRevision = head.Revision,
                OperationId = operation,
                Outcome = head.Outcome,
                Relation = raw.Revision == head.Revision ? GroupWorkRawRelation.SelectedHead : GroupWorkRawRelation.SupersededBySelectedHead
            });
        }
        if (rows.Select(x => x.MessageId).Distinct().Count() != selected.Count) throw Unavailable();
        return rows.ToArray();
    }

    internal static async Task RequireReplayAsync(PlatformDbContext database, GroupScope scope, Guid operation,
        IReadOnlyList<GroupWorkRawDispositionRecord> expected, CancellationToken token)
    {
        var rows = await database.GroupWorkRawDispositions.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.OperationId == operation)
            .OrderBy(x => x.CommittedSequence).Take(GroupBatchAllocationPrefix.MaximumCandidateRows).ToArrayAsync(token);
        if (rows.Length != expected.Count || rows.Where((row, index) => !Same(row, expected[index])).Any()) throw Unavailable();
    }

    private static bool Same(GroupWorkRawDispositionRecord left, GroupWorkRawDispositionRecord right) =>
        left.TenantId == right.TenantId && left.CompanyId == right.CompanyId && left.BindingId == right.BindingId
        && left.BatchId == right.BatchId && left.CommittedSequence == right.CommittedSequence && left.MessageId == right.MessageId
        && left.RawRevision == right.RawRevision && left.SelectedMessageRevision == right.SelectedMessageRevision
        && left.OperationId == right.OperationId && left.Outcome == right.Outcome && left.Relation == right.Relation;
    private static InvalidOperationException Unavailable() => new("Group raw source accounting is not available.");
}
