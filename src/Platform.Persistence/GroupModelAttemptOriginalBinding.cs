namespace MinhHuy.AIOffice.Platform.Persistence;

// Structural correspondence only. A caller-supplied carrier is never proof
// of persistence; the journal authenticates the whole immutable SQL plan in
// its current locked transaction before appending and returning a permit.
internal sealed class GroupModelAttemptOriginalBinding
{
    private GroupModelAttemptOriginalBinding(GroupAutomaticInputPlan plan, GroupAutomaticModelContext context,
        Guid operationId)
    { Plan = plan; Context = context; OperationId = operationId; }

    internal GroupAutomaticInputPlan Plan { get; }
    internal GroupAutomaticModelContext Context { get; }
    internal Guid OperationId { get; }
    public override string ToString() => "Group original attempt binding (private metadata).";

    internal static GroupModelAttemptOriginalBinding Create(GroupAutomaticInputPlan plan,
        GroupAutomaticInputPlan.RestoredChunk restored, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(plan); ArgumentNullException.ThrowIfNull(restored); token.ThrowIfCancellationRequested();
        try
        {
            var frozen = GroupAutomaticInputPlan.Read(plan.Encode());
            var context = restored.Current.ModelContext ?? throw Unavailable();
            var ordinal = restored.Original.Ordinal;
            if (ordinal < 1 || ordinal > frozen.Chunks.Count || restored.Current.Ordinal != ordinal
                || !ReferenceEquals(context.Preparation, restored.Current.Preparation)
                || !ReferenceEquals(context.Brain, restored.Brain)) throw Unavailable();
            var original = frozen.Chunks[ordinal - 1];
            if (!original.RequiresModel || !restored.Original.RequiresModel
                || restored.Original.OperationId != original.OperationId
                || restored.Original.InputSha256 != original.InputSha256
                || !restored.Original.Members.SequenceEqual(original.Members)
                || GroupModelAttemptBudget.InputIdentity(context) != original.InputSha256) throw Unavailable();
            var members = context.Preparation.Context.Items.OrderBy(x => x.CommittedSequence).ThenBy(x => x.MessageId)
                .Select(x => new GroupAutomaticInputPlan.Member(x.MessageId, x.Revision, x.CommittedSequence)).ToArray();
            if (!members.SequenceEqual(original.Members)
                || !frozen.HistoryOrder.SequenceEqual(context.Brain.Items.Select(x => new GroupAutomaticInputPlan.HistoryMember(x.Kind, x.RecordId))))
                throw Unavailable();
            var whole = frozen.Dependencies;
            var part = GroupWorkDependencyManifest.Read(GroupWorkDependencyManifest.Create(
                context.Preparation.Context, context.Brain, context.BatchId));
            if (part.Scope != whole.Scope || part.BatchId != whole.BatchId
                || part.AllocatedThroughSequence != whole.AllocatedThroughSequence
                || part.AuthoritySha256 != whole.AuthoritySha256 || part.CoverageSha256 != whole.CoverageSha256
                || !part.Dependencies.SequenceEqual(whole.Dependencies)
                || part.Sources.Count != original.Members.Count
                || !part.Sources.SequenceEqual(whole.Sources.Where(x => members.Any(m => m.MessageId == x.MessageId)))) throw Unavailable();
            token.ThrowIfCancellationRequested();
            return new(frozen, context, original.OperationId);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or OverflowException)
        { throw Unavailable(); }
    }

    private static InvalidOperationException Unavailable() => new("Group original model attempt binding is not available.");
}
