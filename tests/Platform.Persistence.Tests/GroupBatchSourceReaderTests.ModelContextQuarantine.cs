extern alias GroupReferenceProof;

using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using MinhHuyAiOffice.Shared.Contracts;
using Xunit;
using ContextProof = GroupReferenceProof::MinhHuy.AIOffice.GroupReference.RuntimeProof.GroupAutomaticModelContextRuntimeProof;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class GroupBatchSourceReaderTests
{
    private static async Task<(GroupBatchSourcePreparation Preparation, GroupBrainPrivateContext Brain)> MixedHistoryAsync(Fixture f)
    {
        var readable = await f.CommitAsync(f.Payload("readable", "readable-event", "Tra cứu tồn kho 😀\uFEFF ", GroupSourceEventKind.Media));
        var quarantined = await f.CommitAsync(f.Payload("quarantined", "quarantined-event", "password=OWNED_AUTOMATIC_PRIVATE_SENTINEL", GroupSourceEventKind.Media));
        var claim = await f.ClaimAsync();
        var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(claim, [readable.MessageId, quarantined.MessageId]));
        var source = Assert.Single(preparation.Candidates);
        Assert.Single(preparation.Receipts, x => x.Disposition == GroupSourcePreparationDisposition.Quarantined);
        var note = GroupGroundedWorkProposal.Parse(preparation, ProposalWire(source).ToJsonString()).Notes[0];
        var ids = new List<Guid>();
        for (var index = 0; index < 2; index++)
            ids.Add((await SeedBrainAsync(f, claim, readable, GroupBrainPayloadCodec.EncodeAiNote(note))).Request.Id);
        foreach (var host in GroupHostAttentionPlan.Create(preparation).Notes)
        {
            var seed = await SeedBrainAsync(f, claim, readable, host.EncodePayload());
            seed.Request.Kind = GroupNoteKind.NeedsClarification;
            seed.Request.BusinessStatus = GroupNoteBusinessStatus.NeedsClarification;
            seed.Revision.Origin = GroupRequestRevisionOrigin.HostAttention;
            seed.Revision.VerificationLevel = GroupRequestVerificationLevel.HostObserved;
            f.Auth.Db.GroupRequestEvidence.RemoveRange(await f.Auth.Db.GroupRequestEvidence.Where(x => x.RequestId == seed.Request.Id).ToArrayAsync());
            for (var index = 0; index < host.SourceReferences.Count; index++)
            {
                var reference = host.SourceReferences[index];
                f.Auth.Db.GroupRequestEvidence.Add(new()
                {
                    TenantId = f.Auth.Scope.TenantId,
                    CompanyId = f.Auth.Scope.CompanyId,
                    BindingId = f.Auth.Scope.SourceBindingId,
                    RequestId = seed.Request.Id,
                    RequestRevision = 1,
                    Ordinal = index + 1,
                    MessageId = reference.MessageId,
                    MessageRevision = reference.Revision,
                    Kind = GroupRequestEvidenceKind.HostMetadataAttention
                });
            }
            await f.Auth.Db.SaveChangesAsync();
            ids.Add(seed.Request.Id);
        }
        Assert.Equal(4, ids.Count);
        return (preparation, await BrainReader(f).ReadAsync(claim, ids, []));
    }

    [Fact]
    public async Task ActualMixedHistoryKeepsOnlyReadableCurrentTextAndClosedQuarantineMetadata()
    {
        using var f = new Fixture();
        var (preparation, brain) = await MixedHistoryAsync(f);
        var context = GroupAutomaticModelContext.Create(preparation, brain, "owned-synthetic-context", 64000);
        var id = Guid.NewGuid(); var request = context.CreateGatewayRequest(id);
        using var wire = JsonDocument.Parse(request.Input);
        var current = Assert.Single(wire.RootElement.GetProperty("current_sources").EnumerateArray());
        var quarantine = Assert.Single(preparation.Receipts, x => x.Disposition == GroupSourcePreparationDisposition.Quarantined);
        Assert.NotEqual(quarantine.MessageId.ToString("D"), current.GetProperty("message_id").GetString());
        Assert.DoesNotContain("OWNED_AUTOMATIC_PRIVATE_SENTINEL", request.Input);
        var history = wire.RootElement.GetProperty("same_group_history").EnumerateArray().ToArray();
        Assert.Equal(2, history.Count(x => x.GetProperty("role").GetString() == "source_backed_ai_interpretation"));
        var host = history.Where(x => x.GetProperty("role").GetString() == "host_metadata_attention").ToArray();
        Assert.Equal(2, host.Length);
        foreach (var item in host)
        {
            var payload = item.GetProperty("content");
            Assert.False(payload.TryGetProperty("quote", out _));
            Assert.False(payload.TryGetProperty("text", out _));
            Assert.False(payload.TryGetProperty("evidence", out _));
            Assert.Contains(payload.GetProperty("source_refs").EnumerateArray(), x => x.GetProperty("message_id").GetString() == quarantine.MessageId.ToString("D"));
        }
        // The original whole-input predicate rejects this valid metadata, before SQL.
        Assert.Contains(quarantine.MessageId.ToString("D"), request.Input);
        ContextProof.RequirePrivateInput(context, preparation, brain, f.Auth.Scope, id, request);
    }

    [Fact]
    public async Task ActualEmptyHistoryRetainsTheOriginalPrivateInputChecks()
    {
        using var f = new Fixture();
        var readable = await f.CommitAsync(f.Payload(text: "Tra cứu tồn kho 😀\uFEFF ", kind: GroupSourceEventKind.Media));
        var quarantined = await f.CommitAsync(f.Payload("quarantined", "quarantined-event", "password=OWNED_AUTOMATIC_PRIVATE_SENTINEL", GroupSourceEventKind.Media));
        var claim = await f.ClaimAsync();
        var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(claim, [readable.MessageId, quarantined.MessageId]));
        var brain = await BrainReader(f).ReadAsync(claim, [], []);
        var context = GroupAutomaticModelContext.Create(preparation, brain, "owned-synthetic-context", 64000);
        var id = Guid.NewGuid();
        ContextProof.RequirePrivateInput(context, preparation, brain, f.Auth.Scope, id, context.CreateGatewayRequest(id));
    }

    [Theory]
    [InlineData("quarantined-current-id")]
    [InlineData("private-current-text")]
    [InlineData("private-history-text")]
    [InlineData("tenant-in-input")]
    [InlineData("company-in-input")]
    [InlineData("binding-in-input")]
    [InlineData("wrong-tenant")]
    [InlineData("wrong-company")]
    [InlineData("wrong-task")]
    [InlineData("wrong-request")]
    [InlineData("history-authority")]
    [InlineData("history-role")]
    public async Task ActualPrivateInputGuardStillRejectsQuarantineSecretsScopeIdentityAndHistoryAuthority(string attack)
    {
        using var f = new Fixture();
        var (preparation, brain) = await MixedHistoryAsync(f);
        var context = GroupAutomaticModelContext.Create(preparation, brain, "owned-synthetic-context", 64000);
        var id = Guid.NewGuid(); var request = context.CreateGatewayRequest(id);
        var wire = JsonNode.Parse(request.Input)!;
        switch (attack)
        {
            case "quarantined-current-id":
                wire["current_sources"]![0]!["message_id"] = preparation.Receipts.Single(x => x.Disposition == GroupSourcePreparationDisposition.Quarantined).MessageId.ToString("D"); break;
            case "private-current-text": wire["current_sources"]![0]!["text"] = "OWNED_AUTOMATIC_PRIVATE_SENTINEL"; break;
            case "private-history-text": wire["same_group_history"]![0]!["content"]!["title"] = "OWNED_AUTOMATIC_PRIVATE_SENTINEL"; break;
            case "tenant-in-input": wire["policy"] = f.Auth.Scope.TenantId.ToString("D"); break;
            case "company-in-input": wire["policy"] = f.Auth.Scope.CompanyId.ToString("D"); break;
            case "binding-in-input": wire["policy"] = f.Auth.Scope.SourceBindingId.ToString("D"); break;
            case "wrong-tenant": request = request with { TenantId = Guid.NewGuid().ToString("D") }; break;
            case "wrong-company": request = request with { CompanyId = Guid.NewGuid().ToString("D") }; break;
            case "wrong-task": request = request with { TaskId = Guid.NewGuid().ToString("D") }; break;
            case "wrong-request": request = request with { RequestId = Guid.NewGuid().ToString("D") }; break;
            case "history-authority": wire["same_group_history"]![0]!["it_confirmed"] = true; break;
            case "history-role": wire["same_group_history"]![0]!["role"] = "it_confirmed_stored_revision"; break;
        }
        var json = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var policy = wire["policy"]!.GetValue<string>();
        var adjustment = System.Text.Encoding.UTF8.GetByteCount(request.Input) - System.Text.Encoding.UTF8.GetByteCount(wire.ToJsonString(json));
        wire["policy"] = adjustment >= 0 ? policy + new string(' ', adjustment) : policy[..(policy.Length + adjustment)];
        request = request with { Input = wire.ToJsonString(json) };
        // Each attack reaches the retained guard, without failing byte accounting first.
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(context.CreateGatewayRequest(id).Input), System.Text.Encoding.UTF8.GetByteCount(request.Input));
        Assert.Throws<InvalidOperationException>(() => ContextProof.RequirePrivateInput(context, preparation, brain, f.Auth.Scope, id, request));
    }
}
