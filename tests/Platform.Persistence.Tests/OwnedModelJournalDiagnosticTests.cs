extern alias GroupReferenceProof;

using Xunit;
using Proof = GroupReferenceProof::MinhHuy.AIOffice.GroupReference.RuntimeProof.GroupModelAttemptJournalRuntimeProof;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class OwnedModelJournalDiagnosticTests
{
    [Theory]
    [InlineData("source", "source-lock")]
    [InlineData("journal", "journal")]
    [InlineData("denied", "denied")]
    [InlineData("cancel", "cancelled")]
    [InlineData("private", "other")]
    public async Task ActualObservedOperationRunsOnceAndRetainsOnlyClosedFailureCodes(string cause, string kind)
    {
        Exception original = cause switch
        {
            "source" => new InvalidOperationException("Group source transaction is not available."),
            "journal" => new InvalidOperationException("Group model attempt journal is not available."),
            "denied" => new UnauthorizedAccessException("PRIVATE_JOURNAL_DIAGNOSTIC_8673"),
            "cancel" => new OperationCanceledException("PRIVATE_JOURNAL_DIAGNOSTIC_8673"),
            _ => new InvalidOperationException("Server=private;Password=PRIVATE_JOURNAL_DIAGNOSTIC_8673")
        };
        var calls = 0; Proof.PhaseFailure? recorded = null;
        var failure = await Assert.ThrowsAsync<Proof.PhaseFailure>(() => Proof.ObserveCoreAsync<int>(Proof.JournalPhase.CapStart,
            () => { calls++; return Task.FromException<int>(original); }, x => recorded = x));
        Assert.Equal(1, calls); Assert.Same(failure, recorded); Assert.Null(failure.InnerException);
        Assert.Equal("FAIL owned model journal runtime phase-cap-start kind-" + kind, Proof.FailureDiagnostic(failure));
        Assert.DoesNotContain("PRIVATE_JOURNAL_DIAGNOSTIC", failure.ToString());
        Assert.DoesNotContain("Server=", Proof.FailureDiagnostic(failure));
    }

    [Fact]
    public async Task SuccessfulObservedOperationKeepsItsOriginalValueAndDoesNotRecordFailure()
    {
        var calls = 0; var records = 0; var original = new object();
        var result = await Proof.ObserveCoreAsync(Proof.JournalPhase.CapStart,
            () => { calls++; return Task.FromResult(original); }, _ => records++);
        Assert.Same(original, result); Assert.Equal(1, calls); Assert.Equal(0, records);
    }

    [Fact]
    public async Task EarlierClosedContenderFailureSurvivesOuterObservationWithoutRebindingItsPhase()
    {
        var original = new Proof.PhaseFailure(Proof.JournalPhase.CapClaim, Proof.FailureKind.SourceLock);
        Proof.PhaseFailure? recorded = null;
        var error = await Assert.ThrowsAsync<Proof.PhaseFailure>(() => Proof.ObserveCoreAsync<int>(Proof.JournalPhase.CapAggregate,
            () => Task.FromException<int>(original), x => recorded = x));
        Assert.Same(original, error); Assert.Same(original, recorded);
        Assert.Equal("FAIL owned model journal runtime phase-cap-claim kind-source-lock", Proof.FailureDiagnostic(error));
    }

    [Fact]
    public async Task ConcurrentObservedFailuresKeepTheFirstClosedPhaseAndNeverOverwriteIt()
    {
        var diagnostic = new Proof.JournalDiagnostic();
        var first = await Assert.ThrowsAsync<Proof.PhaseFailure>(() => Proof.ObserveAsync<int>(Proof.JournalPhase.CapClaim,
            () => Task.FromException<int>(new InvalidOperationException("Group source transaction is not available.")), diagnostic));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 4).Select(_ => Assert.ThrowsAsync<Proof.PhaseFailure>(() =>
            Proof.ObserveAsync<int>(Proof.JournalPhase.CapStart, async () =>
            {
                await release.Task;
                throw new InvalidOperationException("PRIVATE_JOURNAL_DIAGNOSTIC_8673");
            }, diagnostic))).ToArray();
        release.SetResult();
        var later = await Task.WhenAll(tasks);
        Assert.Equal(4, later.Length); Assert.Same(first, diagnostic.FirstFailure);
        Assert.All(later, x => Assert.Equal("FAIL owned model journal runtime phase-cap-start kind-other", Proof.FailureDiagnostic(x)));
        Assert.Equal("FAIL owned model journal runtime phase-cap-claim kind-source-lock", Proof.FailureDiagnostic(diagnostic.FirstFailure!));
    }

    [Fact]
    public void ForeignExceptionTextAndUndefinedCodesCannotBecomeDiagnosticOutput()
    {
        Assert.Equal("FAIL owned model journal runtime phase-unknown kind-other",
            Proof.FailureDiagnostic(new InvalidOperationException("phase-cap-start kind-source-lock PRIVATE_JOURNAL_DIAGNOSTIC_8673")));
        Assert.Equal("FAIL owned model journal runtime phase-unknown kind-other",
            Proof.FailureDiagnostic(new Proof.PhaseFailure((Proof.JournalPhase)999, (Proof.FailureKind)999)));
    }
}
