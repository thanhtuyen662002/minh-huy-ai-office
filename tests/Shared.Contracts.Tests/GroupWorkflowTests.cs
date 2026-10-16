using System.Text.Json;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Shared.Contracts.Tests;

public sealed class GroupWorkflowTests
{
    private static GroupScope Scope => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
    private static GroupMessageRevisionReference Message(long sequence = 1) => new(Guid.NewGuid(), 2, sequence, new string('A', 64));
    private static FrozenGroupBatch Batch(params GroupMessageRevisionReference[] messages) =>
        FrozenGroupBatch.Create(Guid.NewGuid(), Scope, 100, GroupCoverage.PartialGap, false, messages.Length == 0 ? [Message()] : messages);
    private static GroupNoteProposal Proposal(FrozenGroupBatch batch) => new("incident_1", GroupNoteKind.Incident,
        "Customer-reported issue", "Stock report does not open", null,
        [new(batch.Messages[0].MessageId, batch.Messages[0].Revision, "report does not open")], [], "tomorrow");

    [Fact]
    public void Frozen_scope_exact_revision_hash_and_commit_cutoff_are_preserved()
    {
        var first = Message(1);
        var second = Message(2);
        var original = new[] { second, first };
        var batch = FrozenGroupBatch.Create(Guid.NewGuid(), Scope, 2, GroupCoverage.PartialGap, false, original);
        original[0] = Message(3);
        Assert.Equal(new[] { first, second }, batch.Messages);
        Assert.Equal(2, batch.CommittedCutoff);
        Assert.Equal(GroupCoverage.PartialGap, batch.Coverage);
        Assert.True(batch.MayNotify);
        Assert.Throws<NotSupportedException>(() => ((IList<GroupMessageRevisionReference>)batch.Messages).Clear());
    }

    [Fact]
    public void Duplicate_message_or_sequence_future_revision_and_invalid_hash_are_refused()
    {
        var message = Message();
        Assert.Throws<InvalidOperationException>(() => Batch(message, message));
        Assert.Throws<InvalidOperationException>(() => Batch(message, Message()));
        Assert.Throws<InvalidOperationException>(() => Batch(message, message with { Revision = 3, CommittedSequence = 2 }));
        Assert.Throws<InvalidOperationException>(() => Batch(message with { CommittedSequence = 101 }));
        Assert.Throws<InvalidOperationException>(() => Batch(message with { ContentSha256 = new string('a', 64) }));
        Assert.Throws<InvalidOperationException>(() => Batch(message with { Revision = 0 }));
        Assert.Throws<InvalidOperationException>(() => Batch(Enumerable.Range(1, 101).Select(x => Message(x)).ToArray()));
    }

    [Fact]
    public void Historical_backfill_cannot_become_a_notification_trigger()
    {
        var batch = FrozenGroupBatch.Create(Guid.NewGuid(), Scope, 1, GroupCoverage.Complete, true, [Message()]);
        var committed = GroupNotesCommitted.Create(Guid.NewGuid(), batch, [new(Guid.NewGuid(), 1, "REQ-1")]);
        Assert.False(batch.MayNotify);
        Assert.False(committed.MayNotify);
    }

    [Fact]
    public void Model_proposal_has_no_committed_due_date_assignment_resolution_or_destination()
    {
        var batch = Batch();
        var proposal = Proposal(batch);
        proposal.Validate(batch);
        Assert.Equal(GroupNoteBusinessStatus.New, proposal.GetInitialBusinessStatus());
        Assert.Equal("tomorrow", proposal.RequestedDeadlineText);
        var forbidden = new[] { "TenantId", "CompanyId", "SourceBindingId", "Destination", "Assignee", "CommittedDueAt", "Status" };
        Assert.DoesNotContain(typeof(GroupNoteProposal).GetProperties(), x => forbidden.Contains(x.Name));
    }

