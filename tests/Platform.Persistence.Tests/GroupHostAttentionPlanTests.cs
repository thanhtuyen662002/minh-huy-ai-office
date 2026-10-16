using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class GroupBatchSourceReaderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostPlanMediaAttentionPreservesSeparateReadableCaptionCandidate(bool caption)
    {
        using var f = new Fixture(); var text = caption ? "Lỗi nhập xuất xem ảnh" : "";
        var source = await f.CommitAsync(f.Payload(kind: GroupSourceEventKind.Media, text: text));
        var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(await f.ClaimAsync(), [source.MessageId]));
        var counts = await f.CountsAsync(); var keys = f.Keys.Reads;
        var plan = GroupHostAttentionPlan.Create(preparation); var note = Assert.Single(plan.Notes);
        Assert.Equal(preparation.Scope, plan.Scope); Assert.Equal(preparation.BatchId, plan.BatchId);
        Assert.Equal(GroupHostAttentionReason.UnsupportedMedia, note.Reason); Assert.False(plan.HasCoverageGap);
        Assert.Equal(new GroupHostAttentionReference(source.MessageId, source.Revision), Assert.Single(note.SourceReferences));
        Assert.Equal(caption ? 1 : 0, preparation.Candidates.Count);
        if (caption) Assert.Equal(text, Assert.Single(preparation.Candidates).Text);
        var payload = GroupBrainPayloadCodec.DecodeHostAttention(note.EncodePayload()); Assert.Equal(note.SourceReferences, payload.SourceReferences);
        Assert.DoesNotContain("quote", note.EncodePayload()); Assert.DoesNotContain(text.Length == 0 ? "Lỗi nhập xuất" : text, note.EncodePayload());
        Assert.Equal(counts, await f.CountsAsync()); Assert.Equal(keys, f.Keys.Reads); Assert.False(f.Auth.Db.ChangeTracker.HasChanges());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostPlanQuarantineNeverReleasesPrivateTextAndMayAlsoObserveMedia(bool media)
    {
        using var f = new Fixture(); var text = "Password=PRIVATE_SENTINEL_72691";
        var source = await f.CommitAsync(f.Payload(kind: media ? GroupSourceEventKind.Media : GroupSourceEventKind.NewText, text: text));
        var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(await f.ClaimAsync(), [source.MessageId]));
        var plan = GroupHostAttentionPlan.Create(preparation); Assert.Empty(preparation.Candidates);
        Assert.Equal(media ? 2 : 1, plan.Notes.Count);
        var quarantine = plan.Notes.Single(x => x.Reason == GroupHostAttentionReason.SecretQuarantine);
        Assert.Equal(GroupSecretQuarantine.PolicyVersion, GroupBrainPayloadCodec.DecodeHostAttention(quarantine.EncodePayload()).QuarantinePolicyVersion);
        Assert.All(plan.Notes, note =>
        {
            Assert.Equal(new GroupHostAttentionReference(source.MessageId, source.Revision), Assert.Single(note.SourceReferences));
            Assert.DoesNotContain("PRIVATE_SENTINEL", note.EncodePayload()); Assert.DoesNotContain(text, note.ToString());
        });
        Assert.DoesNotContain(text, plan.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostPlanGapDoesNotConsumeOrReplaceKnownActionableText(bool media)
    {
        using var f = new Fixture(); var source = await f.CommitAsync(f.Payload(kind: media ? GroupSourceEventKind.Media : GroupSourceEventKind.NewText, text: "Tra cứu tồn kho"));
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
        var plan = GroupHostAttentionPlan.Create(preparation); Assert.True(plan.HasCoverageGap);
        Assert.Equal("Tra cứu tồn kho", Assert.Single(preparation.Candidates).Text);
        if (media) Assert.True(GroupBrainPayloadCodec.DecodeHostAttention(Assert.Single(plan.Notes).EncodePayload()).HasCoverageGap);
        else Assert.Empty(plan.Notes); // Gap-only durable reporting belongs to the later coverage ledger.
        Assert.Empty(await f.Auth.Db.GroupWorkSourceDispositions.ToArrayAsync()); Assert.Empty(await f.Auth.Db.GroupCustomerRequests.ToArrayAsync());
    }

    [Fact]
    public async Task HostPlanRecallDoesNotCreateMediaOrQuarantineAttentionOrModelVerdict()
    {
        using var f = new Fixture(); var source = await f.CommitAsync(f.Payload(kind: GroupSourceEventKind.Media, text: "Password=PRIVATE_SENTINEL_72691"));
        await f.CommitAsync(f.Payload(eventId: "recalled-media", kind: GroupSourceEventKind.Recall, text: ""));
        var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(await f.ClaimAsync(), [source.MessageId]));
        Assert.Empty(GroupHostAttentionPlan.Create(preparation).Notes); Assert.Empty(preparation.Candidates);
        Assert.Equal(GroupSourcePreparationDisposition.Recalled, Assert.Single(preparation.Receipts).Disposition); Assert.Equal(0, f.Keys.Reads);
    }

    [Fact]
    public async Task HostPlanChunksAllObservedReferencesDeterministicallyWithoutTruncation()
    {
        using var f = new Fixture(); var ids = new List<Guid>();
        for (var index = 0; index < 11; index++) ids.Add((await f.CommitAsync(f.Payload(messageId: $"host-media-{index}", eventId: $"host-media-event-{index}",
            kind: GroupSourceEventKind.Media, text: "Password=PRIVATE_SENTINEL_72691"))).MessageId);
        var claim = await f.ClaimAsync(); var first = GroupHostAttentionPlan.Create(GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(claim, ids)));
        ids.Reverse(); var second = GroupHostAttentionPlan.Create(GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(claim, ids)));
        Assert.Equal(first.Notes.Select(x => x.EncodePayload()), second.Notes.Select(x => x.EncodePayload())); Assert.Equal(4, first.Notes.Count);
        foreach (var reason in new[] { GroupHostAttentionReason.UnsupportedMedia, GroupHostAttentionReason.SecretQuarantine })
        {
            var notes = first.Notes.Where(x => x.Reason == reason).ToArray(); Assert.Equal(new[] { 10, 1 }, notes.Select(x => x.SourceReferences.Count));
            Assert.Equal(ids.OrderBy(x => x.ToString("D"), StringComparer.Ordinal), notes.SelectMany(x => x.SourceReferences).Select(x => x.MessageId));
            Assert.All(notes, note => Assert.Equal(reason, GroupBrainPayloadCodec.DecodeHostAttention(note.EncodePayload()).Reason));
        }
        Assert.Throws<NotSupportedException>(() => ((IList<GroupHostAttentionNote>)first.Notes).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<GroupHostAttentionReference>)first.Notes[0].SourceReferences).Clear());
        Assert.DoesNotContain(first.Notes, x => x.Reason == GroupHostAttentionReason.ExtractionFailed);
    }

    [Fact]
    public void HostPlanCannotBeForgedOrMutatedOrAssertRetryExhaustion()
    {
        Assert.Empty(typeof(GroupHostAttentionPlan).GetConstructors()); Assert.Empty(typeof(GroupHostAttentionNote).GetConstructors());
        Assert.All(typeof(GroupHostAttentionPlan).GetProperties(), x => Assert.Null(x.SetMethod));
        Assert.All(typeof(GroupHostAttentionNote).GetProperties(), x => Assert.Null(x.SetMethod));
        Assert.Throws<ArgumentNullException>(() => GroupHostAttentionPlan.Create(null!));
    }
}
