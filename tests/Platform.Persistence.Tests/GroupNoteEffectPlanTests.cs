using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class GroupBatchSourceReaderTests
{
    [Fact]
    public async Task CombinedEffectProjectionPreservesSeparateAiAndHostOriginsEvidenceAndOneSourceReceipt()
    {
        using var f = new Fixture();
        var caption = await f.CommitAsync(f.Payload(messageId: "effect-caption", eventId: "effect-caption", kind: GroupSourceEventKind.Media, text: "Tra cứu tồn kho"));
        var secret = await f.CommitAsync(f.Payload(messageId: "effect-secret", eventId: "effect-secret", kind: GroupSourceEventKind.Media, text: "Password=PRIVATE_SENTINEL_72691"));
        var social = await f.CommitAsync(f.Payload(messageId: "effect-social", eventId: "effect-social", text: "Cảm ơn"));
        var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(await f.ClaimAsync(), [caption.MessageId, secret.MessageId, social.MessageId]));
        var wire = ProposalWire(preparation.Candidates.Single(x => x.MessageId == caption.MessageId));
        wire["source_dispositions"]!.AsArray().Add(JsonSerializer.SerializeToNode(new { message_id = social.MessageId.ToString("D"), revision = social.Revision, disposition = "no_work" }));
        var counts = await f.CountsAsync(); var reads = f.Keys.Reads;
        var effect = GroupNoteEffectPlan.FromAutomatic(GroupAutomaticNotePlan.Create(preparation, GroupGroundedWorkProposal.Parse(preparation, wire.ToJsonString())));
        Assert.Equal(3, effect.Notes.Count); Assert.Equal(GroupWorkCommitOutcome.Notes, effect.Outcome); Assert.Equal(3, effect.Selected.Count);
        var ai = effect.Notes[0]; Assert.Equal(GroupRequestRevisionOrigin.AiExtracted, ai.Origin);
        Assert.Equal(GroupRequestVerificationLevel.SourceBackedAiInterpretation, ai.VerificationLevel);
        Assert.Equal(GroupRequestEvidenceKind.LiteralSourceQuote, ai.EvidenceKind);
        Assert.Equal(caption.MessageId, Assert.Single(GroupBrainPayloadCodec.DecodeAiNote(ai.Payload).Evidence).MessageId);
        foreach (var host in effect.Notes.Skip(1))
        {
            Assert.Equal(GroupRequestRevisionOrigin.HostAttention, host.Origin); Assert.Equal(GroupRequestVerificationLevel.HostObserved, host.VerificationLevel);
            Assert.Equal(GroupNoteKind.NeedsClarification, host.Kind); Assert.Equal(GroupNoteBusinessStatus.NeedsClarification, host.BusinessStatus);
            Assert.Equal(GroupRequestEvidenceKind.HostMetadataAttention, host.EvidenceKind);
            Assert.Equal(host.Evidence, GroupBrainPayloadCodec.DecodeHostAttention(host.Payload).SourceReferences);
            Assert.DoesNotContain("PRIVATE_SENTINEL", host.Payload);
        }
        Assert.Equal(GroupWorkSourceOutcome.Work, effect.Selected.Single(x => x.MessageId == caption.MessageId).Outcome);
        Assert.Equal(GroupWorkSourceOutcome.Quarantined, effect.Selected.Single(x => x.MessageId == secret.MessageId).Outcome);
        Assert.Equal(GroupWorkSourceOutcome.NoWork, effect.Selected.Single(x => x.MessageId == social.MessageId).Outcome);
        Assert.Throws<NotSupportedException>(() => ((IList<GroupNoteEffectPlan.Entry>)effect.Notes).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<GroupHostAttentionReference>)effect.Notes[1].Evidence).Clear());
        Assert.DoesNotContain("Tra cứu", effect.ToString()); Assert.Equal(counts, await f.CountsAsync()); Assert.Equal(reads, f.Keys.Reads);
        Assert.False(f.Auth.Db.ChangeTracker.HasChanges()); Assert.Empty(await f.Auth.Db.GroupWorkSourceDispositions.ToArrayAsync());
    }

    [Fact]
    public async Task HostOnlyEffectRetainsQuarantineWithoutAssertingAiOrRetryFailure()
    {
        using var f = new Fixture(); var source = await f.CommitAsync(f.Payload(kind: GroupSourceEventKind.Media, text: "Password=PRIVATE_SENTINEL_72691"));
        var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(await f.ClaimAsync(), [source.MessageId]));
        var effect = GroupNoteEffectPlan.FromAutomatic(GroupAutomaticNotePlan.Create(preparation));
        Assert.Equal(GroupWorkCommitOutcome.Attention, effect.Outcome); Assert.Equal(2, effect.Notes.Count); Assert.Single(effect.Selected);
        Assert.All(effect.Notes, x => Assert.Equal(GroupRequestRevisionOrigin.HostAttention, x.Origin));
        Assert.DoesNotContain(effect.Notes, x => GroupBrainPayloadCodec.DecodeHostAttention(x.Payload).Reason == GroupHostAttentionReason.ExtractionFailed);
    }

    [Fact]
    public async Task MaximumCombinedEffectKeepsFortyNotesAndTwoHundredNinetyNineEvidenceRows()
    {
        using var f = new Fixture(); var ids = new List<Guid>();
        for (var index = 0; index < 99; index++) ids.Add((await f.CommitAsync(f.Payload(messageId: $"effect-secret-{index}",
            eventId: $"effect-secret-{index}", kind: GroupSourceEventKind.Media, text: "Password=PRIVATE_SENTINEL_72691"))).MessageId);
        var caption = await f.CommitAsync(f.Payload(messageId: "effect-caption", eventId: "effect-caption", kind: GroupSourceEventKind.Media, text: "Tra cứu tồn kho"));
        ids.Add(caption.MessageId);
        var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(await f.ClaimAsync(), ids));
        var wire = ProposalWire(Assert.Single(preparation.Candidates)); var prototype = wire["notes"]![0]!;
        prototype["source_refs"] = JsonSerializer.SerializeToNode(new[] { "Tra", "cứu", "tồn", "kho", "Tra cứu tồn kho" }
            .Select(quote => new { message_id = caption.MessageId.ToString("D"), revision = caption.Revision, quote }));
        var copy = prototype.DeepClone(); var notes = wire["notes"]!.AsArray(); notes.Clear();
        for (var index = 0; index < 20; index++) { var note = copy.DeepClone(); note["title"] = "Request " + index; notes.Add(note); }
        var effect = GroupNoteEffectPlan.FromAutomatic(GroupAutomaticNotePlan.Create(preparation, GroupGroundedWorkProposal.Parse(preparation, wire.ToJsonString())));
        Assert.Equal(40, effect.MaximumNotes); Assert.Equal(40, effect.Notes.Count); Assert.Equal(300, effect.MaximumEvidenceRows);
        Assert.Equal(299, effect.Notes.Sum(x => x.Evidence.Count)); Assert.Equal(100, effect.Selected.Count);
        Assert.Equal(20, effect.Notes.Count(x => x.Origin == GroupRequestRevisionOrigin.AiExtracted));
        Assert.Equal(20, effect.Notes.Count(x => x.Origin == GroupRequestRevisionOrigin.HostAttention));
        Assert.Empty(await f.Auth.Db.GroupNotesCommittedItems.ToArrayAsync());
    }
}
