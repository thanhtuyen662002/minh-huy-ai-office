using System.Diagnostics;

namespace MinhHuy.AIOffice.Agent.Worker;

// Process-wide physical ownership includes synchronous initialization, cache
// locking, BPE and cancellation callbacks. Caller timeout never frees a slot.
// No model, framing, provider or SQL authority is created by a token count.
internal sealed class BoundedStructuredTextTokenCounter
{
    internal const int MaximumPhysicalOperations = 4;
    internal const string CapacityMessage = "Structured text preparation capacity is occupied.";
    internal const string DeadlineMessage = "Structured text preparation deadline expired.";
    internal const string InvalidMessage = "Structured text preparation is unavailable.";
    private static readonly SemaphoreSlim Capacity = new(MaximumPhysicalOperations);
    private readonly Func<string, CancellationToken, int> count;
    private readonly TimeSpan timeout;

    internal BoundedStructuredTextTokenCounter() : this(new StructuredTextTokenCounter().CountText, TimeSpan.FromSeconds(30)) { }

    // Internal test seam; every instance, including test seams, shares capacity.
    internal BoundedStructuredTextTokenCounter(Func<string, CancellationToken, int> count, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(count);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(60))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        this.count = count; this.timeout = timeout;
    }

    internal Task<int> CountTextAsync(string text, CancellationToken caller = default) => BeginCount(text, caller).Caller;

    internal Operation BeginCount(string text, CancellationToken caller = default)
    {
        var started = Stopwatch.GetTimestamp();
        caller.ThrowIfCancellationRequested();
        if (!Capacity.Wait(0)) throw new InvalidOperationException(CapacityMessage);
        var owned = new BoundedStructuredResponses.OwnedCancellation();
        // Cache before any concurrent disposal; caller identity stays separate.
        var operationToken = owned.Token;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = Task.Run(async () =>
        {
            try
            {
                operationToken.ThrowIfCancellationRequested();
                CheckDeadline();
                var result = count(text, operationToken);
                operationToken.ThrowIfCancellationRequested();
                CheckDeadline();
                if (result < 0) throw new InvalidOperationException(InvalidMessage);
                return result;
            }
            catch (OperationCanceledException) when (operationToken.IsCancellationRequested) { throw; }
            catch (PreparationDeadlineException) { throw; }
            catch (Exception error) when (error is not OutOfMemoryException)
            { throw new InvalidOperationException(InvalidMessage); }
            finally
            {
                try { await owned.FinishAsync().ConfigureAwait(false); }
                finally { Capacity.Release(); completed.TrySetResult(); }
            }
        }, CancellationToken.None);
        _ = work.ContinueWith(fault => { _ = fault.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return new(ObserveAsync(), completed.Task);

        TimeSpan Remaining() => timeout - Stopwatch.GetElapsedTime(started);
        void CheckDeadline()
        {
            if (Remaining() <= TimeSpan.Zero) throw new PreparationDeadlineException();
        }
        async Task<int> ObserveAsync()
        {
            try
            {
                caller.ThrowIfCancellationRequested();
                var remaining = Remaining();
                if (remaining <= TimeSpan.Zero) throw new TimeoutException();
                var result = await work.WaitAsync(remaining, caller).ConfigureAwait(false);
                caller.ThrowIfCancellationRequested();
                if (Remaining() <= TimeSpan.Zero) throw new TimeoutException();
                return result;
            }
            catch (OperationCanceledException) when (caller.IsCancellationRequested)
            {
                owned.RequestCancellation();
                throw new OperationCanceledException(caller);
            }
            catch (TimeoutException)
            {
                owned.RequestCancellation();
                throw new InvalidOperationException(DeadlineMessage);
            }
            catch (PreparationDeadlineException)
            {
                throw new InvalidOperationException(DeadlineMessage);
            }
        }
    }

    internal sealed record Operation(Task<int> Caller, Task Completion);
    private sealed class PreparationDeadlineException : Exception { }
    public override string ToString() => "Structured text preparation (bounded local work).";
}
