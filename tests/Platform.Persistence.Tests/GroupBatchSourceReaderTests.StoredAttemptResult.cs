using Microsoft.EntityFrameworkCore;
using System.Reflection;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class GroupBatchSourceReaderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoredAttemptResultRefusesNonSqlProviderBeforeKeysOrWrites(bool persist)
    {
        using var f = new Fixture(); var (plan, chunks, _, response) = await ResultInputAsync(f, false);
        var before = await f.CountsAsync(); var keys = new ResultKeysMustNotResolve();
        var store = new GroupModelAttemptResultStore(f.Auth.Db, f.Worker, f.Auth.Clock, f.Reader,
            new(f.Auth.Db, f.Worker, f.Auth.Clock, f.Keys, new()), keys, new());
        var error = persist
            ? await Assert.ThrowsAsync<InvalidOperationException>(() => store.PersistOriginalAsync(plan, chunks[0], response))
            : await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadOriginalAsync(plan, chunks[0]));
        Assert.Equal("Group model attempt journal is not available.", error.Message);
        Assert.Equal(0, keys.Calls); Assert.Equal(before, await f.CountsAsync());
        Assert.Empty(await f.Auth.Db.GroupModelAttemptResults.ToArrayAsync());
        Assert.Empty(await f.Auth.Db.GroupModelAttemptStarts.ToArrayAsync());
        Assert.Null(f.Auth.Db.Database.CurrentTransaction); Assert.False(f.Auth.Db.ChangeTracker.HasChanges());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoredAttemptResultCancellationPrecedesKeysAndDatabaseEffects(bool persist)
    {
        using var f = new Fixture(); var (plan, chunks, _, response) = await ResultInputAsync(f, false);
        var keys = new ResultKeysMustNotResolve(); using var canceled = new CancellationTokenSource(); canceled.Cancel();
        var store = new GroupModelAttemptResultStore(f.Auth.Db, f.Worker, f.Auth.Clock, f.Reader,
            new(f.Auth.Db, f.Worker, f.Auth.Clock, f.Keys, new()), keys, new());
        if (persist) await Assert.ThrowsAsync<OperationCanceledException>(() => store.PersistOriginalAsync(plan, chunks[0], response, canceled.Token));
        else await Assert.ThrowsAsync<OperationCanceledException>(() => store.ReadOriginalAsync(plan, chunks[0], canceled.Token));
        Assert.Equal(0, keys.Calls); Assert.Empty(await f.Auth.Db.GroupModelAttemptResults.ToArrayAsync());
        Assert.Null(f.Auth.Db.Database.CurrentTransaction); Assert.False(f.Auth.Db.ChangeTracker.HasChanges());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("retained")]
    public async Task StoredResultKeyWaitHasFiniteDefaultDeadlineAndDisposesLateMaterial(string? id)
    {
        using var f = new Fixture(); var (plan, chunks, _, _) = await ResultInputAsync(f, false);
        var original = GroupModelAttemptOriginalBinding.Create(plan, chunks[0]);
        var clock = new ResultDeadlineClock(); var keys = new DeferredResultKeys();
        var store = new GroupModelAttemptResultStore(f.Auth.Db, f.Worker, clock, f.Reader,
            new(f.Auth.Db, f.Worker, f.Auth.Clock, f.Keys, new()), keys, new());
        var pending = ResolveStoredResultKey(store, original, id, default);
        Assert.True(keys.Token.CanBeCanceled); Assert.False(pending.IsCompleted);
        clock.FireDeadline();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(2)));
        var material = new GroupSourceKeyMaterial(id ?? "write", new byte[32]);
        keys.Pending.SetResult(material);
        Assert.Throws<ObjectDisposedException>(() => _ = material.Key.Length);
        Assert.True(clock.Disposed); Assert.Null(f.Auth.Db.Database.CurrentTransaction);
        Assert.False(f.Auth.Db.ChangeTracker.HasChanges());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("retained")]
    public async Task StoredResultKeyWaitPreservesCallerCancellationAndLateDisposal(string? id)
    {
        using var f = new Fixture(); var (plan, chunks, _, _) = await ResultInputAsync(f, false);
        var original = GroupModelAttemptOriginalBinding.Create(plan, chunks[0]);
        var clock = new ResultDeadlineClock(); var keys = new DeferredResultKeys();
        var store = new GroupModelAttemptResultStore(f.Auth.Db, f.Worker, clock, f.Reader,
            new(f.Auth.Db, f.Worker, f.Auth.Clock, f.Keys, new()), keys, new());
        using var canceled = new CancellationTokenSource();
        var pending = ResolveStoredResultKey(store, original, id, canceled.Token);
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(2)));
        var material = new GroupSourceKeyMaterial(id ?? "write", new byte[32]);
        keys.Pending.SetResult(material);
        Assert.Throws<ObjectDisposedException>(() => _ = material.Key.Length);
        Assert.True(clock.Disposed); Assert.Null(f.Auth.Db.Database.CurrentTransaction);
        Assert.False(f.Auth.Db.ChangeTracker.HasChanges());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("retained")]
    public async Task StoredResultKeyWaitHidesProviderFaultAndDisposesDeadline(string? id)
    {
        using var f = new Fixture(); var (plan, chunks, _, _) = await ResultInputAsync(f, false);
        var original = GroupModelAttemptOriginalBinding.Create(plan, chunks[0]);
        var clock = new ResultDeadlineClock(); var keys = new DeferredResultKeys();
        var store = new GroupModelAttemptResultStore(f.Auth.Db, f.Worker, clock, f.Reader,
            new(f.Auth.Db, f.Worker, f.Auth.Clock, f.Keys, new()), keys, new());
        var pending = ResolveStoredResultKey(store, original, id, default);
        keys.Pending.SetException(new InvalidOperationException("PRIVATE_PROVIDER_SENTINEL"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
        Assert.Equal("Group model attempt stored result is unavailable.", error.Message);
        Assert.Null(error.InnerException); Assert.True(clock.Disposed);
    }

    [Fact]
    public async Task StoredResultKeyWaitRefusesNullWithFixedDiagnostic()
    {
        using var f = new Fixture(); var (plan, chunks, _, _) = await ResultInputAsync(f, false);
        var original = GroupModelAttemptOriginalBinding.Create(plan, chunks[0]);
        var clock = new ResultDeadlineClock(); var keys = new DeferredResultKeys();
        var store = new GroupModelAttemptResultStore(f.Auth.Db, f.Worker, clock, f.Reader,
            new(f.Auth.Db, f.Worker, f.Auth.Clock, f.Keys, new()), keys, new());
        var pending = ResolveStoredResultKey(store, original, null, default);
        keys.Pending.SetResult(null!);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
        Assert.Equal("Group model attempt stored result is unavailable.", error.Message);
        Assert.Null(error.InnerException); Assert.True(clock.Disposed);
    }

    private static Task<GroupSourceKeyMaterial> ResolveStoredResultKey(GroupModelAttemptResultStore store,
        GroupModelAttemptOriginalBinding original, string? id, CancellationToken token) =>
        (Task<GroupSourceKeyMaterial>)typeof(GroupModelAttemptResultStore)
            .GetMethod("ResolveKeyAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(store, [original, id, token])!;

    private sealed class DeferredResultKeys : IGroupSourceKeyProvider
    {
        internal readonly TaskCompletionSource<GroupSourceKeyMaterial> Pending = new();
        internal CancellationToken Token;
        public ValueTask<GroupSourceKeyMaterial> ResolveWriteAsync(GroupScope scope, CancellationToken token = default)
        { Token = token; return new(Pending.Task); }
        public ValueTask<GroupSourceKeyMaterial> ResolveReadAsync(GroupScope scope, string id, CancellationToken token = default)
        { Token = token; return new(Pending.Task); }
    }

    private sealed class ResultDeadlineClock : TimeProvider
    {
        private ControlledTimer? timer;
        internal bool Disposed => timer?.Disposed == true;
        internal void FireDeadline() => timer!.Fire();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(TimeSpan.FromMinutes(2), dueTime);
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            return timer = new(callback, state);
        }
        private sealed class ControlledTimer(TimerCallback callback, object? state) : ITimer
        {
            internal bool Disposed;
            internal void Fire() { if (!Disposed) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !Disposed;
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class ResultKeysMustNotResolve : IGroupSourceKeyProvider
    {
        internal int Calls;
        public ValueTask<GroupSourceKeyMaterial> ResolveWriteAsync(GroupScope scope, CancellationToken token = default)
        { Calls++; throw new InvalidOperationException("Unexpected result key resolution."); }
        public ValueTask<GroupSourceKeyMaterial> ResolveReadAsync(GroupScope scope, string id, CancellationToken token = default)
        { Calls++; throw new InvalidOperationException("Unexpected result key resolution."); }
    }
}
