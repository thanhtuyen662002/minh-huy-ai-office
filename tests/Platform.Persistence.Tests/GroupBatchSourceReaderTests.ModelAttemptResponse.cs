using System.Text.Json;
using MinhHuyAiOffice.Shared.Contracts;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class GroupBatchSourceReaderTests
{
    private static AiGatewayResponse NoWorkResponse(GroupModelAttemptPermit permit) => new(
        permit.Start.RequestId.ToString("D"), AiCapability.StructuredGeneration,
        JsonSerializer.Serialize(new
        {
            version = GroupGroundedWorkProposal.FormatVersion,
            notes = Array.Empty<object>(),
            source_dispositions = permit.Context.Preparation.Candidates.Select(x => new
            { message_id = x.MessageId.ToString("D"), revision = x.Revision, disposition = "no_work" })
        }), "configured-primary-or-backup-model", 50, 20)
    { ProviderId = "configured-primary-or-backup" };

    [Fact]
    public async Task AttemptResponseBindsOriginalNonceAndClosedCompleteProposalWithoutEffectsOrModelQualification()
    {
        using var f = new Fixture(); var context = await FenceInputAsync(f);
        var permit = JournalGuardPermit(context, f.Auth.Clock.GetUtcNow());
        var before = await f.CountsAsync(); var keys = f.Keys.Reads;
        var result = GroupModelAttemptResponse.Parse(permit, NoWorkResponse(permit), f.Auth.Clock);
        Assert.Equal(GroupModelAttemptResponseDisposition.Parsed, result.Disposition);
        Assert.Same(permit, result.Permit); Assert.Same(context.Preparation, result.Proposal!.Preparation);
        Assert.Equal(context.Preparation.Candidates.Count, result.Proposal.SourceDispositions.Count);
        Assert.Empty(result.Proposal.Notes); Assert.True(context.RequiresModelTokenizerQualification);
        Assert.DoesNotContain(NoWorkResponse(permit).Output, result.ToString());
        Assert.Equal(before, await f.CountsAsync()); Assert.Equal(keys, f.Keys.Reads);
        Assert.False(f.Auth.Db.ChangeTracker.HasChanges()); Assert.Null(f.Auth.Db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData("request")]
    [InlineData("capability")]
    [InlineData("null")]
    [InlineData("negative-input")]
    [InlineData("negative-output")]
    [InlineData("output-ceiling")]
    [InlineData("model-whitespace")]
    [InlineData("model-control")]
    [InlineData("model-surrogate")]
    [InlineData("model-bytes")]
    [InlineData("provider-null")]
    [InlineData("provider-whitespace")]
    [InlineData("provider-control")]
    [InlineData("provider-surrogate")]
    [InlineData("provider-bytes")]
    [InlineData("incomplete-dispositions")]
    [InlineData("foreign-source")]
    [InlineData("quarantine")]
    [InlineData("malformed-json")]
    [InlineData("oversized-json")]
    public async Task AttemptResponseRefusesMalformedForeignOrQuarantinedOutputWithoutPrivateErrors(string fault)
    {
        using var f = new Fixture(); var context = await FenceInputAsync(f);
        var permit = JournalGuardPermit(context, f.Auth.Clock.GetUtcNow());
        var good = NoWorkResponse(permit);
        var bad = fault switch
        {
            "request" => good with { RequestId = Guid.NewGuid().ToString("D") },
            "capability" => good with { Capability = AiCapability.Reasoning },
            "null" => null,
            "negative-input" => good with { InputTokens = -1 },
            "negative-output" => good with { OutputTokens = -1 },
            "output-ceiling" => good with { OutputTokens = 32769 },
            "model-whitespace" => good with { Model = " padded " },
            "model-control" => good with { Model = "unsafe\nmodel" },
            "model-surrogate" => good with { Model = "\uD800" },
            "model-bytes" => good with { Model = new string('ệ', 86) },
            "provider-null" => good with { ProviderId = null },
            "provider-whitespace" => good with { ProviderId = " padded " },
            "provider-control" => good with { ProviderId = "unsafe\nprovider" },
            "provider-surrogate" => good with { ProviderId = "\uD800" },
            "provider-bytes" => good with { ProviderId = new string('ệ', 43) },
            "incomplete-dispositions" => good with { Output = "{\"version\":\"group-work-proposal-v1\",\"notes\":[],\"source_dispositions\":[]}" },
            "foreign-source" => good with { Output = good.Output.Replace(context.Preparation.Candidates[0].MessageId.ToString("D"), Guid.NewGuid().ToString("D"), StringComparison.Ordinal) },
            "quarantine" => good with { Output = "{\"password\":\"PRIVATE_SECRET_VALUE_7258\"}" },
            "malformed-json" => good with { Output = "PRIVATE_INVALID_OUTPUT_7242" },
            "oversized-json" => good with { Output = new string('X', 64001) },
            _ => throw new InvalidOperationException()
        };
        var before = await f.CountsAsync(); var reads = f.Keys.Reads;
        var result = GroupModelAttemptResponse.Parse(permit, bad, f.Auth.Clock);
        Assert.Equal(GroupModelAttemptResponseDisposition.InvalidOutput, result.Disposition); Assert.Null(result.Proposal);
        Assert.Equal("Group model attempt response (private structural result).", result.ToString());
        Assert.Equal(before, await f.CountsAsync()); Assert.Equal(reads, f.Keys.Reads);
        Assert.False(f.Auth.Db.ChangeTracker.HasChanges()); Assert.Null(f.Auth.Db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("batch")]
    [InlineData("input")]
    [InlineData("nonce")]
    [InlineData("ordinal")]
    [InlineData("predecessor")]
    [InlineData("claim-operation")]
    [InlineData("claim-epoch")]
    [InlineData("start-before-claim")]
    [InlineData("deadline-after-claim")]
    [InlineData("nonpositive-lifetime")]
    [InlineData("too-long-lifetime")]
    public async Task AttemptResponseCannotRebindPermitOrMisclassifyLocalAuthorityFailureAsProviderOutput(string fault)
    {
        using var f = new Fixture(); var context = await FenceInputAsync(f);
        var original = JournalGuardPermit(context, f.Auth.Clock.GetUtcNow());
        var claim = context.Preparation.Context.Handle.Receipt; var start = original.Start;
        var changed = fault switch
        {
            "scope" => start with { Scope = start.Scope with { TenantId = Guid.NewGuid() } },
            "batch" => start with { BatchId = Guid.NewGuid() },
            "input" => start with { InputSha256 = new string('A', 64) },
            "nonce" => start with { RequestId = Guid.Empty },
            "ordinal" => start with { Ordinal = 4 },
            "predecessor" => start with { PredecessorRequestId = Guid.NewGuid() },
            "claim-operation" => start with { ClaimOperationId = Guid.NewGuid() },
            "claim-epoch" => start with { ClaimEpoch = start.ClaimEpoch + 1 },
            "start-before-claim" => start with { StartedAtUtc = claim.IssuedAtUtc.AddTicks(-1) },
            "deadline-after-claim" => start with { DeadlineUtc = claim.ExpiresAtUtc.AddTicks(1) },
            "nonpositive-lifetime" => start with { DeadlineUtc = start.StartedAtUtc },
            "too-long-lifetime" => start with { DeadlineUtc = start.StartedAtUtc.Add(GroupModelAttemptBudget.MaximumAttemptLifetime).AddTicks(1) },
            _ => throw new InvalidOperationException()
        };
        var before = await f.CountsAsync(); var reads = f.Keys.Reads;
        var error = Assert.Throws<InvalidOperationException>(() => GroupModelAttemptResponse.Parse(new(context, changed), NoWorkResponse(original), f.Auth.Clock));
        Assert.Equal("Group model attempt response is not available.", error.Message); Assert.Null(error.InnerException);
        Assert.Equal(before, await f.CountsAsync()); Assert.Equal(reads, f.Keys.Reads);
    }

    [Fact]
    public async Task AttemptResponseNeverReleasesAParsedProposalAtOrAfterOriginalDeadline()
    {
        using var f = new Fixture(); var context = await FenceInputAsync(f);
        var permit = JournalGuardPermit(context, f.Auth.Clock.GetUtcNow()); var good = NoWorkResponse(permit);
        foreach (var times in new[] { new[] { permit.Start.DeadlineUtc }, new[] { permit.Start.StartedAtUtc, permit.Start.DeadlineUtc } })
        {
            var result = GroupModelAttemptResponse.Parse(permit, good, new ResponseSequenceClock(times));
            Assert.Equal(GroupModelAttemptResponseDisposition.Expired, result.Disposition); Assert.Null(result.Proposal);
        }
        Assert.Empty(f.Auth.Db.GroupModelAttemptStarts); Assert.Empty(f.Auth.Db.GroupModelAttemptFailures);
    }

    [Theory]
    [InlineData("backwards")]
    [InlineData("before-start")]
    [InlineData("nonutc")]
    public async Task AttemptResponseClockFailuresRemainClosedAuthorityErrors(string fault)
    {
        using var f = new Fixture(); var context = await FenceInputAsync(f);
        var permit = JournalGuardPermit(context, f.Auth.Clock.GetUtcNow()); var now = permit.Start.StartedAtUtc;
        var times = fault switch
        {
            "backwards" => new[] { now.AddTicks(1), now },
            "before-start" => new[] { now.AddTicks(-1) },
            "nonutc" => new[] { now.ToOffset(TimeSpan.FromHours(7)) },
            _ => throw new InvalidOperationException()
        };
        var error = Assert.Throws<InvalidOperationException>(() => GroupModelAttemptResponse.Parse(permit, NoWorkResponse(permit), new ResponseSequenceClock(times)));
        Assert.Equal("Group model attempt response is not available.", error.Message); Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task AttemptResponseCancellationAndNullCompositionDoNotInspectProviderOutput()
    {
        using var f = new Fixture(); var context = await FenceInputAsync(f);
        var permit = JournalGuardPermit(context, f.Auth.Clock.GetUtcNow());
        Assert.Throws<ArgumentNullException>(() => GroupModelAttemptResponse.Parse(null!, null, f.Auth.Clock));
        Assert.Throws<ArgumentNullException>(() => GroupModelAttemptResponse.Parse(permit, null, null!));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => GroupModelAttemptResponse.Parse(permit, null, new ResponseSequenceClock([]), cancelled.Token));
        Assert.Empty(f.Auth.Db.GroupModelAttemptStarts); Assert.Empty(f.Auth.Db.GroupModelAttemptFailures);
    }

    private sealed class ResponseSequenceClock(DateTimeOffset[] times) : TimeProvider
    {
        private int index;
        public override DateTimeOffset GetUtcNow() => times[index++];
    }
}
