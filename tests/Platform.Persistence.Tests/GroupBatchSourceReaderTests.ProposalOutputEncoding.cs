using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class GroupBatchSourceReaderTests
{
    [Theory]
    [InlineData("incident", GroupWorkProposalKind.Incident)]
    [InlineData("change_request", GroupWorkProposalKind.ChangeRequest)]
    [InlineData("request", GroupWorkProposalKind.Request)]
    [InlineData("needs_clarification", GroupWorkProposalKind.NeedsClarification)]
    public async Task ValidatedOutputEncodingRetainsExactWireAndEveryInterpretationField(string kind, GroupWorkProposalKind expected)
    {
        using var f = new Fixture();
        const string text = "Tra cứu tồn kho ngày mai 😀\uFEFF \"chi tiết\"\n ";
        var source = await f.CommitAsync(f.Payload(text: text));
        var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(await f.ClaimAsync(), [source.MessageId]));
        var wire = ProposalWire(Assert.Single(preparation.Candidates));
        wire["notes"]![0]!["type"] = kind;
        wire["notes"]![0]!["missing_fields"] = JsonSerializer.SerializeToNode(new[] { "Kho cần tra cứu", "Loại nhập–xuất" });
        wire["notes"]![0]!["requested_deadline_text"] = "ngày mai";
        wire["notes"]![0]!["suggested_relation"] = "Gợi ý chưa xác nhận";
        var output = " \n" + wire.ToJsonString() + "\t ";
        var before = await f.CountsAsync(); var keys = f.Keys.Reads;
        var original = GroupGroundedWorkProposal.Parse(preparation, output);
        var bytes = original.EncodeValidatedOutput();
        Assert.Equal(Encoding.UTF8.GetBytes(output), bytes);
        var restored = GroupGroundedWorkProposal.Parse(preparation, Encoding.UTF8.GetString(bytes));
        var note = Assert.Single(restored.Notes); var prior = Assert.Single(original.Notes);
        Assert.Equal(expected, note.Kind);
        Assert.Equal(prior.TitleInterpretation, note.TitleInterpretation);
        Assert.Equal(prior.ProblemInterpretation, note.ProblemInterpretation);
        Assert.Equal(prior.RequestedOutcomeInterpretation, note.RequestedOutcomeInterpretation);
        Assert.Equal(prior.MissingFields, note.MissingFields);
        Assert.Equal("ngày mai", note.CustomerRequestedDeadlineText);
        Assert.Equal(prior.SuggestedRelationHint, note.SuggestedRelationHint);
        var evidence = Assert.Single(note.Evidence);
        Assert.Equal(text, evidence.Quote); Assert.Equal(source.MessageId, evidence.MessageId); Assert.Equal(source.Revision, evidence.Revision);
        Assert.Same(evidence, note.DeadlineEvidence);
        Assert.Equal(GroupModelSourceDisposition.Work, Assert.Single(restored.SourceDispositions).Disposition);
        bytes[0] ^= 1;
        Assert.Equal(Encoding.UTF8.GetBytes(output), original.EncodeValidatedOutput());
        Assert.Equal(before, await f.CountsAsync()); Assert.Equal(keys, f.Keys.Reads);
        Assert.False(f.Auth.Db.ChangeTracker.HasChanges());
        Assert.Empty(await f.Auth.Db.GroupCustomerRequests.ToArrayAsync());
        Assert.DoesNotContain(text, original.ToString());
    }

    [Fact]
    public async Task ValidatedOutputEncodingPreservesNoWorkAndAllDispositionOrdering()
    {
        using var f = new Fixture();
        var first = await f.CommitAsync(f.Payload(messageId: "first", eventId: "first", text: "Xin chào"));
        var second = await f.CommitAsync(f.Payload(messageId: "second", eventId: "second", text: "Cảm ơn"));
        var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(await f.ClaimAsync(), [first.MessageId, second.MessageId]));
        var output = JsonSerializer.Serialize(new
        {
            version = GroupGroundedWorkProposal.FormatVersion,
            notes = Array.Empty<object>(),
            source_dispositions = preparation.Candidates.Reverse().Select(x => new
            { message_id = x.MessageId.ToString("D"), revision = x.Revision, disposition = "no_work" })
        });
        var original = GroupGroundedWorkProposal.Parse(preparation, output);
        var restored = GroupGroundedWorkProposal.Parse(preparation, Encoding.UTF8.GetString(original.EncodeValidatedOutput()));
        Assert.Empty(restored.Notes);
        Assert.Equal(preparation.Candidates.Reverse().Select(x => x.MessageId), restored.SourceDispositions.Select(x => x.MessageId));
        Assert.All(restored.SourceDispositions, x => Assert.Equal(GroupModelSourceDisposition.NoWork, x.Disposition));
        Assert.Equal(2, GroupAutomaticNotePlan.Create(preparation, restored).SourceDispositions.Count);
    }

    [Fact]
    public async Task ValidatedOutputEncodingKeepsExactMaximumBoundWithoutTruncatingOrReserializing()
    {
        using var f = new Fixture(); var source = await f.CommitAsync();
        var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(await f.ClaimAsync(), [source.MessageId]));
        var prefix = ProposalWire(Assert.Single(preparation.Candidates)).ToJsonString();
        var output = prefix + new string(' ', GroupGroundedWorkProposal.MaximumOutputUtf8Bytes - Encoding.UTF8.GetByteCount(prefix));
        var proposal = GroupGroundedWorkProposal.Parse(preparation, output);
        var bytes = proposal.EncodeValidatedOutput();
        Assert.Equal(GroupGroundedWorkProposal.MaximumOutputUtf8Bytes, bytes.Length);
        Assert.Equal(Encoding.UTF8.GetBytes(output), bytes);
        Assert.Single(GroupGroundedWorkProposal.Parse(preparation, Encoding.UTF8.GetString(bytes)).Notes);
        Assert.Throws<InvalidOperationException>(() => GroupGroundedWorkProposal.Parse(preparation, output + " "));
    }

    [Fact]
    public async Task ValidatedOutputEncodingDoesNotGrantCurrentSourceAuthorityOrAllowForeignRecovery()
    {
        using var f = new Fixture(); var source = await f.CommitAsync(f.Payload(text: "Tra cứu tồn kho"));
        var claim = await f.ClaimAsync();
        var original = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(claim, [source.MessageId]));
        var proposal = GroupGroundedWorkProposal.Parse(original, ProposalWire(Assert.Single(original.Candidates)).ToJsonString());
        var bytes = proposal.EncodeValidatedOutput();
        await f.CommitAsync(f.Payload(eventId: "changed", kind: GroupSourceEventKind.Edit, text: "Đã đổi nội dung"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Reader.RequireCurrentAsync(original.Context));
        var current = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(claim, [source.MessageId]));
        var error = Assert.Throws<InvalidOperationException>(() => GroupGroundedWorkProposal.Parse(current, Encoding.UTF8.GetString(bytes)));
        Assert.Equal("Group work proposal is not available.", error.Message); Assert.Null(error.InnerException);
        Assert.Equal(bytes, proposal.EncodeValidatedOutput());
        Assert.Empty(await f.Auth.Db.GroupWorkCommitReceipts.ToArrayAsync());
        Assert.Empty(await f.Auth.Db.GroupNotesCommittedOutbox.ToArrayAsync());
    }

    [Fact]
    public void ValidatedOutputEncodingRemainsInternalAndCannotExposeASetterOrExportInvalidOutput()
    {
        Assert.Null(typeof(GroupGroundedWorkProposal).GetMethod("EncodeValidatedOutput"));
        Assert.Empty(typeof(GroupGroundedWorkProposal).GetConstructors());
        Assert.All(typeof(GroupGroundedWorkProposal).GetProperties(), x => Assert.Null(x.SetMethod));
    }
}
