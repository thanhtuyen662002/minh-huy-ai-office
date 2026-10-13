extern alias GroupReferenceProof;

using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;
using Proof = GroupReferenceProof::MinhHuy.AIOffice.GroupReference.RuntimeProof.GroupModelAttemptResultRuntimeProof;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

// These exercise EF dispatch and admission with synthetic ADO. Actual SQL
// mode, simultaneous holders and physical expiry writes still require CI.
public sealed class OwnedOriginalResultReadObserverTests
{
    [Fact]
    public async Task Manual_source_lock_does_not_dispatch_an_EF_scalar_interceptor()
    {
        await using var connection = new BindingStorePermissionTests.SyntheticConnection(0);
        var scalar = new ScalarCounter();
        await using var database = Database(connection, scalar);
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        _ = await GroupSourceReadLock.AcquireAsync(database, Scope(), default);
        Assert.Single(connection.Commands); Assert.Equal(0, scalar.Calls);
    }

    [Fact]
    public async Task Actual_EF_result_reader_hook_observes_four_distinct_transactions_before_any_reader_executes()
    {
        using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var cohort = new Proof.OriginalResultReadCohort();
        var scope = Scope();
        var connections = new List<BindingStorePermissionTests.SyntheticConnection>();
        var contexts = new List<PlatformDbContext>();
        var transactions = new List<IDbContextTransaction>();
        try
        {
            for (var index = 0; index < 4; index++)
            {
                var connection = new BindingStorePermissionTests.SyntheticConnection("Shared");
                connections.Add(connection);
                var probe = new Proof.OriginalResultReadProbe(cohort, scope);
                var database = Database(connection, probe); contexts.Add(database);
                probe.Arm(database);
                transactions.Add(await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, bounded.Token));
            }
            var firstThree = contexts.Take(3).Select(database => ReadAsync(database, scope, bounded.Token)).ToArray();
            Assert.All(firstThree, read => Assert.False(read.IsCompleted));
            var reads = firstThree.Append(ReadAsync(contexts[3], scope, bounded.Token)).ToArray();
            // Synthetic provider refuses reader execution after the real EF
            // hook returns. Thus no query is suppressed into a fake success.
            await Task.WhenAll(reads.Select(read => Assert.ThrowsAsync<NotSupportedException>(() => read)));
            cohort.RequireComplete();
            Assert.All(connections, connection =>
            {
                Assert.Single(connection.Commands);
                Assert.Equal("SELECT APPLOCK_MODE('public',@resource,'Transaction');", connection.Commands[0]);
            });
            Assert.Equal(4, connections.Select(connection => connection.LastTransaction).Distinct(ReferenceEqualityComparer.Instance).Count());
        }
        finally
        {
            foreach (var transaction in transactions) await transaction.DisposeAsync();
            foreach (var database in contexts) await database.DisposeAsync();
            foreach (var connection in connections) await connection.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("Exclusive")]
    [InlineData("NoLock")]
    [InlineData("Update")]
    [InlineData("SharedIntentExclusive")]
    [InlineData("Unknown")]
    [InlineData(null)]
    public async Task Result_reader_refuses_every_mode_except_actual_Shared(object? mode)
    {
        await using var connection = new BindingStorePermissionTests.SyntheticConnection(mode);
        var cohort = new Proof.OriginalResultReadCohort(); var scope = Scope();
        var probe = new Proof.OriginalResultReadProbe(cohort, scope);
        await using var database = Database(connection, probe); probe.Arm(database);
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync(database, scope, default));
        Assert.Single(connection.Commands);
        Assert.Throws<InvalidOperationException>(() => cohort.RequireComplete());
    }

    [Theory]
    [InlineData(IsolationLevel.ReadCommitted)]
    [InlineData(IsolationLevel.RepeatableRead)]
    public async Task Result_reader_refuses_weaker_transaction_before_mode_query(IsolationLevel level)
    {
        await using var connection = new BindingStorePermissionTests.SyntheticConnection("Shared");
        var scope = Scope(); var cohort = new Proof.OriginalResultReadCohort();
        var probe = new Proof.OriginalResultReadProbe(cohort, scope);
        await using var database = Database(connection, probe); probe.Arm(database);
        await using var transaction = await database.Database.BeginTransactionAsync(level);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync(database, scope, default));
        Assert.Empty(connection.Commands);
    }

    [Fact]
    public async Task Result_reader_without_owned_transaction_refuses_before_mode_query()
    {
        await using var connection = new BindingStorePermissionTests.SyntheticConnection("Shared");
        var scope = Scope(); var cohort = new Proof.OriginalResultReadCohort();
        var probe = new Proof.OriginalResultReadProbe(cohort, scope);
        await using var database = Database(connection, probe); probe.Arm(database);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync(database, scope, default));
        Assert.Empty(connection.Commands);
    }

    [Fact]
    public async Task Missing_reader_cancellation_drains_cohort_and_cannot_report_complete()
    {
        await using var connection = new BindingStorePermissionTests.SyntheticConnection("Shared");
        await using var database = Database(connection, new ScalarCounter());
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var cohort = new Proof.OriginalResultReadCohort(); using var stopped = new CancellationTokenSource();
        var arrival = cohort.ArriveAsync(transaction.GetDbTransaction(), stopped.Token);
        Assert.False(arrival.IsCompleted); await stopped.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => arrival);
        Assert.Throws<InvalidOperationException>(() => cohort.RequireComplete());
    }

    [Fact]
    public async Task Reusing_one_transaction_cannot_satisfy_four_distinct_readers()
    {
        await using var connection = new BindingStorePermissionTests.SyntheticConnection("Shared");
        await using var database = Database(connection, new ScalarCounter());
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var cohort = new Proof.OriginalResultReadCohort(); using var stopped = new CancellationTokenSource();
        var first = cohort.ArriveAsync(transaction.GetDbTransaction(), stopped.Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => cohort.ArriveAsync(transaction.GetDbTransaction(), stopped.Token));
        await stopped.CancelAsync(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Throws<InvalidOperationException>(() => cohort.RequireComplete());
    }

    private static PlatformDbContext Database(BindingStorePermissionTests.SyntheticConnection connection, IInterceptor interceptor) =>
        new(new DbContextOptionsBuilder<PlatformDbContext>().UseSqlServer(connection).AddInterceptors(interceptor).Options);
    private static Task<Guid[]> ReadAsync(PlatformDbContext database, GroupScope scope, CancellationToken token) =>
        database.Set<GroupModelAttemptResultRecord>().AsNoTracking().Where(row => row.TenantId == scope.TenantId
            && row.CompanyId == scope.CompanyId && row.BindingId == scope.SourceBindingId).Select(row => row.RequestId).ToArrayAsync(token);
    private static GroupScope Scope() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
    private sealed class ScalarCounter : DbCommandInterceptor
    {
        internal int Calls;
        public override ValueTask<object?> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData data,
            object? result, CancellationToken token = default)
        { Calls++; return ValueTask.FromResult(result); }
    }
}
