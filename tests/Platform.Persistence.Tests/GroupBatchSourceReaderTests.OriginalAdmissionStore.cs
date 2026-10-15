using System.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class GroupBatchSourceReaderTests
{
    [Theory]
    [InlineData("no-host-preparer")]
    [InlineData("foreign-issuer")]
    [InlineData("forged-decoded")]
    [InlineData("expired")]
    public async Task OriginalAdmissionStoreRefusesNonissuedCarriersBeforeAnySqlOrKey(string change)
    {
        using var f = new Fixture(); var (preparation, brain, selection) = await IssuedDisclosure(f);
        var original = GroupAutomaticInputPlan.CreateV3(preparation, brain, selection, PlanProfile, "model", 64000, []);
        var gate = new SyntheticAdmission(); var preparer = new GroupOriginalAdmissionPreparer(gate, f.Auth.Clock);
        var issued = await preparer.PrepareAsync(original, preparation, brain, []);
        var injecting = change == "no-host-preparer" ? null : change == "foreign-issuer" ? new GroupOriginalAdmissionPreparer(gate, f.Auth.Clock) : preparer;
        if (change == "forged-decoded") issued = new(new object(), GroupAutomaticInputPlan.Read(original.Encode()), preparation, brain,
            GroupOriginalAdmissionSidecar.Read(issued.Sidecar.Encode()), issued.Started, issued.StartedAtUtc, issued.ExpiresAtUtc);
        if (change == "expired") f.Auth.Clock.Current = issued.ExpiresAtUtc;
        var trap = new ModelFenceConnectionTrap(); using var database = V3StoreDatabase(trap);
        var store = new GroupAutomaticInputPlanStore(database, f.Worker, f.Auth.Clock,
            new(database, f.Worker, f.Auth.Clock, f.Keys, new()), new(database, f.Worker, f.Auth.Clock, f.Keys, new()), injecting);
        var keys = f.Keys.Reads;
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.PersistV3WithAdmissionAsync(issued, preparation, brain));
        Assert.Equal(0, trap.Attempts); Assert.Equal(keys, f.Keys.Reads); Assert.Equal(0, gate.Executions);
        Assert.Equal(ConnectionState.Closed, database.Database.GetDbConnection().State);
        Assert.Null(database.Database.CurrentTransaction); Assert.Empty(database.ChangeTracker.Entries());
    }

    [Fact]
    public async Task OriginalAdmissionStoreIssuedCarrierReachesSqlTransactionWithoutProviderOrKeys()
    {
        using var f = new Fixture(); var (preparation, brain, selection) = await IssuedDisclosure(f);
        var original = GroupAutomaticInputPlan.CreateV3(preparation, brain, selection, PlanProfile, "model", 64000, []);
        var gate = new SyntheticAdmission(); var preparer = new GroupOriginalAdmissionPreparer(gate, f.Auth.Clock);
        var issued = await preparer.PrepareAsync(original, preparation, brain, []);
        var trap = new ModelFenceConnectionTrap(); using var database = V3StoreDatabase(trap);
        var store = new GroupAutomaticInputPlanStore(database, f.Worker, f.Auth.Clock,
            new(database, f.Worker, f.Auth.Clock, f.Keys, new()), new(database, f.Worker, f.Auth.Clock, f.Keys, new()), preparer);
        var keys = f.Keys.Reads;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.PersistV3WithAdmissionAsync(issued, preparation, brain));
        Assert.Equal("Model dependency guard attempted SQL.", error.Message);
        Assert.Equal(1, trap.Attempts); Assert.Equal(keys, f.Keys.Reads); Assert.Equal(0, gate.Executions);
        Assert.Null(database.Database.CurrentTransaction); Assert.Empty(database.ChangeTracker.Entries());
        // This sentinel verifies the path only; no real SQL commit is proven.
    }
}
