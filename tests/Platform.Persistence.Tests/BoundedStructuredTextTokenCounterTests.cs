extern alias RuntimeWorker;
using RuntimeWorker::MinhHuy.AIOffice.Agent.Worker;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

[CollectionDefinition(nameof(BoundedTokenizationCollection), DisableParallelization = true)]
public sealed class BoundedTokenizationCollection { }

// Controlled synchronous stalls exercise ownership; real offline BPE fixtures
// remain in the other two test bodies. No provider/model/runtime DI evidence.
[Collection(nameof(BoundedTokenizationCollection))]
public sealed class BoundedStructuredTextTokenCounterTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(4);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task Actual_offline_counter_works_through_default_bounded_path()
    {
        var operation = new BoundedStructuredTextTokenCounter().BeginCount("Tra cứu tồn kho, nhập xuất");
        Assert.Equal(7, await operation.Caller.WaitAsync(Guard));
        await operation.Completion.WaitAsync(Guard);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Four_late_cpu_operations_keep_slots_across_instances_until_actual_completion(bool lateFault)
    {
        var entered = Signal(); var release = Signal(); var calls = 0;
        var operations = new List<BoundedStructuredTextTokenCounter.Operation>();
        try
        {
            for (var index = 0; index < 4; index++)
            {
                var counter = new BoundedStructuredTextTokenCounter((_, _) =>
                {
                    if (Interlocked.Increment(ref calls) == 4) entered.TrySetResult();
                    release.Task.GetAwaiter().GetResult();
                    if (lateFault) throw new InvalidOperationException("PRIVATE_LATE_CPU_CANARY");
                    return 7;
                }, TimeSpan.FromMilliseconds(300));
                operations.Add(counter.BeginCount("synthetic"));
            }
            await entered.Task.WaitAsync(Guard);
            foreach (var operation in operations)
            {
                var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => operation.Caller.WaitAsync(Guard));
                Assert.Equal(BoundedStructuredTextTokenCounter.DeadlineMessage, failure.Message);
                Assert.Null(failure.InnerException);
                Assert.False(operation.Completion.IsCompleted);
            }
            var fifth = new BoundedStructuredTextTokenCounter((_, _) => { Interlocked.Increment(ref calls); return 1; }, Guard);
            var denied = Assert.Throws<InvalidOperationException>(() => fifth.BeginCount("fifth"));
            Assert.Equal(BoundedStructuredTextTokenCounter.CapacityMessage, denied.Message);
            Assert.Equal(4, Volatile.Read(ref calls));
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(operations.Select(x => x.Completion)).WaitAsync(Guard);
        }
        foreach (var operation in operations)
        {
            Assert.True(operation.Caller.IsFaulted);
            Assert.DoesNotContain("PRIVATE_LATE_CPU_CANARY", operation.Caller.Exception!.ToString());
        }
        Assert.Equal(7, await new BoundedStructuredTextTokenCounter().CountTextAsync("Tra cứu tồn kho, nhập xuất").WaitAsync(Guard));
    }

    [Fact]
    public async Task Cancellation_keeps_original_identity_and_discards_late_success()
    {
        using var caller = new CancellationTokenSource();
        var entered = Signal(); var release = Signal();
        var operation = new BoundedStructuredTextTokenCounter((_, _) =>
        { entered.SetResult(); release.Task.GetAwaiter().GetResult(); return 7; }, Guard).BeginCount("synthetic", caller.Token);
        try
        {
            await entered.Task.WaitAsync(Guard); caller.Cancel();
            var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.Caller.WaitAsync(Guard));
            Assert.Equal(caller.Token, failure.CancellationToken);
            Assert.False(operation.Completion.IsCompleted);
        }
        finally { release.TrySetResult(); await operation.Completion.WaitAsync(Guard); }
        Assert.True(operation.Caller.IsCanceled);
    }

    [Fact]
    public async Task Cancellation_callbacks_keep_capacity_after_cpu_returns()
    {
        var callbackEntered = Signal(); var releaseCallback = Signal(); var cpuReturned = Signal();
        var callbackCount = 0; var returnedCount = 0;
        var operations = new List<BoundedStructuredTextTokenCounter.Operation>();
        var registrations = new List<CancellationTokenRegistration>();
        var counter = new BoundedStructuredTextTokenCounter((_, token) =>
        {
            lock (registrations) registrations.Add(token.Register(() =>
            {
                if (Interlocked.Increment(ref callbackCount) == 4) callbackEntered.TrySetResult();
                releaseCallback.Task.GetAwaiter().GetResult();
            }));
            token.WaitHandle.WaitOne();
            if (Interlocked.Increment(ref returnedCount) == 4) cpuReturned.TrySetResult();
            return 7;
        }, TimeSpan.FromMilliseconds(300));
        try
        {
            for (var i = 0; i < 4; i++) operations.Add(counter.BeginCount("synthetic"));
            await callbackEntered.Task.WaitAsync(Guard); await cpuReturned.Task.WaitAsync(Guard);
            foreach (var operation in operations)
            {
                var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => operation.Caller.WaitAsync(Guard));
                Assert.Equal(BoundedStructuredTextTokenCounter.DeadlineMessage, failure.Message);
                Assert.False(operation.Completion.IsCompleted);
            }
            Assert.Equal(BoundedStructuredTextTokenCounter.CapacityMessage,
                Assert.Throws<InvalidOperationException>(() => new BoundedStructuredTextTokenCounter().BeginCount("fifth")).Message);
        }
        finally
        {
            releaseCallback.TrySetResult();
            await Task.WhenAll(operations.Select(x => x.Completion)).WaitAsync(Guard);
            foreach (var registration in registrations) registration.Dispose();
        }
    }

    [Fact]
    public async Task Private_count_errors_and_invalid_results_are_sanitized()
    {
        foreach (var count in new Func<string, CancellationToken, int>[]
        { (_, _) => throw new IOException("PRIVATE_COUNTER_CANARY"), (_, _) => -1 })
        {
            var operation = new BoundedStructuredTextTokenCounter(count, Guard).BeginCount("PRIVATE_INPUT_CANARY");
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => operation.Caller.WaitAsync(Guard));
            Assert.Equal(BoundedStructuredTextTokenCounter.InvalidMessage, error.Message);
            Assert.Null(error.InnerException);
            Assert.DoesNotContain("PRIVATE", error.ToString());
            await operation.Completion.WaitAsync(Guard);
        }
    }

    [Fact]
    public void Precancelled_caller_and_invalid_deadlines_do_not_dispatch()
    {
        var calls = 0;
        using var caller = new CancellationTokenSource(); caller.Cancel();
        var counter = new BoundedStructuredTextTokenCounter((_, _) => { calls++; return 0; }, Guard);
        Assert.Equal(caller.Token, Assert.Throws<OperationCanceledException>(() => counter.BeginCount("text", caller.Token)).CancellationToken);
        foreach (var timeout in new[] { TimeSpan.Zero, TimeSpan.FromTicks(-1), TimeSpan.FromSeconds(61) })
            Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedStructuredTextTokenCounter((_, _) => 0, timeout));
        Assert.Equal(0, calls);
    }
}
