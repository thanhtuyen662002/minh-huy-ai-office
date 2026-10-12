using System.Data;
using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class GroupBatchSourceReaderTests
{
    [Fact]
    public async Task InputPlanStoreRefusesInMemoryInsteadOfClaimingDurableOriginalSelection()
    {
        using var f = new Fixture(); var source = await f.CommitAsync(); var handle = await f.ClaimAsync();
        var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(handle, [source.MessageId]));
        var brain = await BrainReader(f).ReadAsync(handle, [], []);
        var plan = GroupAutomaticInputPlan.Create(preparation, brain, PlanProfile, "model", 64000, []);
        var store = new GroupAutomaticInputPlanStore(f.Auth.Db, f.Worker, f.Auth.Clock, f.Reader, BrainReader(f));
        var before = await f.CountsAsync(); var keys = f.Keys.Reads;
        var read = await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadAsync(handle));
        var write = await Assert.ThrowsAsync<InvalidOperationException>(() => store.PersistAsync(plan, preparation, brain));
        Assert.Equal("Group original input plan store is not available.", read.Message); Assert.Null(read.InnerException);
        Assert.Equal(read.Message, write.Message); Assert.Null(write.InnerException);
        Assert.Empty(await f.Auth.Db.GroupAutomaticInputPlans.AsNoTracking().ToArrayAsync());
        Assert.Equal(before, await f.CountsAsync()); Assert.Equal(keys, f.Keys.Reads); Assert.False(f.Auth.Db.ChangeTracker.HasChanges());
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("company")]
    [InlineData("service")]
    [InlineData("credential")]
    public async Task InputPlanStoreRefusesReboundWorkerBeforeConnectionOrPrivateRelease(string fault)
    {
        using var f = new Fixture(); var source = await f.CommitAsync(); var handle = await f.ClaimAsync();
        var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(handle, [source.MessageId]));
        var brain = await BrainReader(f).ReadAsync(handle, [], []);
        var plan = GroupAutomaticInputPlan.Create(preparation, brain, PlanProfile, "model", 64000, []);
        var worker = fault switch
        {
            "tenant" => f.Worker with { TenantId = Guid.NewGuid() },
            "company" => f.Worker with { CompanyId = Guid.NewGuid() },
            "service" => f.Worker with { ServiceId = Guid.NewGuid() },
            _ => f.Worker with { CredentialEpoch = f.Worker.CredentialEpoch + 1 }
        };
        var keys = f.Keys.Reads;
        var store = new GroupAutomaticInputPlanStore(f.Auth.Db, worker, f.Auth.Clock, f.Reader, BrainReader(f));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.ReadAsync(handle));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.PersistAsync(plan, preparation, brain));
        Assert.Equal(keys, f.Keys.Reads); Assert.False(f.Auth.Db.ChangeTracker.HasChanges());
    }

    [Theory]
    [InlineData("mars")]
    [InlineData("source-null")]
    [InlineData("brain-null")]
    [InlineData("source-database")]
    [InlineData("brain-database")]
    [InlineData("source-clock")]
    [InlineData("brain-clock")]
    [InlineData("ambient")]
    [InlineData("dirty")]
    public async Task InputPlanStoreRequiresOwnedCleanConsistentSqlUnitBeforeConnection(string fault)
    {
        using var f = new Fixture(); var source = await f.CommitAsync(); var handle = await f.ClaimAsync();
        var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(handle, [source.MessageId]));
        var currentBrain = await BrainReader(f).ReadAsync(handle, [], []);
        var plan = GroupAutomaticInputPlan.Create(preparation, currentBrain, PlanProfile, "model", 64000, []);
        var trap = new ModelFenceConnectionTrap(); var keys = f.Keys.Reads;
        using var database = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlServer("Server=owned.invalid;Initial Catalog=Owned;Integrated Security=true;Pooling=false;MultipleActiveResultSets=" + (fault == "mars"))
            .AddInterceptors(trap).Options);
        GroupBatchSourceReader? sources = new(database, f.Worker, f.Auth.Clock, f.Keys, new());
        GroupBrainCurrentReader? brain = new(database, f.Worker, f.Auth.Clock, f.Keys, new());
        switch (fault)
        {
            case "source-null": sources = null; break;
            case "brain-null": brain = null; break;
            case "source-database": sources = f.Reader; break;
            case "brain-database": brain = BrainReader(f); break;
            case "source-clock": sources = new(database, f.Worker, TimeProvider.System, f.Keys, new()); break;
            case "brain-clock": brain = new(database, f.Worker, TimeProvider.System, f.Keys, new()); break;
            case "dirty":
                database.GroupAutomaticInputPlans.Add(new GroupAutomaticInputPlanRecord
                { TenantId = f.Auth.Scope.TenantId, CompanyId = f.Auth.Scope.CompanyId, BindingId = f.Auth.Scope.SourceBindingId, BatchId = handle.Receipt.BatchId }); break;
        }
        var initiallyDirty = database.ChangeTracker.HasChanges();
        using var ambient = fault == "ambient" ? new TransactionScope(TransactionScopeOption.RequiresNew, TransactionScopeAsyncFlowOption.Enabled) : null;
        var store = new GroupAutomaticInputPlanStore(database, f.Worker, f.Auth.Clock, sources!, brain!);
        var read = await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadAsync(handle));
        var write = await Assert.ThrowsAsync<InvalidOperationException>(() => store.PersistAsync(plan, preparation, currentBrain));
        Assert.Equal("Group original input plan store is not available.", read.Message); Assert.Null(read.InnerException);
        Assert.Equal(read.Message, write.Message); Assert.Null(write.InnerException);
        Assert.Equal(0, trap.Attempts); Assert.Equal(keys, f.Keys.Reads);
        Assert.Equal(ConnectionState.Closed, database.Database.GetDbConnection().State);
        Assert.Equal(initiallyDirty, database.ChangeTracker.HasChanges()); Assert.Null(database.Database.CurrentTransaction);
    }

    [Fact]
    public async Task InputPlanStoreCancellationAndNullInputsRefuseBeforeSql()
    {
        using var f = new Fixture(); var source = await f.CommitAsync(); var handle = await f.ClaimAsync();
        var preparation = GroupBatchSourcePreparation.Create(await f.Reader.ReadAsync(handle, [source.MessageId]));
        var brain = await BrainReader(f).ReadAsync(handle, [], []);
        var plan = GroupAutomaticInputPlan.Create(preparation, brain, PlanProfile, "model", 64000, []);
        var store = new GroupAutomaticInputPlanStore(f.Auth.Db, f.Worker, f.Auth.Clock, f.Reader, BrainReader(f));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.ReadAsync(handle, canceled.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.PersistAsync(plan, preparation, brain, canceled.Token));
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.ReadAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.PersistAsync(null!, preparation, brain));
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.PersistAsync(plan, null!, brain));
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.PersistAsync(plan, preparation, null!));
        Assert.False(f.Auth.Db.ChangeTracker.HasChanges()); Assert.Empty(await f.Auth.Db.GroupAutomaticInputPlans.AsNoTracking().ToArrayAsync());
    }
}
