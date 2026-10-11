using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class GroupBatchSourceReaderTests
{
    [Theory]
    [InlineData("MultipleActiveResultSets=owned-private-marker")]
    [InlineData("Connect Timeout=owned-private-marker")]
    [InlineData("Connect Timeout=2147483648")]
    [InlineData("UnknownOption=owned-private-marker")]
    public async Task AutomaticBatchDependencyStoreMalformedConnectionOptionsExposeOnlyFixedErrorBeforeConnection(string option)
    {
        using var f = new Fixture(); await f.CommitAsync(); var handle = await f.ClaimAsync();
        using var database = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlServer("Server=127.0.0.1,1;Database=never_connect;Integrated Security=true;Encrypt=true;" + option).Options);
        var source = new GroupBatchSourceReader(database, f.Worker, f.Auth.Clock, f.Keys, new());
        var brain = new GroupBrainCurrentReader(database, f.Worker, f.Auth.Clock, f.Keys, new());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new GroupAutomaticBatchDependencyStore(database, f.Worker, f.Auth.Clock, source, brain).RequireCurrentAsync(handle));
        Assert.Equal("Automatic group batch dependencies are not available.", error.Message); Assert.Null(error.InnerException);
        Assert.DoesNotContain("owned-private-marker", error.ToString()); Assert.Equal(0, f.Keys.Reads);
        Assert.False(database.ChangeTracker.HasChanges()); Assert.Null(database.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticBatchDependencyStoreRefusesNonSqlAndMarsBeforeKeysOrConnection(bool mars)
    {
        using var f = new Fixture(); await f.CommitAsync(); var handle = await f.ClaimAsync();
        var counts = await f.CountsAsync(); var reads = f.Keys.Reads;
        using var alternate = mars ? new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlServer("Server=127.0.0.1,1;Database=never_connect;Integrated Security=true;Encrypt=true;Connect Timeout=1;MultipleActiveResultSets=true").Options) : null;
        var database = alternate ?? f.Auth.Db;
        var source = new GroupBatchSourceReader(database, f.Worker, f.Auth.Clock, f.Keys, new());
        var brain = new GroupBrainCurrentReader(database, f.Worker, f.Auth.Clock, f.Keys, new());
        var store = new GroupAutomaticBatchDependencyStore(database, f.Worker, f.Auth.Clock, source, brain);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.RequireCurrentAsync(handle));
        Assert.Equal("Automatic group batch dependencies are not available.", error.Message); Assert.Null(error.InnerException);
        Assert.Equal(reads, f.Keys.Reads); Assert.Equal(counts, await f.CountsAsync());
        Assert.Null((await f.Auth.Db.GroupBatchClaimStates.AsNoTracking().SingleAsync()).ExpiryObservedAtUtc);
        Assert.Null(database.Database.CurrentTransaction); Assert.False(database.ChangeTracker.HasChanges());
        if (alternate is not null) Assert.Equal(System.Data.ConnectionState.Closed, alternate.Database.GetDbConnection().State);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("tenant")]
    [InlineData("company")]
    [InlineData("service")]
    [InlineData("credential")]
    public async Task AutomaticBatchDependencyStoreForeignWorkerAndCancellationRefuseBeforeDisposedDatabase(string change)
    {
        using var f = new Fixture(); await f.CommitAsync(); var handle = await f.ClaimAsync(); var worker = f.Worker;
        using var canceled = new CancellationTokenSource();
        switch (change)
        {
            case "cancel": canceled.Cancel(); break;
            case "tenant": worker = worker with { TenantId = Guid.NewGuid() }; break;
            case "company": worker = worker with { CompanyId = Guid.NewGuid() }; break;
            case "service": worker = worker with { ServiceId = Guid.NewGuid() }; break;
            case "credential": worker = worker with { CredentialEpoch = worker.CredentialEpoch + 1 }; break;
        }
        f.Auth.Db.Dispose();
        var store = new GroupAutomaticBatchDependencyStore(f.Auth.Db, worker, f.Auth.Clock, f.Reader, BrainReader(f));
        if (change == "cancel") await Assert.ThrowsAsync<OperationCanceledException>(() => store.RequireCurrentAsync(handle, canceled.Token));
        else await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.RequireCurrentAsync(handle));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticBatchDependencyStoreMismatchedReaderContextRefusesBeforeDisposedDatabase(bool changeBrain)
    {
        using var f = new Fixture(); await f.CommitAsync(); var handle = await f.ClaimAsync();
        using var alternate = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var source = changeBrain ? f.Reader : new GroupBatchSourceReader(alternate, f.Worker, f.Auth.Clock, f.Keys, new());
        var brain = changeBrain ? new GroupBrainCurrentReader(alternate, f.Worker, f.Auth.Clock, f.Keys, new()) : BrainReader(f);
        f.Auth.Db.Dispose();
        var store = new GroupAutomaticBatchDependencyStore(f.Auth.Db, f.Worker, f.Auth.Clock, source, brain);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RequireCurrentAsync(handle)); Assert.Equal(0, f.Keys.Reads);
    }

    [Theory]
    [InlineData(false, "worker")]
    [InlineData(true, "worker")]
    [InlineData(false, "clock")]
    [InlineData(true, "clock")]
    [InlineData(false, "missing")]
    [InlineData(true, "missing")]
    public async Task AutomaticBatchDependencyStoreReaderAffinityAndMissingDependenciesRefuseBeforeDisposedDatabase(bool changeBrain, string change)
    {
        using var f = new Fixture(); await f.CommitAsync(); var handle = await f.ClaimAsync();
        var worker = change == "worker" ? f.Worker with { ServiceId = Guid.NewGuid() } : f.Worker;
        var clock = change == "clock" ? TimeProvider.System : f.Auth.Clock;
        var source = changeBrain ? f.Reader : change == "missing" ? null! : new GroupBatchSourceReader(f.Auth.Db, worker, clock, f.Keys, new());
        var brain = changeBrain ? change == "missing" ? null! : new GroupBrainCurrentReader(f.Auth.Db, worker, clock, f.Keys, new()) : BrainReader(f);
        var reads = f.Keys.Reads; f.Auth.Db.Dispose();
        var store = new GroupAutomaticBatchDependencyStore(f.Auth.Db, f.Worker, f.Auth.Clock, source, brain);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.RequireCurrentAsync(handle));
        Assert.Equal("Automatic group batch dependencies are not available.", error.Message);
        Assert.Null(error.InnerException); Assert.Equal(reads, f.Keys.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticBatchDependencyStoreDirtyTrackerAndAmbientTransactionRefuseBeforeOpeningSql(bool ambient)
    {
        using var f = new Fixture(); await f.CommitAsync(); var handle = await f.ClaimAsync();
        using var database = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlServer("Server=127.0.0.1,1;Database=never_connect;Integrated Security=true;Encrypt=true;Connect Timeout=1").Options);
        using var transaction = ambient ? new System.Transactions.TransactionScope(System.Transactions.TransactionScopeAsyncFlowOption.Enabled) : null;
        if (!ambient) database.GroupSourceStates.Add(new() { TenantId = f.Worker.TenantId, CompanyId = f.Worker.CompanyId, BindingId = handle.Receipt.Scope.SourceBindingId });
        var source = new GroupBatchSourceReader(database, f.Worker, f.Auth.Clock, f.Keys, new());
        var brain = new GroupBrainCurrentReader(database, f.Worker, f.Auth.Clock, f.Keys, new());
        var store = new GroupAutomaticBatchDependencyStore(database, f.Worker, f.Auth.Clock, source, brain);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.RequireCurrentAsync(handle));
        Assert.Equal("Automatic group batch dependencies are not available.", error.Message); Assert.Null(error.InnerException);
        Assert.Equal(0, f.Keys.Reads); Assert.Null(database.Database.CurrentTransaction);
        Assert.Equal(!ambient, database.ChangeTracker.HasChanges());
        Assert.Equal(System.Data.ConnectionState.Closed, database.Database.GetDbConnection().State);
        transaction?.Complete();
    }
}
