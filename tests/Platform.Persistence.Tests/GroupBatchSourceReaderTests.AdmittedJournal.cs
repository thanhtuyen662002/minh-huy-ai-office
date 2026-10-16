using System.Data;
using System.Reflection;
using System.Transactions;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class GroupBatchSourceReaderTests
{
    [Theory]
    [InlineData("missing-issuer")]
    [InlineData("foreign-issuer")]
    [InlineData("issuer-database")]
    [InlineData("issuer-clock")]
    [InlineData("mars")]
    [InlineData("dirty")]
    [InlineData("ambient")]
    [InlineData("expired-preparation")]
    [InlineData("utc-rewind")]
    [InlineData("foreign-worker")]
    public async Task AdmittedJournalRejectsBadCompositionOrLifetimeBeforeOpeningSql(string change)
    {
        using var f = new Fixture(); var (plan, restored, sidecar) = await AttemptAdmissionInput(f);
        var trap = new ModelFenceConnectionTrap();
        using var database = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlServer("Server=owned.invalid;Database=owned_admitted_journal;Integrated Security=true;Pooling=false;MultipleActiveResultSets=" + (change == "mars"))
            .AddInterceptors(trap).Options);
        var gate = new AttemptAdmissionGate();
        var issuer = new GroupModelAttemptAdmissionPreparer(database, gate, f.Auth.Clock);
        var issued = await issuer.PrepareAsync(plan, restored, sidecar);
        GroupModelAttemptAdmissionPreparer? injected = change switch
        {
            "missing-issuer" => null,
            "foreign-issuer" => new(database, gate, f.Auth.Clock),
            "issuer-database" => new(f.Auth.Db, gate, f.Auth.Clock),
            "issuer-clock" => new(database, gate, TimeProvider.System),
            _ => issuer
        };
        if (change == "expired-preparation") f.Auth.Clock.Current = issued.ExpiresAtUtc;
        if (change == "utc-rewind") f.Auth.Clock.Current -= TimeSpan.FromTicks(1);
        if (change == "dirty") database.GroupServiceGrants.Add(new GroupServiceGrantRecord
        {
            TenantId = f.Worker.TenantId,
            CompanyId = f.Worker.CompanyId,
            ServiceId = f.Worker.ServiceId,
            BindingId = restored.Current.ModelContext!.Scope.SourceBindingId,
            Capability = GroupServiceCapability.Extract,
            IsEnabled = true
        });
        var worker = change == "foreign-worker" ? f.Worker with { CompanyId = Guid.NewGuid() } : f.Worker;
        using var ambient = change == "ambient" ? new TransactionScope(TransactionScopeAsyncFlowOption.Enabled) : null;
        var journal = new GroupModelAttemptJournalStore(database, worker, f.Auth.Clock,
            new(database, worker, f.Auth.Clock, f.Keys, new()), new(database, worker, f.Auth.Clock, f.Keys, new()), injected);
        var context = issued.Original.Context; var receipt = context.Preparation.Context.Handle.Receipt;
        var permit = new GroupModelAttemptPermit(context, new(context.Scope, context.BatchId,
            GroupModelAttemptBudget.InputIdentity(context), 1, issued.RequestId, null, receipt.OperationId,
            receipt.Epoch, issued.StartedAtUtc, issued.ExpiresAtUtc), issued.Original, issued);
        var keys = f.Keys.Reads;
        if (change == "foreign-worker")
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => journal.TryStartAdmittedOriginalAsync(issued));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => journal.RecordFailureAsync(context, permit, GroupModelAttemptFailure.InvalidOutput));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => journal.TryStartAdmittedOriginalAsync(issued));
            await Assert.ThrowsAsync<InvalidOperationException>(() => journal.RecordFailureAsync(context, permit, GroupModelAttemptFailure.InvalidOutput));
        }
        Assert.Equal(0, trap.Attempts); Assert.Equal(keys, f.Keys.Reads); Assert.Equal(0, gate.Executions);
        Assert.Single(gate.Requests); Assert.Equal(1, gate.Descriptions);
        Assert.Equal(ConnectionState.Closed, database.Database.GetDbConnection().State);
        Assert.Equal(change == "dirty", database.ChangeTracker.HasChanges()); Assert.Null(database.Database.CurrentTransaction);
    }

    [Fact]
    public async Task AdmittedJournalValidEntryRequiresActualSqlAndCannotAppendInMemoryOrExecuteGate()
    {
        using var f = new Fixture(); var (plan, restored, sidecar) = await AttemptAdmissionInput(f);
        var trap = new ModelFenceConnectionTrap(); using var database = V3StoreDatabase(trap);
        var gate = new AttemptAdmissionGate(); var issuer = new GroupModelAttemptAdmissionPreparer(database, gate, f.Auth.Clock);
        var issued = await issuer.PrepareAsync(plan, restored, sidecar);
        var journal = new GroupModelAttemptJournalStore(database, f.Worker, f.Auth.Clock,
            new(database, f.Worker, f.Auth.Clock, f.Keys, new()), new(database, f.Worker, f.Auth.Clock, f.Keys, new()), issuer);
        var keys = f.Keys.Reads;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => journal.TryStartAdmittedOriginalAsync(issued));
        Assert.Equal("Model dependency guard attempted SQL.", error.Message); Assert.Equal(1, trap.Attempts);
        var memoryIssuer = new GroupModelAttemptAdmissionPreparer(f.Auth.Db, gate, f.Auth.Clock);
        var memory = await memoryIssuer.PrepareAsync(plan, restored, sidecar);
        var before = await f.CountsAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new GroupModelAttemptJournalStore(f.Auth.Db, f.Worker, f.Auth.Clock,
            f.Reader, BrainReader(f), memoryIssuer).TryStartAdmittedOriginalAsync(memory));
        Assert.Equal(before, await f.CountsAsync()); Assert.Equal(keys, f.Keys.Reads); Assert.Equal(0, gate.Executions);
        Assert.Equal(ConnectionState.Closed, database.Database.GetDbConnection().State); Assert.Empty(database.ChangeTracker.Entries());
    }

    [Fact]
    public async Task AdmittedJournalCancellationPreemptsDisposedDatabaseAndNullCarrier()
    {
        using var f = new Fixture(); var (plan, restored, sidecar) = await AttemptAdmissionInput(f);
        var trap = new ModelFenceConnectionTrap(); using var database = V3StoreDatabase(trap);
        var gate = new AttemptAdmissionGate(); var issuer = new GroupModelAttemptAdmissionPreparer(database, gate, f.Auth.Clock);
        var issued = await issuer.PrepareAsync(plan, restored, sidecar);
        var journal = new GroupModelAttemptJournalStore(database, f.Worker, f.Auth.Clock,
            new(database, f.Worker, f.Auth.Clock, f.Keys, new()), new(database, f.Worker, f.Auth.Clock, f.Keys, new()), issuer);
        using var caller = new CancellationTokenSource(); caller.Cancel(); database.Dispose();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => journal.TryStartAdmittedOriginalAsync(issued, caller.Token));
        Assert.Equal(caller.Token, error.CancellationToken);
        await Assert.ThrowsAsync<OperationCanceledException>(() => journal.TryStartAdmittedOriginalAsync(null!, caller.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.TryStartAdmittedOriginalAsync(null!));
        Assert.Equal(0, trap.Attempts); Assert.Equal(0, gate.Executions);
    }

    [Fact]
    public async Task LockedAdmissionAuthenticationRequiresOwnedSerializableUnitBeforeSqlOrKeyReads()
    {
        using var f = new Fixture(); var (plan, restored, sidecar) = await AttemptAdmissionInput(f);
        var trap = new ModelFenceConnectionTrap(); using var database = V3StoreDatabase(trap);
        var store = V3Store(f, database); var keys = f.Keys.Reads;
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RequireOriginalAdmissionUnchangedLockedAsync(
            restored.Current.ModelContext!.Preparation.Context.Handle, plan, sidecar, default));
        using var caller = new CancellationTokenSource(); caller.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.RequireOriginalAdmissionUnchangedLockedAsync(
            restored.Current.ModelContext!.Preparation.Context.Handle, plan, sidecar, caller.Token));
        Assert.Equal(0, trap.Attempts); Assert.Equal(keys, f.Keys.Reads); Assert.Equal(ConnectionState.Closed, database.Database.GetDbConnection().State);
    }

    [Fact]
    public async Task AcceptedResultRecoveryIgnoresAnInjectedForeignAdmissionPreparer()
    {
        using var f = new Fixture(); var (plan, restored, _) = await AttemptAdmissionInput(f);
        var trap = new ModelFenceConnectionTrap(); using var database = V3StoreDatabase(trap);
        var gate = new AttemptAdmissionGate(); var foreign = new GroupModelAttemptAdmissionPreparer(f.Auth.Db, gate, f.Auth.Clock);
        var keys = f.Keys.Reads;
        var store = new GroupModelAttemptResultStore(database, f.Worker, f.Auth.Clock,
            new(database, f.Worker, f.Auth.Clock, f.Keys, new()), new(database, f.Worker, f.Auth.Clock, f.Keys, new()), f.Keys, new(), foreign);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadOriginalAsync(plan, restored));
        Assert.Equal("Model dependency guard attempted SQL.", error.Message); Assert.Equal(1, trap.Attempts);
        Assert.Empty(gate.Requests); Assert.Equal(0, gate.Descriptions); Assert.Equal(0, gate.Executions); Assert.Equal(keys, f.Keys.Reads);
        Assert.Equal(ConnectionState.Closed, database.Database.GetDbConnection().State); Assert.Empty(database.ChangeTracker.Entries());
    }

    [Fact]
    public async Task AdmittedJournalRetainsFirstIssuedExpiryObservationEvenWhenSqlAndIssuerUtcRewind()
    {
        using var f = new Fixture(); var (plan, restored, sidecar) = await AttemptAdmissionInput(f);
        var trap = new ModelFenceConnectionTrap(); using var database = V3StoreDatabase(trap);
        var gate = new AttemptAdmissionGate(); var clock = new AdmissionRewindClock(f.Auth.Clock);
        var issuer = new GroupModelAttemptAdmissionPreparer(database, gate, clock);
        var issued = await issuer.PrepareAsync(plan, restored, sidecar);
        var journal = new GroupModelAttemptJournalStore(database, f.Worker, clock,
            new(database, f.Worker, clock, f.Keys, new()), new(database, f.Worker, clock, f.Keys, new()), issuer);
        var method = typeof(GroupModelAttemptJournalStore).GetMethod("CreateIssuedFence", BindingFlags.Instance | BindingFlags.NonPublic)!;
        clock.Observation = issued.Original.Context.Preparation.Context.Handle.Receipt.ExpiresAtUtc; clock.Armed = true;
        var fence = (Func<GroupBatchClaimExpiryObservation?, CancellationToken, GroupBatchClaimExpiryObservation?>)
            method.Invoke(journal, [issued, issued.Original.Context, CancellationToken.None])!;
        Assert.True(clock.GetUtcNow() < clock.Observation);
        var first = fence(null, default); Assert.NotNull(first); Assert.Equal(clock.Observation, first.ObservedAtUtc);
        var later = new GroupBatchClaimExpiryObservation(first.Handle, first.ObservedAtUtc.AddSeconds(1));
        Assert.Same(first, fence(later, default)); Assert.Same(first, fence(null, default));
        Assert.Equal(0, trap.Attempts); Assert.Single(gate.Requests); Assert.Equal(1, gate.Descriptions); Assert.Equal(0, gate.Executions);
        // Reflection observes this private closure only. It does not execute
        // a SQL transaction, authenticate a claim or qualify retirement.
    }

    [Fact]
    public async Task AdmittedCompanionFieldsAreDerivedFromTheExactIssuedCarrier()
    {
        using var f = new Fixture(); var (plan, restored, sidecar) = await AttemptAdmissionInput(f);
        var gate = new AttemptAdmissionGate(); var issuer = new GroupModelAttemptAdmissionPreparer(f.Auth.Db, gate, f.Auth.Clock);
        var issued = await issuer.PrepareAsync(plan, restored, sidecar);
        var method = typeof(GroupModelAttemptJournalStore).GetMethod("AdmissionRecord", BindingFlags.Static | BindingFlags.NonPublic)!;
        var row = (GroupModelAttemptAdmissionRecord)method.Invoke(null, [issued])!;
        Assert.Equal(issued.Original.Context.Scope.TenantId, row.TenantId); Assert.Equal(issued.Original.Context.Scope.CompanyId, row.CompanyId);
        Assert.Equal(issued.Original.Context.Scope.SourceBindingId, row.BindingId); Assert.Equal(issued.Original.Context.BatchId, row.BatchId);
        Assert.Equal(issued.RequestId, row.RequestId); Assert.Equal(plan.Sha256, row.PlanSha256); Assert.Equal(sidecar.Sha256, row.SidecarSha256);
        Assert.Equal(restored.Original.Ordinal, row.ChunkOrdinal); Assert.Equal(restored.Original.OperationId, row.ChunkOperationId);
        Assert.Equal(restored.Original.InputSha256, row.InputSha256); Assert.Equal(1, row.FormatVersion);
        Assert.Equal(issued.StartedAtUtc, row.PreparedAtUtc); Assert.Equal(issued.ExpiresAtUtc, row.ExpiresAtUtc);
        Assert.Equal(issued.Snapshot.SnapshotSha256, row.SnapshotSha256);
        Assert.Equal(issued.Snapshot.SnapshotSha256, GroupStructuredAdmissionSnapshotCodec.Read(row.Snapshot).SnapshotSha256);
        Assert.Equal(0, gate.Executions);
        // Derived metadata is not a SQL receipt or a provider capability.
    }
}
