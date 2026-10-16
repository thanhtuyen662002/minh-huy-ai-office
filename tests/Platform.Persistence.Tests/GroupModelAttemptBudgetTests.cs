using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class GroupModelAttemptBudgetTests
{
    private static readonly GroupScope Scope = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
    private static readonly Guid Batch = Guid.NewGuid();
    private const string Input = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 11, 0, 0, TimeSpan.Zero);
    private static GroupModelAttemptStarted Start(int ordinal = 1, GroupModelAttemptStarted? prior = null, int seconds = 0) => new(
        Scope, Batch, Input, ordinal, Guid.NewGuid(), prior?.RequestId, Guid.NewGuid(), ordinal,
        Now.AddSeconds(seconds), Now.AddSeconds(seconds + 120));
    private static GroupModelAttemptFailed Fail(GroupModelAttemptStarted start, GroupModelAttemptFailure failure = GroupModelAttemptFailure.TransientProvider,
        int seconds = 10) => new(Scope, start.RequestId, failure, start.StartedAtUtc.AddSeconds(seconds));
    private static GroupModelAttemptBudgetDecision Read(GroupModelAttemptStarted[] starts, GroupModelAttemptFailed[] failures, int seconds = 0) =>
        GroupModelAttemptBudget.Read(Scope, Batch, Input, starts, failures, Now.AddSeconds(seconds));

    [Fact]
    public void EmptyJournalIsAvailableAndActiveOrUnobservedAttemptIsConsumedUntilDeadlineAndBackoff()
    {
        Assert.Equal(new(GroupModelAttemptDisposition.Available, 0, null), Read([], []));
        var first = Start();
        Assert.Equal(new(GroupModelAttemptDisposition.Wait, 1, Now.AddSeconds(135)), Read([first], []));
        Assert.Equal(GroupModelAttemptDisposition.Wait, Read([first], [], 134).Disposition);
        Assert.Equal(new(GroupModelAttemptDisposition.Available, 1, null), Read([first], [], 135));
    }

    [Fact]
    public void ThreeContiguousFailedAttemptsRespectBackoffAndOnlyThenReportStructuralExhaustion()
    {
        var first = Start(); var second = Start(2, first, 25); var third = Start(3, second, 95);
        var starts = new[] { first, second, third }; var failures = starts.Select(x => Fail(x)).ToArray();
        Assert.Equal(new(GroupModelAttemptDisposition.Wait, 3, Now.AddSeconds(215)), Read(starts, failures.Take(2).ToArray(), 95));
        Assert.Equal(new(GroupModelAttemptDisposition.Exhausted, 3, null), Read(starts, failures, 105));
    }

    [Fact]
    public void InterruptedStartsSurviveReplacementClaimAndUnknownResponsesDoNotResetBudget()
    {
        var first = Start(); var second = Start(2, first, 135); var third = Start(3, second, 315);
        Assert.NotEqual(first.ClaimOperationId, second.ClaimOperationId);
        Assert.Equal(GroupModelAttemptDisposition.Wait, Read([first, second, third], [], 434).Disposition);
        Assert.Equal(new(GroupModelAttemptDisposition.Exhausted, 3, null), Read([first, second, third], [], 435));
    }

    [Fact]
    public void PermanentProviderFailurePreventsFurtherAttempts()
    {
        var first = Start(); var failure = Fail(first, GroupModelAttemptFailure.PermanentProvider);
        Assert.Equal(new(GroupModelAttemptDisposition.Exhausted, 1, null), Read([first], [failure], 10));
        Assert.Throws<InvalidOperationException>(() => Read([first, Start(2, first, 200)], [failure], 200));
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("batch")]
    [InlineData("hash")]
    [InlineData("ordinal")]
    [InlineData("empty-request")]
    [InlineData("claim")]
    [InlineData("epoch")]
    [InlineData("future")]
    [InlineData("offset")]
    [InlineData("deadline")]
    [InlineData("duration")]
    [InlineData("predecessor")]
    public void MalformedStartRefusesPrivatelyWithoutClassifyingExhaustion(string fault)
    {
        var first = Start();
        first = fault switch
        {
            "scope" => first with { Scope = Scope with { CompanyId = Guid.NewGuid() } },
            "batch" => first with { BatchId = Guid.NewGuid() },
            "hash" => first with { InputSha256 = new string('B', 64) },
            "ordinal" => first with { Ordinal = 2 },
            "empty-request" => first with { RequestId = Guid.Empty },
            "claim" => first with { ClaimOperationId = Guid.Empty },
            "epoch" => first with { ClaimEpoch = 0 },
            "future" => first with { StartedAtUtc = Now.AddSeconds(1) },
            "offset" => first with { StartedAtUtc = Now.ToOffset(TimeSpan.FromHours(1)) },
            "deadline" => first with { DeadlineUtc = Now },
            "duration" => first with { DeadlineUtc = Now.AddSeconds(121) },
            _ => first with { PredecessorRequestId = Guid.NewGuid() }
        };
        var error = Assert.Throws<InvalidOperationException>(() => Read([first], []));
        Assert.Equal("Group model attempt journal is not available.", error.Message); Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("unknown")]
    [InlineData("enum")]
    [InlineData("before")]
    [InlineData("after-deadline")]
    [InlineData("future")]
    [InlineData("offset")]
    public void MalformedFailureDoesNotAuthorizeAnotherAttemptOrAttention(string fault)
    {
        var first = Start(); var failure = Fail(first);
        failure = fault switch
        {
            "foreign" => failure with { Scope = Scope with { TenantId = Guid.NewGuid() } },
            "unknown" => failure with { RequestId = Guid.NewGuid() },
            "enum" => failure with { Failure = (GroupModelAttemptFailure)999 },
            "before" => failure with { ObservedAtUtc = Now.AddTicks(-1) },
            "after-deadline" => failure with { ObservedAtUtc = Now.AddSeconds(121) },
            "future" => failure with { ObservedAtUtc = Now.AddSeconds(21) },
            _ => failure with { ObservedAtUtc = Now.ToOffset(TimeSpan.FromHours(1)) }
        };
        Assert.Throws<InvalidOperationException>(() => Read([first], [failure], 20));
    }

    [Fact]
    public void DuplicateGapReorderedEarlyAndOverBudgetHistoriesRefuse()
    {
        var first = Start(); var second = Start(2, first, 135); var third = Start(3, second, 315); var fourth = Start(4, third, 495);
        foreach (var rows in new[] { new[] { first, first }, new[] { first, third }, new[] { second, first },
            new[] { first, second with { StartedAtUtc = Now.AddSeconds(134) } }, new[] { first, second, third, fourth } })
            Assert.Throws<InvalidOperationException>(() => Read(rows, [], 600));
        var failure = Fail(first);
        Assert.Throws<InvalidOperationException>(() => Read([first], [failure, failure], 20));
        Assert.Throws<InvalidOperationException>(() => Read([null!], []));
        Assert.Throws<InvalidOperationException>(() => Read([first], [null!]));
    }
}
