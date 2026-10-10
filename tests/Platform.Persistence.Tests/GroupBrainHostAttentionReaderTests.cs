using System.Collections;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class GroupBatchSourceReaderTests
{
    private static async Task<BrainSeed> SeedHostAttentionAsync(Fixture f, GroupBatchClaimHandle claim,
        GroupIngressCommittedReceipt source, GroupHostAttentionReason reason, bool gap = false, string? wire = null)
    {
        var seed = await SeedBrainAsync(f, claim, source, wire ?? GroupBrainPayloadCodec.EncodeHostAttention(reason, gap, [new(source.MessageId, source.Revision)]));
        seed.Request.Kind = reason == GroupHostAttentionReason.ExtractionFailed ? GroupNoteKind.ExtractionFailed : GroupNoteKind.NeedsClarification;
        seed.Request.BusinessStatus = GroupNoteBusinessStatus.NeedsClarification;
        seed.Revision.Origin = GroupRequestRevisionOrigin.HostAttention;
        seed.Revision.VerificationLevel = GroupRequestVerificationLevel.HostObserved;
        (await f.Auth.Db.GroupRequestEvidence.SingleAsync()).Kind = GroupRequestEvidenceKind.HostMetadataAttention;
        await f.Auth.Db.SaveChangesAsync(); return seed;
    }

    [Theory]
    [InlineData(GroupHostAttentionReason.UnsupportedMedia, false)]
    [InlineData(GroupHostAttentionReason.UnsupportedMedia, true)]
    [InlineData(GroupHostAttentionReason.SecretQuarantine, false)]
    [InlineData(GroupHostAttentionReason.SecretQuarantine, true)]
    [InlineData(GroupHostAttentionReason.ExtractionFailed, false)]
    [InlineData(GroupHostAttentionReason.ExtractionFailed, true)]
    public async Task BrainReadsHostObservationWithoutAiItOrBusinessConfirmation(GroupHostAttentionReason reason, bool gap)
    {
        using var f = new Fixture(); var source = await f.CommitAsync(); var claim = await f.ClaimAsync();
        var seed = await SeedHostAttentionAsync(f, claim, source, reason, gap);
        var reader = BrainReader(f); var context = await reader.ReadAsync(claim, [seed.Request.Id], [seed.Glossary.Id]);
        var host = context.Items[0];
        Assert.Equal(GroupRequestRevisionOrigin.HostAttention, host.Origin);
        Assert.Equal(GroupRequestVerificationLevel.HostObserved, host.VerificationLevel);
        Assert.Equal(GroupNoteBusinessStatus.NeedsClarification, host.BusinessStatus); Assert.False(host.IsItConfirmed);
        var payload = GroupBrainPayloadCodec.DecodeHostAttention(host.Content);
        Assert.Equal(reason, payload.Reason); Assert.Equal(gap, payload.HasCoverageGap);
        Assert.Equal(new GroupHostAttentionReference(source.MessageId, source.Revision), Assert.Single(payload.SourceReferences));
        Assert.Null(context.Items[1].Origin); Assert.Null(context.Items[1].VerificationLevel); Assert.False(context.Items[1].IsItConfirmed);
        await reader.RequireCurrentAsync(context); Assert.Equal(1, f.Keys.Reads);
        Assert.False(f.Auth.Db.ChangeTracker.HasChanges()); Assert.Single(await f.Auth.Db.GroupCustomerRequests.ToArrayAsync());
        Assert.Empty(await f.Auth.Db.GroupNotesCommittedOutbox.ToArrayAsync());
    }

    [Theory]
    [InlineData("ai-level")]
    [InlineData("it-level")]
    [InlineData("null-service")]
    [InlineData("empty-service")]
    [InlineData("user")]
    [InlineData("null-batch")]
    [InlineData("empty-batch")]
    [InlineData("null-epoch")]
    [InlineData("zero-epoch")]
    [InlineData("work-kind")]
    [InlineData("new-status")]
    [InlineData("it-confirmation")]
    [InlineData("literal-evidence")]
    public async Task BrainHostMetadataCannotMasqueradeAsInterpretationOrConfirmedWorkBeforeKeys(string fault)
    {
        using var f = new Fixture(); var source = await f.CommitAsync(); var claim = await f.ClaimAsync();
        var seed = await SeedHostAttentionAsync(f, claim, source, GroupHostAttentionReason.UnsupportedMedia);
        switch (fault)
        {
            case "ai-level": seed.Revision.VerificationLevel = GroupRequestVerificationLevel.SourceBackedAiInterpretation; break;
            case "it-level": seed.Revision.VerificationLevel = GroupRequestVerificationLevel.ItConfirmed; break;
            case "null-service": seed.Revision.AuthorServiceId = null; break;
            case "empty-service": seed.Revision.AuthorServiceId = Guid.Empty; break;
            case "user": seed.Revision.AuthorUserId = Guid.NewGuid(); break;
            case "null-batch": seed.Revision.SourceBatchId = null; break;
            case "empty-batch": seed.Revision.SourceBatchId = Guid.Empty; break;
            case "null-epoch": seed.Revision.ClaimEpoch = null; break;
            case "zero-epoch": seed.Revision.ClaimEpoch = 0; break;
            case "work-kind": seed.Request.Kind = GroupNoteKind.Incident; break;
            case "new-status": seed.Request.BusinessStatus = GroupNoteBusinessStatus.New; break;
            case "it-confirmation": seed.Request.ConfirmedByUserId = Guid.NewGuid(); seed.Request.ConfirmedAtUtc = seed.Request.CreatedAtUtc; break;
            case "literal-evidence": (await f.Auth.Db.GroupRequestEvidence.SingleAsync()).Kind = GroupRequestEvidenceKind.LiteralSourceQuote; break;
            default: throw new InvalidOperationException();
        }
        await f.Auth.Db.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => BrainReader(f).ReadAsync(claim, [seed.Request.Id], []));
        Assert.Equal("Group brain context is unavailable.", error.Message); Assert.Null(error.InnerException); Assert.Equal(0, f.Keys.Reads);
    }

    [Theory]
    [InlineData("opaque")]
    [InlineData("foreign-ref")]
    [InlineData("wrong-revision")]
    [InlineData("failed-kind")]
    [InlineData("invented-quote")]
    [InlineData("wrong-policy")]
    public async Task BrainHostClosedPayloadMustMatchExactMetadataEvidenceAndReason(string fault)
    {
        using var f = new Fixture(); var source = await f.CommitAsync(); var claim = await f.ClaimAsync();
        var reason = fault == "failed-kind" ? GroupHostAttentionReason.ExtractionFailed : GroupHostAttentionReason.UnsupportedMedia;
        var wire = GroupBrainPayloadCodec.EncodeHostAttention(reason, false,
            [new(fault == "foreign-ref" ? Guid.NewGuid() : source.MessageId, fault == "wrong-revision" ? source.Revision + 1 : source.Revision)]);
        if (fault == "opaque") wire = "{\"private\":\"NEVER_EXPORT\"}";
        if (fault == "invented-quote") wire = wire.Replace("\"has_coverage_gap\":", "\"quote\":\"NEVER_EXPORT\",\"has_coverage_gap\":", StringComparison.Ordinal);
        if (fault == "wrong-policy") wire = wire.Replace("\"quarantine_policy_version\":null", "\"quarantine_policy_version\":\"unknown-policy\"", StringComparison.Ordinal);
        var seed = await SeedHostAttentionAsync(f, claim, source, GroupHostAttentionReason.UnsupportedMedia, wire: wire);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => BrainReader(f).ReadAsync(claim, [seed.Request.Id], []));
        Assert.Equal("Group brain context is unavailable.", error.Message); Assert.Null(error.InnerException); Assert.Equal(1, f.Keys.Reads);
    }

    [Fact]
    public async Task BrainAiRevisionRequiresLiteralEvidenceAndItsOriginDoesNotConfirmBusiness()
    {
        using var f = new Fixture(); var source = await f.CommitAsync(); var claim = await f.ClaimAsync();
        var seed = await SeedBrainAsync(f, claim, source); var reader = BrainReader(f);
        var item = Assert.Single((await reader.ReadAsync(claim, [seed.Request.Id], [])).Items);
        Assert.Equal(GroupRequestRevisionOrigin.AiExtracted, item.Origin);
        Assert.Equal(GroupRequestVerificationLevel.SourceBackedAiInterpretation, item.VerificationLevel); Assert.False(item.IsItConfirmed);
        (await f.Auth.Db.GroupRequestEvidence.SingleAsync()).Kind = GroupRequestEvidenceKind.HostMetadataAttention;
        await f.Auth.Db.SaveChangesAsync(); var reads = f.Keys.Reads;
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAsync(claim, [seed.Request.Id], [])); Assert.Equal(reads, f.Keys.Reads);
    }

    [Fact]
    public async Task BrainItEditedRevisionAndBusinessConfirmationRemainSeparate()
    {
        using var f = new Fixture(); var source = await f.CommitAsync(); var claim = await f.ClaimAsync();
        var seed = await SeedBrainAsync(f, claim, source);
        seed.Revision.Origin = GroupRequestRevisionOrigin.ItEdited; seed.Revision.VerificationLevel = GroupRequestVerificationLevel.ItConfirmed;
        seed.Revision.AuthorServiceId = null; seed.Revision.AuthorUserId = seed.Glossary.PublishedByUserId;
        seed.Revision.SourceBatchId = null; seed.Revision.ClaimEpoch = null; await f.Auth.Db.SaveChangesAsync();
        var reader = BrainReader(f); var context = await reader.ReadAsync(claim, [seed.Request.Id], []);
        var item = Assert.Single(context.Items); Assert.Equal(GroupRequestRevisionOrigin.ItEdited, item.Origin);
        Assert.Equal(GroupRequestVerificationLevel.ItConfirmed, item.VerificationLevel); Assert.False(item.IsItConfirmed);
        seed.Request.ConfirmedByUserId = seed.Revision.AuthorUserId; seed.Request.ConfirmedAtUtc = seed.Request.CreatedAtUtc;
        seed.Request.BusinessStatus = GroupNoteBusinessStatus.Resolved; seed.Request.BusinessVersion++; await f.Auth.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.RequireCurrentAsync(context));
        Assert.True(Assert.Single((await reader.ReadAsync(claim, [seed.Request.Id], [])).Items).IsItConfirmed);
    }

    [Theory]
    [InlineData("verification")]
    [InlineData("evidence")]
    [InlineData("recall")]
    public async Task BrainHostDependencyChangeDuringKeyAwaitWinsBeforePrivateRelease(string change)
    {
        using var f = new Fixture(); var source = await f.CommitAsync(); var claim = await f.ClaimAsync();
        var seed = await SeedHostAttentionAsync(f, claim, source, GroupHostAttentionReason.SecretQuarantine);
        f.Keys.BeforeRead = async () =>
        {
            if (change == "recall") await f.CommitAsync(f.Payload(eventId: "later-host", kind: GroupSourceEventKind.Recall, text: ""));
            else
            {
                if (change == "verification") seed.Revision.VerificationLevel = GroupRequestVerificationLevel.SourceBackedAiInterpretation;
                else (await f.Auth.Db.GroupRequestEvidence.SingleAsync()).Kind = GroupRequestEvidenceKind.LiteralSourceQuote;
                await f.Auth.Db.SaveChangesAsync();
            }
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => BrainReader(f).ReadAsync(claim, [seed.Request.Id], [])); Assert.Equal(1, f.Keys.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BrainSelectionPreservesBothActualIListItemsInEitherPurpose(bool glossary)
    {
        using var f = new Fixture(); var source = await f.CommitAsync(); var claim = await f.ClaimAsync();
        var first = await SeedBrainAsync(f, claim, source); var second = await SeedBrainAsync(f, claim, source);
        var ids = glossary ? new[] { first.Glossary.Id, second.Glossary.Id } : new[] { first.Request.Id, second.Request.Id };
        var caller = new FalseSizedBrainIds(ids);
        var context = await BrainReader(f).ReadAsync(claim, glossary ? [] : caller, glossary ? caller : []);
        Assert.Equal(ids, context.Items.Select(x => x.RecordId)); Assert.Equal(1, f.Keys.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BrainSelectionRejectsActualIListOverflowBeforeKeys(bool combined)
    {
        using var f = new Fixture(); await f.CommitAsync(); var claim = await f.ClaimAsync();
        var caller = new FalseSizedBrainIds(Enumerable.Range(0, combined ? 20 : 21).Select(_ => Guid.NewGuid()).ToArray());
        await Assert.ThrowsAsync<InvalidOperationException>(() => BrainReader(f).ReadAsync(claim, caller, combined ? [Guid.NewGuid()] : []));
        Assert.Equal(0, f.Keys.Reads);
    }

    private sealed class FalseSizedBrainIds(Guid[] values) : IReadOnlyList<Guid>, IList<Guid>
    {
        public int Count => 1;
        public Guid this[int index] { get => values[index]; set => throw new NotSupportedException(); }
        public bool IsReadOnly => true;
        public IEnumerator<Guid> GetEnumerator() => ((IEnumerable<Guid>)values).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public void Add(Guid item) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
        public bool Contains(Guid item) => throw new NotSupportedException();
        public void CopyTo(Guid[] array, int arrayIndex) => throw new NotSupportedException();
        public int IndexOf(Guid item) => throw new NotSupportedException();
        public void Insert(int index, Guid item) => throw new NotSupportedException();
        public bool Remove(Guid item) => throw new NotSupportedException();
        public void RemoveAt(int index) => throw new NotSupportedException();
    }
}
