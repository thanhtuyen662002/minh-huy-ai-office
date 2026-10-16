using System.Text;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class GroupBatchSourceReaderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AttemptResultKeepsOriginalPlanAttemptOutputAndProviderObservationWithoutEffects(bool work)
    {
        using var f = new Fixture(); var (plan, chunks, permit, response) = await ResultInputAsync(f, work);
        var before = await f.CountsAsync(); var keys = f.Keys.Reads;
        var result = GroupModelAttemptResult.Create(response, chunks[0]); var bytes = result.Encode();
        var restored = GroupModelAttemptResult.Read(bytes, plan, chunks[0], permit.Start, f.Auth.Clock.Current);
        Assert.Equal(bytes, restored.Encode()); Assert.Equal(response.Observed, restored.Observed);
        Assert.Equal(response.Proposal!.EncodeValidatedOutput(), restored.Proposal.EncodeValidatedOutput());
        Assert.Same(chunks[0].Current.Preparation, restored.Proposal.Preparation);
        Assert.Equal(work ? 1 : 0, restored.Proposal.Notes.Count);
        Assert.Single(restored.Proposal.SourceDispositions);
        bytes[0] ^= 1; Assert.NotEqual(bytes, result.Encode());
        Assert.Equal(before, await f.CountsAsync()); Assert.Equal(keys, f.Keys.Reads);
        Assert.False(f.Auth.Db.ChangeTracker.HasChanges()); Assert.Null(f.Auth.Db.Database.CurrentTransaction);
        Assert.Empty(await f.Auth.Db.GroupModelAttemptStarts.ToArrayAsync());
        Assert.Empty(await f.Auth.Db.GroupWorkCommitReceipts.ToArrayAsync());
        Assert.DoesNotContain("Tồn kho", result.ToString());
    }

    [Theory]
    [InlineData("magic")]
    [InlineData("schema")]
    [InlineData("plan")]
    [InlineData("chunk")]
    [InlineData("operation")]
    [InlineData("input")]
    [InlineData("attempt")]
    [InlineData("provider-utf8")]
    [InlineData("model-utf8")]
    [InlineData("negative-input-tokens")]
    [InlineData("output-token-limit")]
    [InlineData("observation-deadline")]
    [InlineData("observation-future")]
    [InlineData("output-length")]
    [InlineData("output-utf8")]
    [InlineData("truncated")]
    [InlineData("trailing")]
    [InlineData("oversized")]
    [InlineData("foreign-evidence")]
    [InlineData("quarantine")]
    public async Task AttemptResultRefusesSplicedMalformedOrQuarantinedRecoveryBytes(string fault)
    {
        using var f = new Fixture(); var (plan, chunks, permit, response) = await ResultInputAsync(f, true);
        var bytes = GroupModelAttemptResult.Create(response, chunks[0]).Encode();
        var provider = 155;
        var model = provider + BitConverter.ToUInt16(bytes, 153) + 2;
        var inputTokens = model + BitConverter.ToUInt16(bytes, model - 2);
        var outputLength = inputTokens + 24;
        var output = outputLength + 4;
        switch (fault)
        {
            case "magic": bytes[0] ^= 1; break;
            case "schema": bytes[8] ^= 1; break;
            case "plan": bytes[40] ^= 1; break;
            case "chunk": bytes[72] = 2; break;
            case "operation": bytes[73] ^= 1; break;
            case "input": bytes[89] ^= 1; break;
            case "attempt": bytes[121] ^= 1; break;
            case "provider-utf8": bytes[provider] = 255; break;
            case "model-utf8": bytes[model] = 255; break;
            case "negative-input-tokens": BitConverter.GetBytes(-1L).CopyTo(bytes, inputTokens); break;
            case "output-token-limit": BitConverter.GetBytes(32769L).CopyTo(bytes, inputTokens + 8); break;
            case "observation-deadline": BitConverter.GetBytes(permit.Start.DeadlineUtc.UtcTicks).CopyTo(bytes, inputTokens + 16); break;
            case "observation-future": BitConverter.GetBytes(f.Auth.Clock.Current.AddTicks(1).UtcTicks).CopyTo(bytes, inputTokens + 16); break;
            case "output-length": BitConverter.GetBytes(64001).CopyTo(bytes, outputLength); break;
            case "output-utf8": bytes[output] = 255; break;
            case "truncated": bytes = bytes[..^1]; break;
            case "trailing": bytes = bytes.Concat(new byte[] { 0 }).ToArray(); break;
            case "oversized": bytes = new byte[GroupModelAttemptResult.MaximumBytes + 1]; break;
            case "foreign-evidence":
                var original = chunks[0].Current.Preparation.Candidates[0].MessageId.ToString("D");
                var bad = response.Proposal!.EncodeValidatedOutput();
                bad = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bad).Replace(original, Guid.NewGuid().ToString("D"), StringComparison.Ordinal));
                bad.CopyTo(bytes, output); break;
            case "quarantine":
                var unsafeOutput = Encoding.UTF8.GetBytes("{\"password\":\"PRIVATE_RESULT_SENTINEL_7523\"}");
                BitConverter.GetBytes(unsafeOutput.Length).CopyTo(bytes, outputLength);
                bytes = bytes[..output].Concat(unsafeOutput).ToArray(); break;
        }
        var before = await f.CountsAsync(); var keys = f.Keys.Reads;
        var error = Assert.Throws<InvalidOperationException>(() => GroupModelAttemptResult.Read(bytes, plan, chunks[0], permit.Start, f.Auth.Clock.Current));
        Assert.Equal("Group original attempt result is not available.", error.Message); Assert.Null(error.InnerException);
        Assert.DoesNotContain("PRIVATE_RESULT_SENTINEL", error.ToString());
        Assert.Equal(before, await f.CountsAsync()); Assert.Equal(keys, f.Keys.Reads);
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("batch")]
    [InlineData("input")]
    [InlineData("ordinal")]
    [InlineData("request")]
    [InlineData("predecessor")]
    [InlineData("claim-operation")]
    [InlineData("claim-epoch")]
    [InlineData("start")]
    [InlineData("deadline")]
    public async Task AttemptResultBindsEveryOriginalJournalFieldAndNeverRebindsANonce(string fault)
    {
        using var f = new Fixture(); var (plan, chunks, permit, response) = await ResultInputAsync(f, false);
        var bytes = GroupModelAttemptResult.Create(response, chunks[0]).Encode(); var start = permit.Start;
        var changed = fault switch
        {
            "tenant" => start with { Scope = start.Scope with { TenantId = Guid.NewGuid() } },
            "batch" => start with { BatchId = Guid.NewGuid() },
            "input" => start with { InputSha256 = new string('B', 64) },
            "ordinal" => start with { Ordinal = 2, PredecessorRequestId = Guid.NewGuid() },
            "request" => start with { RequestId = Guid.NewGuid() },
            "predecessor" => start with { PredecessorRequestId = Guid.NewGuid() },
            "claim-operation" => start with { ClaimOperationId = Guid.NewGuid() },
            "claim-epoch" => start with { ClaimEpoch = start.ClaimEpoch + 1 },
            "start" => start with { StartedAtUtc = start.StartedAtUtc.AddTicks(1) },
            "deadline" => start with { DeadlineUtc = start.DeadlineUtc.AddTicks(-1) },
            _ => throw new InvalidOperationException()
        };
        Assert.Throws<InvalidOperationException>(() => GroupModelAttemptResult.Read(bytes, plan, chunks[0], changed, f.Auth.Clock.Current));
        Assert.Throws<InvalidOperationException>(() => GroupModelAttemptResult.Read(bytes, plan, chunks[1], start, f.Auth.Clock.Current));
    }

    [Fact]
    public async Task AttemptResultRecoveryAfterProviderDeadlineIsStructuralAndCannotIssueANewPermit()
    {
        using var f = new Fixture(); var (plan, chunks, permit, response) = await ResultInputAsync(f, false);
        var bytes = GroupModelAttemptResult.Create(response, chunks[0]).Encode();
        var later = permit.Start.DeadlineUtc.AddMinutes(1);
        var result = GroupModelAttemptResult.Read(bytes, plan, chunks[0], permit.Start, later);
        Assert.Empty(result.Proposal.Notes); Assert.Equal(response.Observed, result.Observed);
        var expired = GroupModelAttemptResponse.Parse(permit, NoWorkResponse(permit), new ResponseSequenceClock([later]));
        Assert.Equal(GroupModelAttemptResponseDisposition.Expired, expired.Disposition); Assert.Null(expired.Observed);
        Assert.Throws<InvalidOperationException>(() => GroupModelAttemptResult.Create(expired, chunks[0]));
        Assert.Empty(await f.Auth.Db.GroupModelAttemptStarts.ToArrayAsync());
        Assert.Empty(await f.Auth.Db.GroupNotesCommittedOutbox.ToArrayAsync());
    }

    [Fact]
    public async Task AttemptResultRefusesLegacyUnboundResponseNullCancellationAndNonUtcObservationTime()
    {
        using var f = new Fixture(); var (plan, chunks, permit, response) = await ResultInputAsync(f, false);
        var legacy = new GroupModelAttemptPermit(permit.Context, permit.Start);
        var legacyResponse = GroupModelAttemptResponse.Parse(legacy, NoWorkResponse(legacy), f.Auth.Clock);
        Assert.Throws<InvalidOperationException>(() => GroupModelAttemptResult.Create(legacyResponse, chunks[0]));
        var bytes = GroupModelAttemptResult.Create(response, chunks[0]).Encode();
        Assert.Throws<InvalidOperationException>(() => GroupModelAttemptResult.Read(bytes, plan, chunks[0], permit.Start, f.Auth.Clock.Current.ToOffset(TimeSpan.FromHours(7))));
        Assert.Throws<InvalidOperationException>(() => GroupModelAttemptResult.Read(null, plan, chunks[0], permit.Start, f.Auth.Clock.Current));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => GroupModelAttemptResult.Create(response, chunks[0], canceled.Token));
        Assert.Throws<OperationCanceledException>(() => GroupModelAttemptResult.Read(bytes, plan, chunks[0], permit.Start, f.Auth.Clock.Current, canceled.Token));
        Assert.Throws<ArgumentNullException>(() => GroupModelAttemptResult.Create(null!, chunks[0]));
        Assert.Throws<ArgumentNullException>(() => GroupModelAttemptResult.Read(bytes, null!, chunks[0], permit.Start, f.Auth.Clock.Current));
    }

    private static async Task<(GroupAutomaticInputPlan Plan, IReadOnlyList<GroupAutomaticInputPlan.RestoredChunk> Chunks,
        GroupModelAttemptPermit Permit, GroupModelAttemptResponse Response)> ResultInputAsync(Fixture f, bool work)
    {
        var (plan, chunks) = await OriginalAttemptInputAsync(f);
        var binding = GroupModelAttemptOriginalBinding.Create(plan, chunks[0]);
        var synthetic = JournalGuardPermit(binding.Context, f.Auth.Clock.Current);
        var permit = new GroupModelAttemptPermit(binding.Context, synthetic.Start, binding);
        var wire = NoWorkResponse(permit);
        if (work) wire = wire with { Output = ProposalWire(Assert.Single(binding.Context.Preparation.Candidates)).ToJsonString() };
        var response = GroupModelAttemptResponse.Parse(permit, wire, f.Auth.Clock);
        Assert.Equal(GroupModelAttemptResponseDisposition.Parsed, response.Disposition); Assert.NotNull(response.Observed);
        return (plan, chunks, permit, response);
    }
}
