extern alias GroupReferenceProof;

using System.Data;
using System.Data.Common;
using System.Reflection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;
using Proof = GroupReferenceProof::MinhHuy.AIOffice.GroupReference.RuntimeProof.GroupOriginalAdmissionRuntimeProof;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class GroupBatchSourceReaderTests
{
    [Theory]
    [InlineData("tenant")]
    [InlineData("company")]
    [InlineData("service")]
    [InlineData("credential")]
    public async Task OriginalAdmissionReadRefusesForeignWorkerReceiptBeforeSqlOrKeys(string change)
    {
        using var f = new Fixture(); var (preparation, brain, selection) = await IssuedDisclosure(f);
        var original = GroupAutomaticInputPlan.CreateV3(preparation, brain, selection, PlanProfile, "model", 64000, []);
        var handle = preparation.Context.Handle;
        var receipt = change switch
        {
            "tenant" => handle.Receipt with { Scope = handle.Receipt.Scope with { TenantId = Guid.NewGuid() } },
            "company" => handle.Receipt with { Scope = handle.Receipt.Scope with { CompanyId = Guid.NewGuid() } },
            "service" => handle.Receipt with { ServiceId = Guid.NewGuid() },
            _ => handle.Receipt with { CredentialEpoch = checked(handle.Receipt.CredentialEpoch + 1) }
        };
        var foreign = new GroupBatchClaimHandle(receipt, handle.Authority);
        var trap = new ModelFenceConnectionTrap(); using var database = V3StoreDatabase(trap);
        var keys = f.Keys.Reads;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => V3Store(f, database).ReadOriginalAdmissionAsync(foreign, original));
        Assert.Equal(0, trap.Attempts); Assert.Equal(keys, f.Keys.Reads);
        Assert.Equal(ConnectionState.Closed, database.Database.GetDbConnection().State);
        Assert.Null(database.Database.CurrentTransaction); Assert.Empty(database.ChangeTracker.Entries());
    }

    [Fact]
    public async Task OriginalAdmissionReadCancellationAndNullRefuseBeforeSqlOrKeys()
    {
        using var f = new Fixture(); var (preparation, brain, selection) = await IssuedDisclosure(f);
        var original = GroupAutomaticInputPlan.CreateV3(preparation, brain, selection, PlanProfile, "model", 64000, []);
        var trap = new ModelFenceConnectionTrap(); using var database = V3StoreDatabase(trap);
        var store = V3Store(f, database); var keys = f.Keys.Reads;
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.ReadOriginalAdmissionAsync(preparation.Context.Handle, original, cancelled.Token));
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.ReadOriginalAdmissionAsync(null!, original));
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.ReadOriginalAdmissionAsync(preparation.Context.Handle, null!));
        Assert.Equal(0, trap.Attempts); Assert.Equal(keys, f.Keys.Reads);
        Assert.Equal(ConnectionState.Closed, database.Database.GetDbConnection().State);
        Assert.Null(database.Database.CurrentTransaction); Assert.Empty(database.ChangeTracker.Entries());
    }

    [Fact]
    public async Task OriginalAdmissionReadValidEntryReachesSqlWithoutResolvingKeys()
    {
        using var f = new Fixture(); var (preparation, brain, selection) = await IssuedDisclosure(f);
        var original = GroupAutomaticInputPlan.CreateV3(preparation, brain, selection, PlanProfile, "model", 64000, []);
        var trap = new ModelFenceConnectionTrap(); using var database = V3StoreDatabase(trap);
        var keys = f.Keys.Reads;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => V3Store(f, database)
            .ReadOriginalAdmissionAsync(preparation.Context.Handle, original));
        Assert.Equal("Model dependency guard attempted SQL.", error.Message);
        Assert.Equal(1, trap.Attempts); Assert.Equal(keys, f.Keys.Reads);
        Assert.Equal(ConnectionState.Closed, database.Database.GetDbConnection().State);
        Assert.Null(database.Database.CurrentTransaction); Assert.Empty(database.ChangeTracker.Entries());
        // This sentinel verifies entry validation only. No actual SQL read or cross-scope isolation is qualified.
    }

    [Fact]
    public async Task OriginalAdmissionReadProbeAcceptsActualProviderGeneratedSingleQueryBeforeNetworkExecution()
    {
        using var f = new Fixture(); var (preparation, _, _) = await IssuedDisclosure(f);
        var handle = preparation.Context.Handle;
        var open = new TranslationOpenProbe(); var command = new TranslationCommandProbe(handle);
        using var database = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlServer("Server=owned.invalid;Initial Catalog=Owned;Integrated Security=true;Pooling=false;MultipleActiveResultSets=false")
            .AddInterceptors(open, command).Options);
        var store = V3Store(f, database);
        var method = typeof(GroupAutomaticInputPlanStore).GetMethod("ReadAdmissionLockedAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException();
        var task = (Task)(method.Invoke(store, [handle, CancellationToken.None]) ?? throw new InvalidOperationException());
        await Assert.ThrowsAsync<TranslationReached>(() => task);
        Assert.Equal(1, open.Attempts); Assert.Equal(1, command.Attempts);
        Assert.Equal(ConnectionState.Closed, database.Database.GetDbConnection().State);
        // Reflection bypasses authority exclusively to observe the real query translation.
        // Opening and execution are suppressed. This is NOT an actual SQL/authority/isolation proof.
    }

    private sealed class TranslationReached : Exception;
    private sealed class TranslationOpenProbe : DbConnectionInterceptor
    {
        internal int Attempts;
        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData data,
            InterceptionResult result, CancellationToken token = default)
        { Attempts++; return ValueTask.FromResult(InterceptionResult.Suppress()); }
    }
    private sealed class TranslationCommandProbe(GroupBatchClaimHandle handle) : DbCommandInterceptor
    {
        internal int Attempts;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken token = default)
        {
            Attempts++;
            Assert.IsType<SqlCommand>(command);
            Assert.Equal(4, Proof.ReadSelectorParameters((SqlCommand)command, handle.Receipt.Scope, handle.Receipt.BatchId).Count);
            throw new TranslationReached();
        }
    }
}
