using System.Security.Cryptography;
using System.Text;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class GroupBatchSourceReaderTests
{
    [Theory]
    [InlineData(GroupSourceEventKind.Recall, false)]
    [InlineData(GroupSourceEventKind.Recall, true)]
    [InlineData(GroupSourceEventKind.Edit, false)]
    [InlineData(GroupSourceEventKind.Edit, true)]
    public async Task StructuralCoverageAcceptsActualReaderPriorityHeadsInsideAndBeforeAllocation(
        GroupSourceEventKind winningKind, bool beforeAllocation)
    {
        using var f = new Fixture();
        var winner = await f.CommitAsync(f.Payload(eventId: "priority-winner", kind: winningKind,
            text: winningKind == GroupSourceEventKind.Recall ? "" : "edited"));
        if (beforeAllocation) await f.AllocateAsync();
        var later = await f.CommitAsync(f.Payload(eventId: "later-lower-priority",
            kind: winningKind == GroupSourceEventKind.Recall ? GroupSourceEventKind.Edit : GroupSourceEventKind.NewText));
        var allocation = await f.AllocateAsync(); var claim = await f.ClaimAllocatedAsync(allocation.BatchId);
        var context = await f.Reader.ReadAsync(claim, [winner.MessageId]);
        var entry = Assert.Single(context.Items); Assert.Equal(winner.Revision, entry.Revision);
        Assert.True(entry.Revision < later.Revision);
        Assert.Equal(beforeAllocation, entry.CommittedSequence <= allocation.AfterSequence);
        var originalHead = Assert.Single(context.Snapshots).Head;
        var cutoff = new GroupPendingRevisionMetadata(context.Scope, originalHead.MessageId, originalHead.Revision,
            originalHead.CommittedSequence, originalHead.ContentSha256, originalHead.Kind,
            originalHead.CommittedAtUtc, originalHead.IsHistoricalBackfill);
        // Source/context/manifest are from actual InMemory shipping readers.
        // The effect receipt/dispositions below are structural test metadata,
        // not a native committed effect, model output or terminal transaction.
        var operation = Guid.NewGuid(); var scope = context.Scope;
        var receipt = new GroupWorkCommitReceiptRecord
        {
            TenantId = scope.TenantId,
            CompanyId = scope.CompanyId,
            BindingId = scope.SourceBindingId,
            BatchId = context.BatchId,
            OperationId = operation,
            SourceSetSha256 = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(
                FormattableString.Invariant($"{entry.MessageId:D}/{entry.Revision}")))),
            SelectedMessageCount = 1,
            NoteCount = 0,
            Outcome = GroupWorkCommitOutcome.NoWork,
            ServiceId = claim.Receipt.ServiceId,
            ClaimEpoch = claim.Receipt.Epoch,
            CredentialEpoch = claim.Receipt.CredentialEpoch,
            GrantVersion = claim.Receipt.GrantVersion,
            SourceVersion = claim.Receipt.SourceVersion,
            DeletionGeneration = claim.Receipt.DeletionGeneration,
            AccountVersion = claim.Receipt.AccountVersion,
            CommittedAtUtc = f.Auth.Clock.Current,
            DependencyManifestVersion = 1,
            DependencyManifest = GroupWorkDependencyManifest.Create(context, new(claim, [], []), operation)
        };
        var selected = new GroupWorkSourceDispositionRecord
        {
            TenantId = scope.TenantId,
            CompanyId = scope.CompanyId,
            BindingId = scope.SourceBindingId,
            BatchId = context.BatchId,
            OperationId = operation,
            MessageId = winner.MessageId,
            MessageRevision = winner.Revision,
            Outcome = GroupWorkSourceOutcome.NoWork
        };
        var raw = GroupWorkRawAccounting.Build(context, allocation,
            new Dictionary<Guid, (long Revision, GroupWorkSourceOutcome Outcome)> { [winner.MessageId] = (winner.Revision, selected.Outcome) }, operation);
        var result = GroupWholeBatchCoverage.Require(allocation, [cutoff], [receipt], [selected], raw);
        Assert.Equal(beforeAllocation ? 1 : 2, result.RawCount);
        Assert.Equal(winner.Revision, Assert.Single(Assert.Single(result.Manifests).Sources).Revision);
        if (beforeAllocation) Assert.All(raw, x => Assert.Equal(GroupWorkRawRelation.SupersededBySelectedHead, x.Relation));
        selected.MessageRevision = later.Revision;
        Assert.Throws<InvalidOperationException>(() => GroupWholeBatchCoverage.Require(allocation, [cutoff], [receipt], [selected], raw));
    }
}
