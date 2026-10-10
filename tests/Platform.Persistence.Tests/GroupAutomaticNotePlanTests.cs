using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using System.Text.Json;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class GroupBatchSourceReaderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticPlanRetainsMediaAttentionAlongsideTextWorkOrNoWork(bool work)
    {
        using var f = new Fixture(); var source = await f.CommitAsync(f.Payload(kind: GroupSourceEventKind.Media, text: "Tra cứu tồn kho"));
        var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(await f.ClaimAsync(), [source.MessageId]));
        var wire = ProposalWire(Assert.Single(preparation.Candidates));
        if (!work) { wire["notes"]!.AsArray().Clear(); wire["source_dispositions"]![0]!["disposition"] = "no_work"; }
        var proposal = GroupGroundedWorkProposal.Parse(preparation, wire.ToJsonString());
        var counts = await f.CountsAsync(); var keys = f.Keys.Reads;
        var plan = GroupAutomaticNotePlan.Create(preparation, proposal);
        Assert.Equal(work ? 2 : 1, plan.NoteCount); Assert.Equal(work ? 1 : 0, plan.AiNotes.Count); Assert.Single(plan.HostNotes);
        var receipt = Assert.Single(plan.SourceDispositions);
        Assert.Equal(source.MessageId, receipt.MessageId); Assert.Equal(source.Revision, receipt.Revision); Assert.True(receipt.HasHostAttention);
        Assert.Equal(work ? GroupWorkSourceOutcome.Work : GroupWorkSourceOutcome.Attention, receipt.Outcome);
        Assert.Equal(counts, await f.CountsAsync()); Assert.Equal(keys, f.Keys.Reads); Assert.False(f.Auth.Db.ChangeTracker.HasChanges());
        Assert.Empty(await f.Auth.Db.GroupCustomerRequests.ToArrayAsync()); Assert.Empty(await f.Auth.Db.GroupWorkSourceDispositions.ToArrayAsync());
    }

    [Fact]
    public async Task AutomaticPlanQuarantinedMediaNeedsNoModelAndHasOneDispositionForTwoHostNotes()
    {
        using var f = new Fixture(); var source = await f.CommitAsync(f.Payload(kind: GroupSourceEventKind.Media, text: "Password=PRIVATE_SENTINEL_72691"));
        var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(await f.ClaimAsync(), [source.MessageId]));
        var plan = GroupAutomaticNotePlan.Create(preparation);
        Assert.Empty(plan.AiNotes); Assert.Equal(2, plan.HostNotes.Count); Assert.Equal(2, plan.NoteCount);
        var receipt = Assert.Single(plan.SourceDispositions); Assert.Equal(GroupWorkSourceOutcome.Quarantined, receipt.Outcome); Assert.True(receipt.HasHostAttention);
        Assert.All(plan.HostNotes, note => Assert.DoesNotContain("PRIVATE_SENTINEL", note.EncodePayload()));
        Assert.DoesNotContain("PRIVATE_SENTINEL", plan.ToString()); Assert.DoesNotContain("PRIVATE_SENTINEL", receipt.ToString());
    }

    [Fact]
    public async Task AutomaticPlanPreservesEveryMixedSourceOnceAndDoesNotInferRawCompletion()
    {
        using var f = new Fixture();
        var work = await f.CommitAsync(f.Payload(messageId: "mixed-work", eventId: "mixed-work", kind: GroupSourceEventKind.Media, text: "Tra cứu tồn kho"));
        var media = await f.CommitAsync(f.Payload(messageId: "mixed-media", eventId: "mixed-media", kind: GroupSourceEventKind.Media, text: ""));
        var secret = await f.CommitAsync(f.Payload(messageId: "mixed-secret", eventId: "mixed-secret", kind: GroupSourceEventKind.Media, text: "Password=PRIVATE_SENTINEL_72691"));
        var social = await f.CommitAsync(f.Payload(messageId: "mixed-social", eventId: "mixed-social", text: "Cảm ơn"));
        var empty = await f.CommitAsync(f.Payload(messageId: "mixed-empty", eventId: "mixed-empty", text: ""));
        var recalled = await f.CommitAsync(f.Payload(messageId: "mixed-recalled", eventId: "mixed-recalled", kind: GroupSourceEventKind.Media, text: "Ảnh"));
        await f.CommitAsync(f.Payload(messageId: "mixed-recalled", eventId: "mixed-recall", kind: GroupSourceEventKind.Recall, text: ""));
        var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(await f.ClaimAsync(),
            [social.MessageId, secret.MessageId, recalled.MessageId, work.MessageId, empty.MessageId, media.MessageId]));
        var wire = ProposalWire(preparation.Candidates.Single(x => x.MessageId == work.MessageId));
        wire["source_dispositions"]!.AsArray().Add(JsonSerializer.SerializeToNode(new { message_id = social.MessageId.ToString("D"), revision = social.Revision, disposition = "no_work" }));
        var plan = GroupAutomaticNotePlan.Create(preparation, GroupGroundedWorkProposal.Parse(preparation, wire.ToJsonString()));
        var receipts = plan.SourceDispositions.ToDictionary(x => x.MessageId);
        Assert.Equal(6, receipts.Count); Assert.Equal(6, plan.SourceDispositions.Count);
        Assert.Equal(GroupWorkSourceOutcome.Work, receipts[work.MessageId].Outcome);
        Assert.Equal(GroupWorkSourceOutcome.Attention, receipts[media.MessageId].Outcome);
        Assert.Equal(GroupWorkSourceOutcome.Quarantined, receipts[secret.MessageId].Outcome);
        Assert.Equal(GroupWorkSourceOutcome.NoWork, receipts[social.MessageId].Outcome);
        Assert.Equal(GroupWorkSourceOutcome.NoWork, receipts[empty.MessageId].Outcome);
        Assert.Equal(GroupWorkSourceOutcome.Recalled, receipts[recalled.MessageId].Outcome); Assert.False(receipts[recalled.MessageId].HasHostAttention);
        Assert.Empty(await f.Auth.Db.GroupWorkSourceDispositions.ToArrayAsync()); Assert.Empty(await f.Auth.Db.GroupNotesCommittedOutbox.ToArrayAsync());
    }

    [Fact]
    public async Task AutomaticPlanRetainsGapFlagAndKnownWorkWithoutClaimingCompleteCoverage()
    {
        using var f = new Fixture(); var source = await f.CommitAsync(f.Payload(kind: GroupSourceEventKind.Media, text: "Tra cứu tồn kho"));
        var claim = await f.ClaimAsync();
        f.Auth.Db.Add(new GroupCoverageGapRecord
        {
            TenantId = f.Auth.Scope.TenantId,
            CompanyId = f.Auth.Scope.CompanyId,
            BindingId = f.Auth.Scope.SourceBindingId,
            Id = Guid.NewGuid(),
            AfterCommittedSequence = 1,
            Reason = "owned-gap",
            OpenedAtUtc = f.Auth.Clock.Current
        });
        await f.Auth.Db.SaveChangesAsync();
        var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(claim, [source.MessageId]));
        var plan = GroupAutomaticNotePlan.Create(preparation,
            GroupGroundedWorkProposal.Parse(preparation, ProposalWire(Assert.Single(preparation.Candidates)).ToJsonString()));
        Assert.True(plan.HasCoverageGap); Assert.Single(plan.AiNotes);
        Assert.True(GroupBrainPayloadCodec.DecodeHostAttention(Assert.Single(plan.HostNotes).EncodePayload()).HasCoverageGap);
        Assert.Equal(GroupWorkSourceOutcome.Work, Assert.Single(plan.SourceDispositions).Outcome);
        Assert.Empty(await f.Auth.Db.GroupWorkSourceDispositions.ToArrayAsync()); Assert.Empty(await f.Auth.Db.GroupWorkCommitReceipts.ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticPlanCarriesChangedAndObsoleteMetadataWithoutInventingMediaOrModelVerdict(bool obsolete)
    {
        using var f = new Fixture(); var source = await f.CommitAsync(f.Payload(kind: GroupSourceEventKind.Media, text: "Password=PRIVATE_SENTINEL_72691"));
        var allocation = await f.AllocateAsync();
        if (obsolete)
        {
            f.Auth.Binding.DeletionGeneration++; f.Auth.Binding.Version++; await f.Auth.Db.SaveChangesAsync();
        }
        else
        {
            (await f.Auth.Db.GroupListenerLeases.SingleAsync()).ExpiresAtUtc = f.Auth.Clock.Current.AddSeconds(30); await f.Auth.Db.SaveChangesAsync();
            await f.CommitAsync(f.Payload(eventId: "combined-post-cutoff", kind: GroupSourceEventKind.Edit, text: "changed"));
        }
        var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(await f.ClaimAllocatedAsync(allocation.BatchId), [source.MessageId]));
        var plan = GroupAutomaticNotePlan.Create(preparation);
        Assert.Empty(plan.AiNotes); Assert.Empty(plan.HostNotes); Assert.Equal(0, plan.NoteCount);
        var receipt = Assert.Single(plan.SourceDispositions); Assert.False(receipt.HasHostAttention);
        Assert.Equal(obsolete ? GroupWorkSourceOutcome.ObsoleteGeneration : GroupWorkSourceOutcome.ChangedAfterCutoff, receipt.Outcome);
        Assert.Equal(0, f.Keys.Reads); Assert.Empty(await f.Auth.Db.GroupWorkSourceDispositions.ToArrayAsync());
    }

    [Fact]
    public async Task AutomaticPlanRequiresSealedProposalForExactPreparationEvenWithIdenticalScopeAndIds()
    {
        using var f = new Fixture(); var source = await f.CommitAsync(); var context = await f.Reader.ReadAsync(await f.ClaimAsync(), [source.MessageId]);
        var first = GroupBatchSourcePreparation.Create(context); var second = GroupBatchSourcePreparation.Create(context);
        var proposal = GroupGroundedWorkProposal.Parse(first, ProposalWire(Assert.Single(first.Candidates)).ToJsonString());
        Assert.Equal(first.Scope, second.Scope); Assert.Equal(first.BatchId, second.BatchId);
        Assert.Throws<InvalidOperationException>(() => GroupAutomaticNotePlan.Create(first));
        var error = Assert.Throws<InvalidOperationException>(() => GroupAutomaticNotePlan.Create(second, proposal));
        Assert.Equal("Group automatic note plan is not available.", error.Message);
        Assert.Single(GroupAutomaticNotePlan.Create(first, proposal).AiNotes);
    }

    [Fact]
    public async Task AutomaticPlanRetainsAllFortyBoundedNotesAndAllHundredSelectedSources()
    {
        using var f = new Fixture(); var ids = new List<Guid>();
        for (var index = 0; index < 99; index++) ids.Add((await f.CommitAsync(f.Payload(messageId: $"combined-secret-{index}",
            eventId: $"combined-secret-{index}", kind: GroupSourceEventKind.Media, text: "Password=PRIVATE_SENTINEL_72691"))).MessageId);
        var caption = await f.CommitAsync(f.Payload(messageId: "combined-caption", eventId: "combined-caption", kind: GroupSourceEventKind.Media, text: "Tra cứu tồn kho"));
        ids.Add(caption.MessageId);
        var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(await f.ClaimAsync(), ids));
        var wire = ProposalWire(Assert.Single(preparation.Candidates)); var prototype = wire["notes"]![0]!.DeepClone();
        var notes = wire["notes"]!.AsArray(); notes.Clear();
        for (var index = 0; index < 20; index++) { var note = prototype.DeepClone(); note["title"] = "Request " + index; notes.Add(note); }
        var plan = GroupAutomaticNotePlan.Create(preparation, GroupGroundedWorkProposal.Parse(preparation, wire.ToJsonString()));
        Assert.Equal(GroupAutomaticNotePlan.MaximumNotes, plan.NoteCount); Assert.Equal(20, plan.AiNotes.Count); Assert.Equal(20, plan.HostNotes.Count);
        Assert.Equal(100, plan.SourceDispositions.Count); Assert.Equal(100, plan.SourceDispositions.Select(x => x.MessageId).Distinct().Count());
        Assert.Equal(99, plan.SourceDispositions.Count(x => x.Outcome == GroupWorkSourceOutcome.Quarantined));
        Assert.Equal(GroupWorkSourceOutcome.Work, plan.SourceDispositions.Single(x => x.MessageId == caption.MessageId).Outcome);
        Assert.All(plan.SourceDispositions, x => Assert.True(x.HasHostAttention));
        Assert.Throws<NotSupportedException>(() => ((IList<GroupAutomaticSourceReceipt>)plan.SourceDispositions).Clear());
    }

    [Fact]
    public void AutomaticPlanCannotBeForgedMutatedOrAssertRetryExhaustion()
    {
        Assert.Empty(typeof(GroupAutomaticNotePlan).GetConstructors()); Assert.Empty(typeof(GroupAutomaticSourceReceipt).GetConstructors());
        Assert.All(typeof(GroupAutomaticNotePlan).GetProperties(), property => Assert.Null(property.SetMethod));
        Assert.All(typeof(GroupAutomaticSourceReceipt).GetProperties(), property => Assert.Null(property.SetMethod));
        Assert.Throws<ArgumentNullException>(() => GroupAutomaticNotePlan.Create(null!));
    }
}
