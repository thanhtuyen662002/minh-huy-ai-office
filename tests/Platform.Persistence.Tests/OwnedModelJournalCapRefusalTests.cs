extern alias GroupReferenceProof;

using Xunit;
using Proof = GroupReferenceProof::MinhHuy.AIOffice.GroupReference.RuntimeProof.GroupModelAttemptJournalRuntimeProof;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class OwnedModelJournalCapRefusalTests
{
    [Fact]
    public async Task ExactSourceRefusalRunsOnceAndRequiresReleasedContenderWithoutRetry()
    {
        var calls = 0; var fences = 0;
        var outcome = await Proof.ObserveCapStartCoreAsync<object>(
            () => { calls++; throw new InvalidOperationException("Group source transaction is not available."); },
            () => { fences++; return true; }, default);
        Assert.Equal(1, calls); Assert.Equal(1, fences); Assert.True(outcome.SourceLockRefused); Assert.Null(outcome.Result);
    }

    [Fact]
    public async Task OriginalSuccessfulResultIsPreservedAndDoesNotEnterRefusalFence()
    {
        var calls = 0; var original = new object();
        var result = await Proof.ObserveCapStartCoreAsync(
            () => { calls++; return Task.FromResult(original); }, () => throw new InvalidOperationException(), default);
        Assert.Same(original, result.Result); Assert.False(result.SourceLockRefused); Assert.Equal(1, calls);
    }

    [Fact]
    public async Task SourceRefusalWithDirtyOrOpenContenderKeepsItsOriginalFailure()
    {
        var original = new InvalidOperationException("Group source transaction is not available.");
        var calls = 0;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Proof.ObserveCapStartCoreAsync<object>(
            () => { calls++; return Task.FromException<object>(original); }, () => false, default));
        Assert.Same(original, error); Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CancelledCallerNeverRunsAnOperationOrRefusalFence()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Proof.ObserveCapStartCoreAsync<object>(
            () => throw new InvalidOperationException(), () => throw new InvalidOperationException(), cancellation.Token));
    }

    [Fact]
    public async Task CancellationDuringSourceRefusalCannotBecomeALosingOutcome()
    {
        using var cancellation = new CancellationTokenSource(); var calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Proof.ObserveCapStartCoreAsync<object>(
            () => { calls++; cancellation.Cancel(); throw new InvalidOperationException("Group source transaction is not available."); },
            () => throw new InvalidOperationException(), cancellation.Token));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("padded")]
    [InlineData("lowercase")]
    [InlineData("journal")]
    [InlineData("denied")]
    [InlineData("reply")]
    [InlineData("subclass")]
    [InlineData("cancelled")]
    public async Task EveryOtherFailureEscapesUnchangedWithoutInvokingRefusalFence(string cause)
    {
        Exception original = cause switch
        {
            "padded" => new InvalidOperationException("Group source transaction is not available. "),
            "lowercase" => new InvalidOperationException("group source transaction is not available."),
            "journal" => new InvalidOperationException("Group model attempt journal is not available."),
            "denied" => new UnauthorizedAccessException("PRIVATE_REFUSAL_7912"),
            "reply" => new IOException("PRIVATE_REFUSAL_7912"),
            "subclass" => new SameMessageSubclass(),
            _ => new OperationCanceledException("PRIVATE_REFUSAL_7912")
        };
        var calls = 0;
        var error = await Record.ExceptionAsync(() => Proof.ObserveCapStartCoreAsync<object>(
            () => { calls++; return Task.FromException<object>(original); }, () => throw new InvalidOperationException(), default));
        Assert.Same(original, error); Assert.Equal(1, calls);
    }

    [Fact]
    public async Task NullSuccessCannotImpersonateASourceRefusal()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Proof.ObserveCapStartCoreAsync<object>(
            () => Task.FromResult<object>(null!), () => throw new InvalidOperationException(), default));
    }

    [Fact]
    public void MixedCapLosersStillRequireOneActualWinningStart()
    {
        Assert.Equal(1, Winner([Normal(false), Normal(true), Refused(), Normal(false)]));
    }

    [Fact]
    public void FourRefusalsCannotSatisfyTheCapProof()
    {
        Assert.Throws<InvalidOperationException>(() => Winner([Refused(), Refused(), Refused(), Refused()]));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    public void ActualFourContendersRemainRequired(int count)
    {
        var outcomes = Enumerable.Range(0, count).Select(i => Normal(i == 0)).ToArray();
        Assert.Throws<InvalidOperationException>(() => Winner(outcomes));
    }

    [Fact]
    public void TwoPermitsRemainAProofFailure()
    {
        Assert.Throws<InvalidOperationException>(() => Winner([Normal(true), Normal(true), Refused(), Normal(false)]));
    }

    [Theory]
    [InlineData("null-normal")]
    [InlineData("nonnull-refusal")]
    [InlineData("replayed")]
    [InlineData("lower-budget")]
    [InlineData("null-outcome")]
    public void MalformedOutcomeOrIncorrectBudgetCannotSatisfyTheCapProof(string cause)
    {
        Proof.CapStartOutcome<Start>[] outcomes = [Normal(true), Normal(false), Refused(), Normal(false)];
        outcomes[1] = cause switch
        {
            "null-normal" => new(null, false),
            "nonnull-refusal" => new(new(false, false, 3), true),
            "replayed" => new(new(false, true, 3), false),
            "lower-budget" => new(new(false, false, 2), false),
            _ => null!
        };
        Assert.Throws<InvalidOperationException>(() => Winner(outcomes));
    }

    private sealed record Start(bool Permit, bool Replayed, int Consumed);
    private static Proof.CapStartOutcome<Start> Normal(bool permit) => new(new(permit, false, 3), false);
    private static Proof.CapStartOutcome<Start> Refused() => new(null, true);
    private static int Winner(IReadOnlyList<Proof.CapStartOutcome<Start>> outcomes) =>
        Proof.RequireCapWinner(outcomes, x => x.Permit, x => !x.Replayed && x.Consumed == 3);
    private sealed class SameMessageSubclass() : InvalidOperationException("Group source transaction is not available.");
}