    [Theory]
    [InlineData("Destination")]
    [InlineData("TenantId")]
    [InlineData("Status")]
    [InlineData("CommittedDueAt")]
    public void Model_json_cannot_add_trusted_fields(string field)
    {
        var json = JsonSerializer.Serialize(Proposal(Batch()));
        var hostile = json[..^1] + ",\"" + field + "\":\"untrusted\"}";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<GroupNoteProposal>(hostile));
    }

    [Fact]
    public void Evidence_requires_exact_frozen_message_revision_and_quotes()
    {
        var batch = Batch();
        var proposal = Proposal(batch);
        Assert.Throws<InvalidOperationException>(() => (proposal with { Evidence = [proposal.Evidence[0] with { MessageId = Guid.NewGuid() }] }).Validate(batch));
        Assert.Throws<InvalidOperationException>(() => (proposal with { Evidence = [proposal.Evidence[0] with { Revision = 3 }] }).Validate(batch));
        Assert.Throws<InvalidOperationException>(() => (proposal with { Evidence = [proposal.Evidence[0], proposal.Evidence[0]] }).Validate(batch));
        Assert.Throws<InvalidOperationException>(() => (proposal with { Evidence = [proposal.Evidence[0] with { Quote = null }] }).Validate(batch));
        Assert.Throws<InvalidOperationException>(() => (proposal with { Kind = GroupNoteKind.ExtractionFailed }).Validate(batch));
    }

    [Fact]
    public void Unsupported_media_attention_and_missing_information_can_be_recorded_without_approval()
    {
        var batch = Batch();
        var attention = Proposal(batch) with
        {
            Kind = GroupNoteKind.NeedsClarification,
            MissingFields = ["Image contents need IT review"],
            Evidence = [new(batch.Messages[0].MessageId, 2, null)]
        };
        attention.Validate(batch);
        Assert.Equal(GroupNoteBusinessStatus.NeedsClarification, attention.GetInitialBusinessStatus());
        Assert.Throws<InvalidOperationException>(() => (attention with { MissingFields = [] }).Validate(batch));
    }

    [Fact]
    public void Notes_committed_contains_only_scoped_durable_refs_and_copies_collection()
    {
        var batch = Batch();
        var note = new GroupCommittedNoteReference(Guid.NewGuid(), 3, "REQ-2026-1");
        var notes = new[] { note };
        var committed = GroupNotesCommitted.Create(Guid.NewGuid(), batch, notes);
        notes[0] = note with { Revision = 4 };
        Assert.Equal(batch.Scope, committed.Scope);
        Assert.Equal(batch.Id, committed.BatchId);
        Assert.Equal(note, Assert.Single(committed.Notes));
        Assert.DoesNotContain(typeof(GroupNotesCommitted).GetProperties(), x => x.Name is "Content" or "MessageText" or "Approval");
        Assert.Throws<InvalidOperationException>(() => GroupNotesCommitted.Create(Guid.NewGuid(), batch, [note, note]));
    }

    [Theory]
    [InlineData(GroupReportSendState.Pending, true)]
    [InlineData(GroupReportSendState.Dispatching, false)]
    [InlineData(GroupReportSendState.Accepted, false)]
    [InlineData(GroupReportSendState.Failed, false)]
    [InlineData(GroupReportSendState.Unknown, false)]
    [InlineData(GroupReportSendState.Blocked, false)]
    public void Duplicate_delivery_and_possibly_accepted_send_do_not_authorize_a_new_attempt(GroupReportSendState state, bool expected)
    {
        Assert.Equal(expected, GroupReportSendPolicy.CanClaim(state));
    }

    [Fact]
    public void Persisted_commit_times_support_quiet_trigger_restart_and_continuous_input_without_starvation()
    {
        var first = new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);
        var timing = GroupBatchTiming.InitialTuning;
        Assert.Equal(first.AddSeconds(30), timing.ComputeDue(first, first));
        Assert.Equal(first.AddSeconds(40), timing.ComputeDue(first, first.AddSeconds(10)));
        Assert.Equal(first.AddSeconds(120), timing.ComputeDue(first, first.AddSeconds(119)));
        Assert.Equal(first.AddSeconds(120), timing.ComputeDue(first, first.AddDays(1)));
        var restored = new GroupBatchTiming(timing.QuietPeriod, timing.MaximumWait);
        Assert.Equal(timing.ComputeDue(first, first.AddSeconds(20)), restored.ComputeDue(first, first.AddSeconds(20)));
    }

    [Fact]
    public void Invalid_or_non_utc_checkpoint_times_do_not_schedule_a_batch()
    {
        var first = new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);
        Assert.Throws<InvalidOperationException>(() => GroupBatchTiming.InitialTuning.ComputeDue(first, first.AddSeconds(-1)));
        Assert.Throws<InvalidOperationException>(() => GroupBatchTiming.InitialTuning.ComputeDue(first.ToOffset(TimeSpan.FromHours(7)), first));
        Assert.Throws<InvalidOperationException>(() => new GroupBatchTiming(TimeSpan.Zero, TimeSpan.FromSeconds(1)).ComputeDue(first, first));
        Assert.Throws<InvalidOperationException>(() => new GroupBatchTiming(TimeSpan.FromSeconds(31), TimeSpan.FromSeconds(30)).ComputeDue(first, first));
        Assert.Throws<InvalidOperationException>(() => new GroupBatchTiming(TimeSpan.FromSeconds(30), TimeSpan.FromDays(2)).ComputeDue(first, first));
        Assert.Throws<InvalidOperationException>(() => GroupBatchTiming.InitialTuning.ComputeDue(DateTimeOffset.MaxValue, DateTimeOffset.MaxValue));
    }
}
