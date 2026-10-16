using System.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class GroupBatchSourceReaderTests
{
    [Theory]
    [InlineData(2, 2, true)]
    [InlineData(3, 3, true)]
    [InlineData(2, 3, false)]
    [InlineData(3, 2, false)]
    [InlineData(1, 1, false)]
    [InlineData(4, 4, false)]
    [InlineData(0, 0, false)]
    [InlineData(-1, -1, false)]
    [InlineData(2, 1, false)]
    [InlineData(3, 4, false)]
    public void InputPlanStoreV3BindsOnlyClosedMatchingStoredRowAndWireVersions(int row, int wire, bool accepted)
    {
        if (accepted) GroupAutomaticInputPlanStore.RequireDecodedFormat(row, wire);
        else
        {
            var error = Assert.Throws<InvalidOperationException>(() => GroupAutomaticInputPlanStore.RequireDecodedFormat(row, wire));
            Assert.Equal("Group original input plan store is not available.", error.Message); Assert.Null(error.InnerException);
        }
    }

    [Theory]
    [InlineData("decoded")]
    [InlineData("legacy")]
    [InlineData("foreign-handle")]
    [InlineData("rebound-carrier")]
    public async Task InputPlanStoreV3ExplicitNewEntryRefusesStructuralOrForeignCarriersBeforeSql(string kind)
    {
        using var f = new Fixture(); var (preparation, brain, selection) = await IssuedDisclosure(f);
        var original = GroupAutomaticInputPlan.CreateV3(preparation, brain, selection, PlanProfile, "model", 64000, []);
        var plan = kind == "decoded" ? GroupAutomaticInputPlan.Read(original.Encode())
            : kind == "legacy" ? GroupAutomaticInputPlan.Create(preparation, brain, PlanProfile, "model", 64000, []) : original;
        if (kind is "foreign-handle" or "rebound-carrier")
        {
            var current = new GroupBatchClaimHandle(preparation.Context.Handle.Receipt with { OperationId = Guid.NewGuid() }, preparation.Context.Handle.Authority);
            brain = new GroupBrainPrivateContext(current, brain.Snapshots.ToArray(), brain.Items.ToArray());
            if (kind == "rebound-carrier") preparation = GroupBatchSourcePreparation.Create(new(current, preparation.Context.AllocatedThroughSequence,
                preparation.Context.Coverage, preparation.Context.Snapshots.ToArray(), preparation.Context.Items.ToArray()));
        }
        var trap = new ModelFenceConnectionTrap(); var keys = f.Keys.Reads;
        using var database = V3StoreDatabase(trap);
        var store = V3Store(f, database);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.PersistV3Async(plan, preparation, brain));
        Assert.Equal("Group original input plan store is not available.", error.Message); Assert.Null(error.InnerException);
        Assert.Equal(0, trap.Attempts); Assert.Equal(keys, f.Keys.Reads);
        Assert.Equal(ConnectionState.Closed, database.Database.GetDbConnection().State);
        Assert.Null(database.Database.CurrentTransaction); Assert.Empty(database.ChangeTracker.Entries());
    }

    [Fact]
    public async Task InputPlanStoreV3IssuedCarrierReachesExistingSqlFenceWithoutResolvingKeysOrCallingModel()
    {
        using var f = new Fixture(); var (preparation, brain, selection) = await IssuedDisclosure(f);
        var plan = GroupAutomaticInputPlan.CreateV3(preparation, brain, selection, PlanProfile, "model", 64000, []);
        plan.RequireOriginalCarrier(preparation, brain);
        var trap = new ModelFenceConnectionTrap(); var keys = f.Keys.Reads;
        using var database = V3StoreDatabase(trap);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => V3Store(f, database).PersistV3Async(plan, preparation, brain));
        Assert.Equal("Model dependency guard attempted SQL.", error.Message);
        Assert.Equal(1, trap.Attempts); Assert.Equal(keys, f.Keys.Reads);
        Assert.Equal(ConnectionState.Closed, database.Database.GetDbConnection().State);
        Assert.Null(database.Database.CurrentTransaction); Assert.Empty(database.ChangeTracker.Entries());
        // A connection interceptor sentinel is not a successful real SQL write.
    }

    [Fact]
    public async Task InputPlanStoreV3CancellationAndNullRefuseBeforeSql()
    {
        using var f = new Fixture(); var (preparation, brain, selection) = await IssuedDisclosure(f);
        var plan = GroupAutomaticInputPlan.CreateV3(preparation, brain, selection, PlanProfile, "model", 64000, []);
        var trap = new ModelFenceConnectionTrap(); using var database = V3StoreDatabase(trap); var store = V3Store(f, database);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.PersistV3Async(plan, preparation, brain, cancelled.Token));
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.PersistV3Async(null!, preparation, brain));
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.PersistV3Async(plan, null!, brain));
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.PersistV3Async(plan, preparation, null!));
        Assert.Equal(0, trap.Attempts); Assert.Equal(ConnectionState.Closed, database.Database.GetDbConnection().State);
        Assert.Null(database.Database.CurrentTransaction); Assert.Empty(database.ChangeTracker.Entries());
    }

    private static PlatformDbContext V3StoreDatabase(ModelFenceConnectionTrap trap) => new(new DbContextOptionsBuilder<PlatformDbContext>()
        .UseSqlServer("Server=owned.invalid;Initial Catalog=Owned;Integrated Security=true;Pooling=false;MultipleActiveResultSets=false")
        .AddInterceptors(trap).Options);

    private static GroupAutomaticInputPlanStore V3Store(Fixture f, PlatformDbContext database) => new(database, f.Worker, f.Auth.Clock,
        new GroupBatchSourceReader(database, f.Worker, f.Auth.Clock, f.Keys, new()),
        new GroupBrainCurrentReader(database, f.Worker, f.Auth.Clock, f.Keys, new()));
}
