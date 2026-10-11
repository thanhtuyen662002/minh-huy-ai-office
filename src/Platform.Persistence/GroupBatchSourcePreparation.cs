using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using System.Globalization;
using System.Text;

namespace MinhHuy.AIOffice.Platform.Persistence;

public enum GroupSourcePreparationDisposition
{
    ModelText = 1, Quarantined = 2, EmptyText = 3, Recalled = 4, ObsoleteGeneration = 5, ChangedAfterCutoff = 6
}

// Provider candidates expose host-issued evidence IDs and original text only.
// Raw external IDs, sender/reply identities and credential-like input are not
// provider metadata. This is not a token-budgeted request or release authority.
public sealed class GroupPreparedModelSource
{
    internal GroupPreparedModelSource(GroupBatchSourceEntry entry)
    {
        MessageId = entry.MessageId; Revision = entry.Revision;
        Kind = entry.Kind; Text = entry.Text!; OccurredAtUtc = entry.OccurredAtUtc;
    }
    public Guid MessageId { get; }
    public long Revision { get; }
    public GroupSourceEventKind Kind { get; }
    public string Text { get; }
    public DateTimeOffset OccurredAtUtc { get; }
    public override string ToString() => "Prepared group source (private content).";
}

public sealed record GroupSourcePreparationReceipt(Guid MessageId, long Revision,
    GroupSourcePreparationDisposition Disposition, GroupSecretQuarantineReason? QuarantineReason,
    bool HasUnsupportedMedia);

public sealed class GroupBatchSourcePreparation
{
    private GroupBatchSourcePreparation(GroupBatchSourceContext context, GroupPreparedModelSource[] candidates,
        GroupSourcePreparationReceipt[] receipts)
    {
        Context = context; Candidates = Array.AsReadOnly(candidates); Receipts = Array.AsReadOnly(receipts);
    }
    internal GroupBatchSourceContext Context { get; }
    public GroupScope Scope => Context.Scope;
    public Guid BatchId => Context.BatchId;
    public string QuarantinePolicyVersion => GroupSecretQuarantine.PolicyVersion;
    public IReadOnlyList<GroupPreparedModelSource> Candidates { get; }
    public IReadOnlyList<GroupSourcePreparationReceipt> Receipts { get; }
    public override string ToString() => "Group source preparation (private content).";

    public static GroupBatchSourcePreparation Create(GroupBatchSourceContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var candidates = new List<GroupPreparedModelSource>();
        var receipts = new List<GroupSourcePreparationReceipt>();
        foreach (var entry in context.Items)
        {
            var disposition = entry.Disposition switch
            {
                GroupBatchSourceDisposition.Recalled => GroupSourcePreparationDisposition.Recalled,
                GroupBatchSourceDisposition.ObsoleteGeneration => GroupSourcePreparationDisposition.ObsoleteGeneration,
                GroupBatchSourceDisposition.ChangedAfterCutoff => GroupSourcePreparationDisposition.ChangedAfterCutoff,
                GroupBatchSourceDisposition.Readable => GroupSourcePreparationDisposition.ModelText,
                _ => throw new InvalidOperationException("Group source preparation is not available.")
            };
            GroupSecretQuarantineReason? reason = null;
            if (disposition == GroupSourcePreparationDisposition.ModelText)
            {
                var decision = GroupSecretQuarantine.Inspect(entry.Text);
                if (decision.RequiresQuarantine) { disposition = GroupSourcePreparationDisposition.Quarantined; reason = decision.Reason; }
                else if (!entry.Text!.EnumerateRunes().Any(rune => !Rune.IsWhiteSpace(rune)
                    && Rune.GetUnicodeCategory(rune) is not (UnicodeCategory.Format or UnicodeCategory.Control)))
                    disposition = GroupSourcePreparationDisposition.EmptyText;
                else candidates.Add(new(entry));
            }
            // Every selected item has exactly one receipt. Media captions may
            // enter the model, but the unknown media still requires host attention.
            receipts.Add(new(entry.MessageId, entry.Revision, disposition, reason, entry.Kind == GroupSourceEventKind.Media));
        }
        return new(context, candidates.ToArray(), receipts.ToArray());
    }
}
