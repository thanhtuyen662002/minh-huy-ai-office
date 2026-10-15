using System.Data;
using System.Data.Common;
using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class GroupBatchSourceReaderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginalActivityActualFixedQueryReturnsObservationWithoutReadingCheckpointPayload(bool present)
    {
        using var f = new Fixture(); await f.CommitAsync(); var handle = await f.ClaimAsync();
        var interceptor = new ActivityQueryProbe(present);
        using var connection = new BindingStorePermissionTests.SyntheticConnection(1);
        using var database = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlServer(connection).AddInterceptors(interceptor).Options);
        await using var transaction = await database.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        var result = await new GroupOriginalActivityStore(database, f.Worker).IsLegacyActiveLockedAsync(handle, CancellationToken.None);
        Assert.Equal(!present, result); Assert.Equal(1, interceptor.Reads);
        Assert.Single(connection.Commands);
        Assert.Equal(GroupOriginalInvalidationPermissionVerifier.EffectiveVerificationSql, connection.Commands[0]);
        Assert.Same(transaction.GetDbTransaction(), connection.LastTransaction);
        var values = interceptor.Parameters.Select(x => x.Value).ToArray();
        foreach (var id in new[] { handle.Receipt.Scope.TenantId, handle.Receipt.Scope.CompanyId,
            handle.Receipt.Scope.SourceBindingId, handle.Receipt.BatchId }) Assert.Contains(id, values);
        foreach (var key in new[] { "TenantId", "CompanyId", "BindingId", "BatchId" }) Assert.Contains("[" + key + "] =", interceptor.Command);
        Assert.Contains("EXISTS", interceptor.Command);
        Assert.DoesNotContain("SELECT *", interceptor.Command);
        Assert.DoesNotContain("Protected", interceptor.Command);
        Assert.DoesNotContain("PlanSha256", interceptor.Command);
        Assert.Empty(database.ChangeTracker.Entries()); Assert.False(database.ChangeTracker.HasChanges());
        // Synthetic command/EF dispatch only: no SQL authority or lifecycle claim.
    }

    [Fact]
    public async Task OriginalActivityKeepsExistingSharedReadCapabilityWithoutExclusiveUpgrade()
    {
        using var f = new Fixture(); await f.CommitAsync(); var handle = await f.ClaimAsync();
        var probe = new ActivityQueryProbe(true);
        using var connection = new BindingStorePermissionTests.SyntheticConnection(1)
        { ResultForCommand = command => command.Contains("sp_getapplock", StringComparison.Ordinal) ? 0 : 1 };
        using var database = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlServer(connection).AddInterceptors(probe).Options);
        await using var transaction = await database.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        _ = await GroupSourceReadLock.AcquireAsync(database, handle.Receipt.Scope, CancellationToken.None);
        Assert.False(await new GroupOriginalActivityStore(database, f.Worker).IsLegacyActiveLockedAsync(handle, CancellationToken.None));
        Assert.True(GroupSourceReadLock.IsReadTransaction(transaction.GetDbTransaction()));
        Assert.Contains(connection.Commands, x => x.Contains("@LockMode='Shared'", StringComparison.Ordinal));
        Assert.DoesNotContain(connection.Commands, x => x.Contains("@LockMode='Exclusive'", StringComparison.Ordinal));
        Assert.Equal(1, probe.Reads); Assert.False(database.ChangeTracker.HasChanges());
    }

    [Theory]
    [InlineData("no-transaction")]
    [InlineData("read-committed")]
    [InlineData("closed")]
    [InlineData("ambient")]
    [InlineData("dirty")]
    public async Task OriginalActivityRequiresOwnedCleanSerializableUnitBeforeAnyCommand(string fault)
    {
        using var f = new Fixture(); await f.CommitAsync(); var handle = await f.ClaimAsync();
        var probe = new ActivityQueryProbe(false);
        using var connection = new BindingStorePermissionTests.SyntheticConnection(1);
        using var database = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlServer(connection).AddInterceptors(probe).Options);
        await using var transaction = fault == "no-transaction" ? null : await database.Database.BeginTransactionAsync(
            fault == "read-committed" ? System.Data.IsolationLevel.ReadCommitted : System.Data.IsolationLevel.Serializable);
        if (fault == "closed") connection.Close();
        if (fault == "dirty") database.GroupAutomaticInputPlans.Add(new GroupAutomaticInputPlanRecord
        {
            TenantId = handle.Receipt.Scope.TenantId,
            CompanyId = handle.Receipt.Scope.CompanyId,
            BindingId = handle.Receipt.Scope.SourceBindingId,
            BatchId = handle.Receipt.BatchId
        });
        var dirty = database.ChangeTracker.HasChanges();
        using var ambient = fault == "ambient" ? new TransactionScope(TransactionScopeOption.RequiresNew, TransactionScopeAsyncFlowOption.Enabled) : null;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new GroupOriginalActivityStore(database, f.Worker).IsLegacyActiveLockedAsync(handle, CancellationToken.None));
        Assert.Equal("Group original activity is not available.", error.Message); Assert.Null(error.InnerException);
        Assert.Empty(connection.Commands); Assert.Equal(0, probe.Reads); Assert.Equal(dirty, database.ChangeTracker.HasChanges());
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("company")]
    [InlineData("service")]
    [InlineData("credential")]
    public async Task OriginalActivityForeignWorkerRefusesBeforeAnyConnection(string field)
    {
        using var f = new Fixture(); await f.CommitAsync(); var handle = await f.ClaimAsync();
        var worker = field switch
        {
            "tenant" => f.Worker with { TenantId = Guid.NewGuid() },
            "company" => f.Worker with { CompanyId = Guid.NewGuid() },
            "service" => f.Worker with { ServiceId = Guid.NewGuid() },
            _ => f.Worker with { CredentialEpoch = f.Worker.CredentialEpoch + 1 }
        };
        var trap = new ModelFenceConnectionTrap();
        using var database = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlServer("Server=owned.invalid;Initial Catalog=Owned;Integrated Security=true;Pooling=false;MultipleActiveResultSets=false")
            .AddInterceptors(trap).Options);
        var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            new GroupOriginalActivityStore(database, worker).IsLegacyActiveLockedAsync(handle, CancellationToken.None));
        Assert.Equal("Group service access is not available.", error.Message); Assert.Null(error.InnerException);
        Assert.Equal(0, trap.Attempts); Assert.Equal(ConnectionState.Closed, database.Database.GetDbConnection().State);
    }

    [Fact]
    public async Task OriginalActivityNullCancellationAndNonSqlRefuseWithoutNewReads()
    {
        using var f = new Fixture(); await f.CommitAsync(); var handle = await f.ClaimAsync();
        var store = new GroupOriginalActivityStore(f.Auth.Db, f.Worker); var keys = f.Keys.Reads;
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.IsLegacyActiveLockedAsync(null!, CancellationToken.None));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.IsLegacyActiveLockedAsync(handle, cancelled.Token));
        Assert.Equal(cancelled.Token, error.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.IsLegacyActiveLockedAsync(handle, CancellationToken.None));
        Assert.Equal(keys, f.Keys.Reads);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(1L)]
    public async Task OriginalActivityUnsafeEffectiveProofNeverBecomesAbsenceOrActive(object? proof)
    {
        using var f = new Fixture(); await f.CommitAsync(); var handle = await f.ClaimAsync();
        var probe = new ActivityQueryProbe(false);
        using var connection = new BindingStorePermissionTests.SyntheticConnection(proof);
        using var database = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlServer(connection).AddInterceptors(probe).Options);
        await using var transaction = await database.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            new GroupOriginalActivityStore(database, f.Worker).IsLegacyActiveLockedAsync(handle, CancellationToken.None));
        Assert.Equal("Data source is unavailable for authorized use.", error.Message); Assert.Null(error.InnerException);
        Assert.Single(connection.Commands); Assert.Equal(0, probe.Reads);
        Assert.Empty(database.ChangeTracker.Entries()); Assert.False(database.ChangeTracker.HasChanges());
    }

    private sealed class ActivityQueryProbe(bool present) : DbCommandInterceptor
    {
        internal int Reads;
        internal string Command = "";
        internal (string Name, object Value)[] Parameters = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            Assert.Contains("[aioffice].[GroupOriginalInvalidations]", command.CommandText);
            Assert.Equal(System.Data.IsolationLevel.Serializable, command.Transaction!.IsolationLevel);
            Assert.Same(data.Context!.Database.GetDbConnection(), command.Connection);
            Assert.Equal(ConnectionState.Open, command.Connection!.State);
            Command = command.CommandText; Parameters = command.Parameters.Cast<DbParameter>().Select(x => (x.ParameterName, x.Value!)).ToArray();
            Reads++;
            var table = new DataTable(); table.Columns.Add("Value", typeof(bool)); table.Rows.Add(present);
            return ValueTask.FromResult(InterceptionResult<DbDataReader>.SuppressWithResult(table.CreateDataReader()));
        }
    }
}
