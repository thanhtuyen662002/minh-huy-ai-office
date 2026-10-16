using System.Text.Json.Nodes;
using Xunit;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class GroupBatchSourceReaderTests
{
    [Fact]
    public async Task ReviewerNeedsClarificationRequiresAtLeastOneMissingField()
    {
        using var f = new Fixture(); var message = await f.CommitAsync(f.Payload(text: "Lỗi nhập kho, chưa rõ mã chứng từ"));
        var prep = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(await f.ClaimAsync(), [message.MessageId]));
        var wire = ProposalWire(Assert.Single(prep.Candidates)); wire["notes"]![0]!["type"] = "needs_clarification";
        Assert.Throws<InvalidOperationException>(() => GroupGroundedWorkProposal.Parse(prep, wire.ToJsonString()));
        wire["notes"]![0]!["missing_fields"] = new JsonArray("Mã chứng từ");
        Assert.Equal(GroupWorkProposalKind.NeedsClarification, Assert.Single(GroupGroundedWorkProposal.Parse(prep, wire.ToJsonString()).Notes).Kind);
    }
    [Fact]
    public async Task ReviewerDecodedEvidenceAndDispositionShapesCannotOverrideCanonicalEarlierValues()
    {
        using var f = new Fixture(); var message = await f.CommitAsync(f.Payload(text: "Tra cứu tồn kho"));
        var prep = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(await f.ClaimAsync(), [message.MessageId]));
        var wire = ProposalWire(Assert.Single(prep.Candidates)).ToJsonString();
        foreach (var d in new[] { wire.Replace("\"revision\":", "\"revi\\u0073ion\":99999,\"revision\":"), wire.Replace("\"disposition\":", "\"disposi\\u0074ion\":\"no_work\",\"disposition\":") })
        {
            var e = Assert.Throws<InvalidOperationException>(() => GroupGroundedWorkProposal.Parse(prep, d));
            Assert.Equal("Group work proposal is not available.", e.Message); Assert.Null(e.InnerException);
        }
    }
    [Fact]
    public async Task ReviewerRawScalarFailureIsFixedAndDecodedJsonAssignmentStillDenied()
    {
        using var f = new Fixture(); var message = await f.CommitAsync(f.Payload(text: "Tra cứu tồn kho"));
        var prep = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(await f.ClaimAsync(), [message.MessageId]));
        foreach (var field in new[] { "title", "problem", "outcome", "suggested_relation" })
        {
            var wire = ProposalWire(Assert.Single(prep.Candidates)); wire["notes"]![0]![field] = "password=owned-private-marker";
            var e = Assert.Throws<InvalidOperationException>(() => GroupGroundedWorkProposal.Parse(prep, wire.ToJsonString()));
            Assert.Equal("Group work proposal is not available.", e.Message); Assert.Null(e.InnerException);
        }
        var json = ProposalWire(Assert.Single(prep.Candidates)).ToJsonString();
        var raw = json.Replace("\"title\":\"", "\"title\":\"" + new string((char)0xD800, 1)); Assert.NotEqual(json, raw);
        Assert.Throws<InvalidOperationException>(() => GroupGroundedWorkProposal.Parse(prep, raw));
    }
    [Fact]
    public async Task ReviewerDeadlineIsExactSourceEvidenceAndNeverNewItAuthority()
    {
        using var f = new Fixture(); var message = await f.CommitAsync(f.Payload(text: "Khách đề nghị ngày mai, chưa có IT xác nhận."));
        var prep = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(await f.ClaimAsync(), [message.MessageId]));
        var wire = ProposalWire(Assert.Single(prep.Candidates)); wire["notes"]![0]!["requested_deadline_text"] = "ngày mai";
        var note = Assert.Single(GroupGroundedWorkProposal.Parse(prep, wire.ToJsonString()).Notes);
        Assert.Equal("ngày mai", note.CustomerRequestedDeadlineText); Assert.Equal(message.MessageId, note.DeadlineEvidence!.MessageId);
        Assert.DoesNotContain(note.GetType().GetProperties(), x => x.Name is "CommittedDueAt" or "ResolvedAt" or "Status");
        wire["notes"]![0]!["requested_deadline_text"] = "ngày kia"; Assert.Throws<InvalidOperationException>(() => GroupGroundedWorkProposal.Parse(prep, wire.ToJsonString()));
    }
    [Fact]
    public async Task ReviewerHostScopedPreparationCannotAcceptAnotherAuthorizedSourceProposal()
    {
        using var first = new Fixture(); using var second = new Fixture();
        var m1 = await first.CommitAsync(fixtureText()); var m2 = await second.CommitAsync(second.Payload(text: "Tra cứu tồn kho"));
        var prep1 = GroupBatchSourcePreparation.Create(await first.Reader.ReadAsync(await first.ClaimAsync(), [m1.MessageId]));
        var prep2 = GroupBatchSourcePreparation.Create(await second.Reader.ReadAsync(await second.ClaimAsync(), [m2.MessageId]));
        var wire = ProposalWire(Assert.Single(prep2.Candidates));
        Assert.Throws<InvalidOperationException>(() => GroupGroundedWorkProposal.Parse(prep1, wire.ToJsonString()));
        Assert.Single(GroupGroundedWorkProposal.Parse(prep2, wire.ToJsonString()).Notes);
        MinhHuy.AIOffice.Platform.Persistence.GroupIngressPayload fixtureText() => first.Payload(text: "Tra cứu tồn kho");
    }
}
