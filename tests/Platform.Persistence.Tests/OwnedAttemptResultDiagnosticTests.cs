extern alias GroupReferenceProof;

using Xunit;
using Proof = GroupReferenceProof::MinhHuy.AIOffice.GroupReference.RuntimeProof.GroupModelAttemptResultRuntimeProof;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class OwnedAttemptResultDiagnosticTests
{
    [Fact]
    public async Task FourDistinctContextsPrepareOnceSequentiallyThenAllFourReadsOverlapAndDrain()
    {
        using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var diagnostic = new Proof.ResultDiagnostic(); var activeSetup = 0; var prepared = 0; var entered = 0; var completed = 0;
        var created = new List<int>(); var disposed = new List<int>(); var prepareCalls = new int[4]; var readCalls = new int[4];
        var allEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Proof.RunPreparedConcurrentReadsAsync(index => { created.Add(index); return index; }, async (index, context, _) =>
        {
            Assert.Equal(index, context); Assert.Equal(0, entered); Assert.Equal(1, Interlocked.Increment(ref activeSetup));
            prepareCalls[index]++; await Task.Yield(); Interlocked.Decrement(ref activeSetup); prepared++;
        }, async (index, context, token) =>
        {
            Assert.Equal(index, context); Assert.Equal(4, prepared); Assert.Equal(0, activeSetup); readCalls[index]++;
            if (Interlocked.Increment(ref entered) == 4) allEntered.TrySetResult();
            await allEntered.Task.WaitAsync(token); Interlocked.Increment(ref completed);
        }, context => { Assert.Equal(4, completed); disposed.Add(context); return ValueTask.CompletedTask; }, diagnostic, bounded.Token);
        Assert.Equal([0, 1, 2, 3], created); Assert.Equal(created, disposed); Assert.Equal(4, completed);
        Assert.All(prepareCalls, count => Assert.Equal(1, count)); Assert.All(readCalls, count => Assert.Equal(1, count));
        Assert.Null(diagnostic.FirstFailure);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task AnyPreparationSourceLockFailureDisposesEveryCreatedContextAndLaunchesNoRead(int failedIndex)
    {
        var diagnostic = new Proof.ResultDiagnostic(); var created = new List<int>(); var disposed = new List<int>();
        var prepareCalls = new int[4]; var reads = 0;
        var failure = await Assert.ThrowsAsync<Proof.PhaseFailure>(() => Proof.RunPreparedConcurrentReadsAsync(
            index => { created.Add(index); return index; }, (index, _, _) =>
            {
                prepareCalls[index]++;
                if (index == failedIndex) throw new InvalidOperationException("Group source transaction is not available.");
                return Task.CompletedTask;
            }, (_, _, _) => { reads++; return Task.CompletedTask; },
            context => { disposed.Add(context); return ValueTask.CompletedTask; }, diagnostic, default));
        Assert.Equal(Proof.ResultPhase.ConcurrentPreparation, failure.Phase); Assert.Equal(Proof.FailureKind.SourceLock, failure.Kind);
        Assert.Same(failure, diagnostic.FirstFailure); Assert.Equal(0, reads); Assert.Equal(failedIndex + 1, created.Count);
        Assert.Equal(created, disposed); Assert.All(prepareCalls.Take(failedIndex + 1), count => Assert.Equal(1, count));
        Assert.All(prepareCalls.Skip(failedIndex + 1), count => Assert.Equal(0, count));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ContextFactoryFailureDisposesOnlyOwnedContextsWithoutContinuingSetup(int failedIndex)
    {
        var diagnostic = new Proof.ResultDiagnostic(); var created = new List<int>(); var disposed = new List<int>();
        var factoryCalls = 0; var prepared = 0; var reads = 0;
        var failure = await Assert.ThrowsAsync<Proof.PhaseFailure>(() => Proof.RunPreparedConcurrentReadsAsync(index =>
        {
            factoryCalls++;
            if (index == failedIndex) throw new InvalidOperationException("PRIVATE_RESULT_DIAGNOSTIC_FACTORY");
            created.Add(index); return index;
        }, (_, _, _) => { prepared++; return Task.CompletedTask; }, (_, _, _) => { reads++; return Task.CompletedTask; },
            context => { disposed.Add(context); return ValueTask.CompletedTask; }, diagnostic, default));
        Assert.Equal(Proof.FailureKind.Other, failure.Kind); Assert.Equal(Proof.ResultPhase.ConcurrentPreparation, failure.Phase);
        Assert.Equal(failedIndex + 1, factoryCalls); Assert.Equal(failedIndex, prepared); Assert.Equal(0, reads);
        Assert.Equal(created, disposed); Assert.Null(failure.InnerException); Assert.DoesNotContain("PRIVATE_RESULT_DIAGNOSTIC", failure.ToString());
    }

    [Fact]
    public async Task CallerCancelledDuringPreparationCannotLaunchPartialReadSetAndReleasesOwnedContexts()
    {
        using var caller = new CancellationTokenSource(); var diagnostic = new Proof.ResultDiagnostic();
        var created = 0; var disposed = 0; var reads = 0;
        var failure = await Assert.ThrowsAsync<Proof.PhaseFailure>(() => Proof.RunPreparedConcurrentReadsAsync(
            index => { created++; return index; }, (index, _, token) =>
            {
                if (index == 2) { caller.Cancel(); token.ThrowIfCancellationRequested(); }
                return Task.CompletedTask;
            }, (_, _, _) => { reads++; return Task.CompletedTask; },
            _ => { disposed++; return ValueTask.CompletedTask; }, diagnostic, caller.Token));
        Assert.Equal(Proof.FailureKind.Cancelled, failure.Kind); Assert.Equal(3, created); Assert.Equal(created, disposed); Assert.Equal(0, reads);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task AnyActualReadRefusalStillFailsCancelsAndDrainsAllFourPreparedContexts(int failedIndex)
    {
        using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(5)); var diagnostic = new Proof.ResultDiagnostic();
        var first = new Proof.PhaseFailure(Proof.ResultPhase.ConcurrentRead, Proof.FailureKind.SourceLock);
        var prepared = 0; var entered = 0; var drained = 0; var disposed = 0; var readCalls = new int[4];
        var allEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = await Assert.ThrowsAsync<Proof.PhaseFailure>(() => Proof.RunPreparedConcurrentReadsAsync(index => index,
            (_, _, _) => { prepared++; return Task.CompletedTask; }, async (index, _, token) =>
            {
                readCalls[index]++;
                try
                {
                    if (Interlocked.Increment(ref entered) == 4) allEntered.TrySetResult();
                    await allEntered.Task.WaitAsync(token);
                    if (index == failedIndex) throw first;
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                finally { Interlocked.Increment(ref drained); }
            }, _ => { Assert.Equal(4, drained); disposed++; return ValueTask.CompletedTask; }, diagnostic, bounded.Token));
        Assert.Same(first, failure); Assert.Same(first, diagnostic.FirstFailure); Assert.Equal(4, prepared); Assert.Equal(4, drained);
        Assert.Equal(4, disposed); Assert.All(readCalls, count => Assert.Equal(1, count)); Assert.False(bounded.IsCancellationRequested);
    }

    [Fact]
    public async Task SuccessfulFourReadsCannotHideAnyCleanupFailureAndEveryContextIsReleased()
    {
        var diagnostic = new Proof.ResultDiagnostic(); var reads = 0; var disposed = 0;
        var failure = await Assert.ThrowsAsync<Proof.PhaseFailure>(() => Proof.RunPreparedConcurrentReadsAsync(index => index,
            (_, _, _) => Task.CompletedTask, (_, _, _) => { reads++; return Task.CompletedTask; }, _ =>
            { disposed++; return ValueTask.FromException(new InvalidOperationException("PRIVATE_RESULT_DIAGNOSTIC_DISPOSE")); }, diagnostic, default));
        Assert.Equal(4, reads); Assert.Equal(4, disposed); Assert.Equal(Proof.ResultPhase.ConcurrentChecks, failure.Phase);
        Assert.Equal(Proof.FailureKind.Other, failure.Kind); Assert.Null(failure.InnerException);
        Assert.DoesNotContain("PRIVATE_RESULT_DIAGNOSTIC", failure.ToString());
    }

    [Theory]
    [InlineData("source", "source-lock")]
    [InlineData("result", "result")]
    [InlineData("plan", "plan")]
    [InlineData("journal", "journal")]
    [InlineData("denied", "denied")]
    [InlineData("cancel", "cancelled")]
    [InlineData("private", "other")]
    public async Task OneObservedOperationRetainsOnlyClosedFailureCodes(string cause, string kind)
    {
        Exception original = cause switch
        {
            "source" => new InvalidOperationException("Group source transaction is not available."),
            "result" => new InvalidOperationException("Group model attempt stored result is unavailable."),
            "plan" => new InvalidOperationException("Group original input plan store is not available."),
            "journal" => new InvalidOperationException("Group model attempt journal is not available."),
            "denied" => new UnauthorizedAccessException("PRIVATE_RESULT_DIAGNOSTIC_1281"),
            "cancel" => new OperationCanceledException("PRIVATE_RESULT_DIAGNOSTIC_1281"),
            _ => new InvalidOperationException("Server=private;Password=PRIVATE_RESULT_DIAGNOSTIC_1281")
        };
        var calls = 0; var diagnostic = new Proof.ResultDiagnostic();
        var failure = await Assert.ThrowsAsync<Proof.PhaseFailure>(() => Proof.ObserveAsync<int>(Proof.ResultPhase.ConcurrentPreparation,
            () => { calls++; return Task.FromException<int>(original); }, diagnostic));
        Assert.Equal(1, calls); Assert.Same(failure, diagnostic.FirstFailure); Assert.Null(failure.InnerException);
        Assert.Equal("FAIL owned attempt result runtime phase-concurrent-preparation kind-" + kind, Proof.FailureDiagnostic(failure));
        Assert.DoesNotContain("PRIVATE_RESULT_DIAGNOSTIC", failure.ToString());
        Assert.DoesNotContain("Server=", Proof.FailureDiagnostic(failure));
    }

    [Fact]
    public async Task SuccessfulObservationPreservesValueAndNoFailure()
    {
        var calls = 0; var diagnostic = new Proof.ResultDiagnostic(); var original = new object();
        var result = await Proof.ObserveAsync(Proof.ResultPhase.ResumeRead,
            () => { calls++; return Task.FromResult(original); }, diagnostic);
        Assert.Same(original, result); Assert.Equal(1, calls); Assert.Null(diagnostic.FirstFailure);
    }

    [Fact]
    public async Task NestedObservationCannotRebindAnEarlierFailure()
    {
        var diagnostic = new Proof.ResultDiagnostic();
        var first = new Proof.PhaseFailure(Proof.ResultPhase.ConcurrentClaim, Proof.FailureKind.SourceLock);
        var failure = await Assert.ThrowsAsync<Proof.PhaseFailure>(() => Proof.ObserveAsync<int>(Proof.ResultPhase.ConcurrentReads,
            () => Task.FromException<int>(first), diagnostic));
        Assert.Same(first, failure); Assert.Same(first, diagnostic.FirstFailure);
        Assert.Equal("FAIL owned attempt result runtime phase-concurrent-claim kind-source-lock", Proof.FailureDiagnostic(failure));
    }

    [Fact]
    public void ForeignTextAndUndefinedCodesCannotBecomeOutput()
    {
        Assert.Equal("FAIL owned attempt result runtime phase-unknown kind-other",
            Proof.FailureDiagnostic(new InvalidOperationException("phase-concurrent-read kind-source-lock PRIVATE_RESULT_DIAGNOSTIC_1281")));
        Assert.Equal("FAIL owned attempt result runtime phase-unknown kind-other",
            Proof.FailureDiagnostic(new Proof.PhaseFailure((Proof.ResultPhase)999, (Proof.FailureKind)999)));
    }

    [Fact]
    public async Task ConcurrentSuccessRequiresFourCompletedOneShotOperations()
    {
        using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var diagnostic = new Proof.ResultDiagnostic(); var entered = 0; var completed = 0;
        var calls = new int[4]; var allEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Proof.RunConcurrentReadsAsync(async (index, token) =>
        {
            Interlocked.Increment(ref calls[index]);
            if (Interlocked.Increment(ref entered) == 4) allEntered.SetResult();
            await allEntered.Task.WaitAsync(token);
            Interlocked.Increment(ref completed);
        }, diagnostic, bounded.Token);
        Assert.Equal(4, entered); Assert.Equal(4, completed); Assert.All(calls, count => Assert.Equal(1, count));
        Assert.Null(diagnostic.FirstFailure);
    }

    [Theory]
    [InlineData(0, "claim")]
    [InlineData(1, "claim")]
    [InlineData(2, "claim")]
    [InlineData(3, "claim")]
    [InlineData(0, "preparation")]
    [InlineData(1, "preparation")]
    [InlineData(2, "preparation")]
    [InlineData(3, "preparation")]
    [InlineData(0, "read")]
    [InlineData(1, "read")]
    [InlineData(2, "read")]
    [InlineData(3, "read")]
    public async Task FailureAtEachContenderAndPhaseCancelsAndDrainsAllOperations(int failedIndex, string phase)
    {
        using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var diagnostic = new Proof.ResultDiagnostic(); var entered = 0; var drained = 0;
        var calls = new int[4]; var allEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var actualPhase = phase switch
        {
            "claim" => Proof.ResultPhase.ConcurrentClaim,
            "preparation" => Proof.ResultPhase.ConcurrentPreparation,
            _ => Proof.ResultPhase.ConcurrentRead
        };
        var first = new Proof.PhaseFailure(actualPhase, Proof.FailureKind.Plan);
        var failure = await Assert.ThrowsAsync<Proof.PhaseFailure>(() => Proof.RunConcurrentReadsAsync(async (index, token) =>
        {
            Interlocked.Increment(ref calls[index]);
            try
            {
                if (Interlocked.Increment(ref entered) == 4) allEntered.SetResult();
                await allEntered.Task.WaitAsync(token);
                if (index == failedIndex) throw first;
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            finally { Interlocked.Increment(ref drained); }
        }, diagnostic, bounded.Token));
        Assert.Same(first, failure); Assert.Same(first, diagnostic.FirstFailure);
        Assert.False(bounded.IsCancellationRequested); Assert.Equal(4, drained);
        Assert.All(calls, count => Assert.Equal(1, count));
    }

    [Fact]
    public async Task SourceLockRefusalRemainsFailureAndIsNeverRetriedOrCountedAsSuccess()
    {
        using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var diagnostic = new Proof.ResultDiagnostic(); var calls = new int[4];
        var failure = await Assert.ThrowsAsync<Proof.PhaseFailure>(() => Proof.RunConcurrentReadsAsync((index, _) =>
        {
            Interlocked.Increment(ref calls[index]);
            throw new InvalidOperationException("Group source transaction is not available.");
        }, diagnostic, bounded.Token));
        Assert.Equal(Proof.FailureKind.SourceLock, failure.Kind); Assert.Same(failure, diagnostic.FirstFailure);
        Assert.All(calls, count => Assert.InRange(count, 0, 1)); Assert.InRange(calls.Sum(), 1, 4);
    }

    [Fact]
    public async Task CancelledCallerRunsNoOperation()
    {
        using var stopped = new CancellationTokenSource(); stopped.Cancel();
        var calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Proof.RunConcurrentReadsAsync((_, _) =>
        { calls++; return Task.CompletedTask; }, new(), stopped.Token));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task HostileExceptionGettersAndToStringAreNeverUsedByDiagnostics()
    {
        var diagnostic = new Proof.ResultDiagnostic();
        var failure = await Assert.ThrowsAsync<Proof.PhaseFailure>(() => Proof.ObserveAsync<int>(Proof.ResultPhase.ConcurrentRead,
            () => Task.FromException<int>(new HostileException()), diagnostic));
        Assert.Equal(Proof.FailureKind.Other, failure.Kind); Assert.Null(failure.InnerException);
        Assert.Equal("FAIL owned attempt result runtime phase-concurrent-read kind-other", Proof.FailureDiagnostic(failure));
        Assert.Equal("FAIL owned attempt result runtime phase-unknown kind-other", Proof.FailureDiagnostic(new HostileException()));
    }

    [Fact]
    public async Task OperationFailureSurvivesLaterContextDisposalFailureAndAllTasksAreDrained()
    {
        using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var diagnostic = new Proof.ResultDiagnostic(); var disposed = 0;
        var first = new Proof.PhaseFailure(Proof.ResultPhase.ConcurrentRead, Proof.FailureKind.Result);
        var failure = await Assert.ThrowsAsync<Proof.PhaseFailure>(() => Proof.RunConcurrentReadsAsync(async (index, token) =>
        {
            await using var context = new Disposal(() => { Interlocked.Increment(ref disposed); });
            if (index == 0)
                await Proof.ObserveAsync<int>(Proof.ResultPhase.ConcurrentRead, () => Task.FromException<int>(first), diagnostic);
            else await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }, diagnostic, bounded.Token));
        Assert.Same(first, failure); Assert.Same(first, diagnostic.FirstFailure); Assert.InRange(disposed, 1, 4);
        Assert.False(bounded.IsCancellationRequested);
    }

    [Fact]
    public async Task SuccessfulReadWithFailedContextDisposalCannotQualify()
    {
        using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var diagnostic = new Proof.ResultDiagnostic(); var reads = 0; var disposed = 0;
        var failure = await Assert.ThrowsAsync<Proof.PhaseFailure>(() => Proof.RunConcurrentReadsAsync(async (_, _) =>
        {
            await using var context = new Disposal(() => { Interlocked.Increment(ref disposed); });
            Interlocked.Increment(ref reads);
            await Task.CompletedTask;
        }, diagnostic, bounded.Token));
        Assert.Equal(Proof.FailureKind.Other, failure.Kind); Assert.Equal(reads, disposed); Assert.InRange(reads, 1, 4);
        Assert.DoesNotContain("PRIVATE_RESULT_DIAGNOSTIC", failure.ToString());
    }

    private sealed class HostileException : InvalidOperationException
    {
        public override string Message => throw new InvalidOperationException("PRIVATE_RESULT_DIAGNOSTIC_GETTER");
        public override string ToString() => throw new InvalidOperationException("PRIVATE_RESULT_DIAGNOSTIC_FORMAT");
    }
    private sealed class Disposal(Action disposed) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            disposed();
            return ValueTask.FromException(new InvalidOperationException("PRIVATE_RESULT_DIAGNOSTIC_DISPOSAL"));
        }
    }
}

public sealed partial class GroupBatchSourceReaderTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("retained")]
    public async Task ActualStoredResultKeyFailureProjectsOnlyRealStoreContract(string? keyId)
    {
        using var f = new Fixture(); var (plan, chunks, _, _) = await ResultInputAsync(f, false);
        var original = GroupModelAttemptOriginalBinding.Create(plan, chunks[0]);
        var clock = new ResultDeadlineClock(); var keys = new DeferredResultKeys();
        var store = new GroupModelAttemptResultStore(f.Auth.Db, f.Worker, clock, f.Reader,
            new(f.Auth.Db, f.Worker, f.Auth.Clock, f.Keys, new()), keys, new());
        var before = await f.CountsAsync(); var diagnostic = new Proof.ResultDiagnostic();
        // The shipping resolver normalizes this actual cold provider fault.
        // The diagnostic receives that real exception, not a fabricated message.
        var pending = Proof.ObserveAsync(Proof.ResultPhase.ConcurrentRead,
            () => ResolveStoredResultKey(store, original, keyId, default), diagnostic);
        keys.Pending.SetException(new InvalidOperationException("PRIVATE_RESULT_DIAGNOSTIC_PROVIDER"));
        var failure = await Assert.ThrowsAsync<Proof.PhaseFailure>(() => pending);
        Assert.Equal(Proof.FailureKind.Result, failure.Kind); Assert.Null(failure.InnerException);
        Assert.Same(failure, diagnostic.FirstFailure); Assert.True(clock.Disposed);
        Assert.Equal("FAIL owned attempt result runtime phase-concurrent-read kind-result", Proof.FailureDiagnostic(failure));
        Assert.DoesNotContain("PRIVATE_RESULT_DIAGNOSTIC", failure.ToString());
        Assert.Equal(before, await f.CountsAsync()); Assert.Null(f.Auth.Db.Database.CurrentTransaction);
        Assert.False(f.Auth.Db.ChangeTracker.HasChanges());
    }

    [Fact]
    public async Task NonexistentV1StoreMessageCannotMasqueradeAsActualResultFailure()
    {
        var diagnostic = new Proof.ResultDiagnostic();
        var failure = await Assert.ThrowsAsync<Proof.PhaseFailure>(() => Proof.ObserveAsync<int>(Proof.ResultPhase.ConcurrentRead,
            () => Task.FromException<int>(new InvalidOperationException("Group model attempt result store is not available.")), diagnostic));
        Assert.Equal(Proof.FailureKind.Other, failure.Kind);
        Assert.Equal("FAIL owned attempt result runtime phase-concurrent-read kind-other", Proof.FailureDiagnostic(failure));
    }
}
