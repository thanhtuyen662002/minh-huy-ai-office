extern alias GroupReferenceProof;

using System.Data;
using System.Data.Common;
using System.Reflection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;
using ContextProof = GroupReferenceProof::MinhHuy.AIOffice.GroupReference.RuntimeProof.GroupAutomaticModelContextRuntimeProof;
using JournalProof = GroupReferenceProof::MinhHuy.AIOffice.GroupReference.RuntimeProof.GroupModelAttemptJournalRuntimeProof;
using DependencyProof = GroupReferenceProof::MinhHuy.AIOffice.GroupReference.RuntimeProof.GroupAutomaticDependencyRuntimeProof;
using NoteProof = GroupReferenceProof::MinhHuy.AIOffice.GroupReference.RuntimeProof.GroupNoteRuntimeProof;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

// Actual observer callbacks and EF ChangeTracker, with synthetic branch flags.
// No SQL connection/lock/savepoint/commit/provider proof is claimed here.
public sealed class GroupOwnedObserverRegressionTests
{
    public static IEnumerable<object[]> MaterialNames()
    {
        foreach (var table in new[] { "GroupMessages", "GroupMessageRevisions", "GroupIngressReceipts",
            "GroupCustomerRequests", "GroupRequestRevisions", "GroupRequestEvidence" })
            foreach (var schemaBracketed in new[] { false, true })
                foreach (var tableBracketed in new[] { false, true })
                    yield return new object[] { table, schemaBracketed, tableBracketed };
    }

    [Theory]
    [MemberData(nameof(MaterialNames))]
    public async Task ActualReadObserverRecognizesRawAndComposedNamesAndStillRequiresTransaction(string table, bool schemaBracketed, bool tableBracketed)
    {
        var schema = schemaBracketed ? "[aioffice]" : "aioffice";
        var name = tableBracketed ? "[" + table + "]" : table;
        var sql = "SELECT * FROM (SELECT TOP (1) * FROM " + schema + "." + name + ") AS [composed]";
        Assert.Equal(new[] { table }, ContextProof.MaterialTables(sql));
        var evidence = new ContextProof.Evidence(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())) { Armed = true };
        using var command = new SqlCommand(sql);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new ContextProof.ReadProbe(evidence).ReaderExecutingAsync(command, null!, default));
        Assert.Empty(evidence.Observed);
    }

    [Theory]
    [InlineData("SELECT * FROM other.GroupMessages")]
    [InlineData("SELECT * FROM aioffice.GroupMessagesSuffix")]
    [InlineData("SELECT * FROM aioffice.GroupMessage")]
    [InlineData("SELECT GroupMessages")]
    public async Task UnrelatedCommandsDoNotInventMaterialObservation(string sql)
    {
        Assert.Empty(ContextProof.MaterialTables(sql));
        var evidence = new ContextProof.Evidence(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())) { Armed = true };
        using var command = new SqlCommand(sql);
        await new ContextProof.ReadProbe(evidence).ReaderExecutingAsync(command, null!, default);
        Assert.Empty(evidence.Observed);
    }

    [Theory]
    [InlineData("SELECT CASE WHEN DATALENGTH(ProtectedContent) BETWEEN 30 AND 64029 THEN ProtectedContent END AS ProtectedContent FROM aioffice.GroupRequestRevisions")]
    [InlineData("SELECT CASE\nWHEN DATALENGTH([ProtectedContent]) BETWEEN 30 AND 64029 THEN [ProtectedContent] END AS [ProtectedContent] FROM [aioffice].[GroupRequestRevisions]")]
    public void CurrentBoundedBrainProjectionObservesBothCapAndCiphertext(string sql)
    {
        var evidence = new ContextProof.Evidence(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));
        evidence.Observed.UnionWith(new[] { "GroupMessages", "GroupMessageRevisions", "GroupIngressReceipts",
            "GroupCustomerRequests", "GroupRequestRevisions", "GroupRequestEvidence" });
        // The old observer's DATALENGTH-only branch leaves Payload false.
        evidence.Metadata = true;
        Assert.Throws<InvalidOperationException>(() => evidence.RequireComplete(true));
        Assert.True(ContextProof.HasBoundedRevisionProjection(sql));
        evidence.ObserveRevision(sql);
        Assert.True(evidence.Metadata); Assert.True(evidence.Payload);
        evidence.RequireComplete(true);
    }

    [Theory]
    [InlineData("SELECT DATALENGTH(ProtectedContent) AS Length FROM aioffice.GroupRequestRevisions")]
    [InlineData("SELECT CASE WHEN DATALENGTH(ProtectedContent) BETWEEN 0 AND 64029 THEN ProtectedContent END AS ProtectedContent FROM aioffice.GroupRequestRevisions")]
    [InlineData("SELECT CASE WHEN DATALENGTH(ProtectedContent) BETWEEN 30 AND 64030 THEN ProtectedContent END AS ProtectedContent FROM aioffice.GroupRequestRevisions")]
    [InlineData("SELECT CASE WHEN DATALENGTH(OtherContent) BETWEEN 30 AND 64029 THEN ProtectedContent END AS ProtectedContent FROM aioffice.GroupRequestRevisions")]
    [InlineData("SELECT CASE WHEN DATALENGTH(ProtectedContent) BETWEEN 30 AND 64029 THEN OtherContent END AS ProtectedContent FROM aioffice.GroupRequestRevisions")]
    [InlineData("SELECT CASE WHEN DATALENGTH(ProtectedContent) BETWEEN 30 AND 64029 THEN ProtectedContent ELSE OtherContent END AS ProtectedContent FROM aioffice.GroupRequestRevisions")]
    [InlineData("SELECT CASE WHEN DATALENGTH(ProtectedContent) BETWEEN 30 AND 64029 THEN ProtectedContent END AS OtherContent FROM aioffice.GroupRequestRevisions")]
    public void LengthMetadataAndMalformedBoundsCannotInventPayloadObservation(string sql)
    {
        var evidence = new ContextProof.Evidence(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));
        Assert.False(ContextProof.HasBoundedRevisionProjection(sql));
        evidence.ObserveRevision(sql);
        Assert.True(evidence.Metadata); Assert.False(evidence.Payload);
        Assert.Throws<InvalidOperationException>(() => evidence.RequireComplete(true));
    }

    [Fact]
    public void SeparatePayloadStillRequiresPriorMetadataAndBothHistoryObservations()
    {
        var evidence = new ContextProof.Evidence(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));
        const string payload = "SELECT ProtectedContent FROM aioffice.GroupRequestRevisions";
        Assert.Throws<InvalidOperationException>(() => evidence.ObserveRevision(payload));
        Assert.False(evidence.Metadata); Assert.False(evidence.Payload);
        evidence.ObserveRevision("SELECT DATALENGTH(ProtectedContent) FROM aioffice.GroupRequestRevisions");
        Assert.True(evidence.Metadata); Assert.False(evidence.Payload);
        evidence.ObserveRevision(payload);
        Assert.True(evidence.Payload);
        Assert.Throws<InvalidOperationException>(() => evidence.RequireComplete(true)); // The other five tables are still mandatory.
    }

    [Fact]
    public async Task BoundedProjectionStillCannotBypassActualTransactionGuard()
    {
        var evidence = new ContextProof.Evidence(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())) { Armed = true };
        using var command = new SqlCommand("SELECT CASE WHEN DATALENGTH(ProtectedContent) BETWEEN 30 AND 64029 THEN ProtectedContent END AS ProtectedContent FROM aioffice.GroupRequestRevisions");
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new ContextProof.ReadProbe(evidence).ReaderExecutingAsync(command, null!, default));
        Assert.Empty(evidence.Observed); Assert.False(evidence.Metadata); Assert.False(evidence.Payload);
    }

    [Fact]
    public async Task DetachedActualSavedWitnessClassifiesAfterExplicitPrecommitObservationAndRewindRecheck()
    {
        using var f = new WitnessFixture();
        await f.PersistActualWitnessAsync();
        Assert.Empty(f.Db.ChangeTracker.Entries());
        Assert.True(f.Evidence.MatchesOriginal(await f.Db.GroupBatchClaimStates.AsNoTracking().SingleAsync()));
        f.Evidence.WitnessSaved = true; f.Evidence.WitnessCommitChecks = 1;
        var probe = new JournalProof.JournalTransactionProbe(f.Evidence);
        await probe.TransactionCommittedAsync(f.Transaction, f.EndEvent);
        Assert.True(f.Evidence.WitnessOnly); Assert.Equal(1, f.Evidence.WitnessCommits);
        await Assert.ThrowsAsync<InvalidOperationException>(() => probe.TransactionCommittedAsync(f.Transaction, f.EndEvent));
        f.Clock.Current = f.Claim.IssuedAtUtc;
        await f.PersistActualWitnessAsync(); // Actual already-retired path: no save or reattachment.
        Assert.Empty(f.Db.ChangeTracker.Entries());
        Assert.Equal(f.Claim.ExpiresAtUtc, (await f.Db.GroupBatchClaimStates.AsNoTracking().SingleAsync()).ExpiryObservedAtUtc);
        f.Evidence.WitnessCommitChecks = 2; // Synthetic only: real SQL recheck is mandatory in native callback.
        await probe.TransactionCommittedAsync(f.Transaction, f.EndEvent);
        Assert.Equal(2, f.Evidence.WitnessCommits);
    }

    [Theory]
    [InlineData("missing-save")]
    [InlineData("missing-precommit")]
    [InlineData("missing-rollback")]
    [InlineData("missing-flush")]
    [InlineData("pending-save")]
    [InlineData("tracked-witness")]
    [InlineData("dirty-witness")]
    [InlineData("residual-start")]
    [InlineData("residual-failure")]
    public async Task ActualCommitCallbackRefusesMissingObservationsAndResidualTrackedRows(string attack)
    {
        using var f = new WitnessFixture(); await f.PersistActualWitnessAsync();
        f.Evidence.WitnessSaved = true; f.Evidence.WitnessCommitChecks = 1;
        switch (attack)
        {
            case "missing-save": f.Evidence.WitnessSaved = false; break;
            case "missing-precommit": f.Evidence.WitnessCommitChecks = 0; break;
            case "missing-rollback": f.Evidence.RolledBack = false; break;
            case "missing-flush": f.Evidence.Flushed = false; break;
            case "pending-save": f.Evidence.WitnessSaveIntent = true; break;
            case "tracked-witness": f.Db.Attach(await f.Db.GroupBatchClaimStates.AsNoTracking().SingleAsync()); break;
            case "dirty-witness": f.Db.Attach(await f.Db.GroupBatchClaimStates.AsNoTracking().SingleAsync()).Entity.OwnerId = Guid.NewGuid(); break;
            case "residual-start": f.Db.Attach(new GroupModelAttemptStartRecord { RequestId = Guid.NewGuid() }); break;
            case "residual-failure": f.Db.Attach(new GroupModelAttemptFailureRecord { RequestId = Guid.NewGuid() }); break;
        }
        var probe = new JournalProof.JournalTransactionProbe(f.Evidence);
        // Before rollback the expiry commit branch is intentionally inactive.
        if (attack == "missing-rollback")
        {
            Assert.Throws<InvalidOperationException>(() => f.Evidence.RequireDetachedWitness(f.Db));
            await probe.TransactionCommittedAsync(f.Transaction, f.EndEvent);
        }
        else await Assert.ThrowsAsync<InvalidOperationException>(() => probe.TransactionCommittedAsync(f.Transaction, f.EndEvent));
        Assert.False(f.Evidence.WitnessOnly); Assert.Equal(0, f.Evidence.WitnessCommits);
    }

    [Fact]
    public void ExactOriginalWitnessIsTheOnlyAllowedSaveIntent()
    {
        using var f = new WitnessFixture();
        var state = f.Db.GroupBatchClaimStates.AsNoTracking().Single();
        f.Db.Attach(state); state.ExpiryObservedAtUtc = f.Claim.ExpiresAtUtc;
        f.Evidence.RequireWitnessSaveIntent(f.Db);
        Assert.True(f.Evidence.WitnessSaveIntent);
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("wrong-owner")]
    [InlineData("wrong-expiry")]
    [InlineData("wrong-field")]
    [InlineData("mixed")]
    [InlineData("missing")]
    [InlineData("missing-lock")]
    public async Task SaveAndPersistedWitnessOraclesRefuseForeignMixedWrongOrUnprovenState(string attack)
    {
        using var f = new WitnessFixture();
        var state = f.Db.GroupBatchClaimStates.AsNoTracking().Single();
        if (attack == "foreign") state.BindingId = Guid.NewGuid();
        f.Db.Attach(state); state.ExpiryObservedAtUtc = f.Claim.ExpiresAtUtc;
        switch (attack)
        {
            case "wrong-owner": state.OwnerId = Guid.NewGuid(); break;
            case "wrong-expiry": state.ExpiryObservedAtUtc = f.Claim.ExpiresAtUtc.AddTicks(1); break;
            case "wrong-field": state.IssuedAtUtc = state.IssuedAtUtc.AddTicks(1); break;
            case "mixed": f.Db.Add(new GroupModelAttemptStartRecord { RequestId = Guid.NewGuid() }); break;
            case "missing": f.Db.ChangeTracker.Clear(); break;
        }
        if (attack == "missing-lock")
        {
            f.Evidence.RequireWitnessSaveIntent(f.Db);
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Evidence.RequirePersistedWitnessAsync(f.Db, f.Transaction, default));
            Assert.False(f.Evidence.WitnessSaved); // No connection: lock cannot be established.
        }
        else Assert.Throws<InvalidOperationException>(() => f.Evidence.RequireWitnessSaveIntent(f.Db));
    }

    private sealed class WitnessFixture : IDisposable
    {
        internal readonly GroupScope Scope = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        internal readonly PlatformDbContext Db = new(new DbContextOptionsBuilder<PlatformDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        internal readonly InertTransaction Transaction = new();
        internal readonly GroupBatchClaimReceipt Claim;
        internal readonly NoteProof.OwnedClock Clock;
        internal readonly JournalProof.JournalEvidence Evidence;
        internal WitnessFixture()
        {
            var now = new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);
            Claim = new(Scope, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 3, TimeSpan.FromMinutes(2).Ticks,
                now, now.AddMinutes(2), Guid.NewGuid(), 1, 1, 1, 0, 1);
            Clock = new(Claim.ExpiresAtUtc);
            var state = new GroupBatchClaimStateRecord
            {
                TenantId = Scope.TenantId,
                CompanyId = Scope.CompanyId,
                BindingId = Scope.SourceBindingId,
                BatchId = Claim.BatchId,
                Epoch = Claim.Epoch,
                OwnerId = Claim.OwnerId,
                OperationId = Claim.OperationId,
                IssuedAtUtc = Claim.IssuedAtUtc,
                ExpiresAtUtc = Claim.ExpiresAtUtc
            };
            Db.Add(state); Db.SaveChanges(); Db.ChangeTracker.Clear();
            Evidence = new(Scope, Clock)
            {
                Claim = Claim,
                OriginalState = DependencyProof.StateFingerprint(state),
                ExpireAfterFlush = true,
                Flushed = true,
                RolledBack = true,
                RequestId = Guid.NewGuid()
            };
        }
        internal async Task PersistActualWitnessAsync()
        {
            var state = await Db.GroupBatchClaimStates.AsNoTracking().SingleAsync();
            var method = typeof(GroupBatchClaimStore).GetMethod("PersistExpiryAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await (Task)method.Invoke(new GroupBatchClaimStore(Db, new(Scope.TenantId, Scope.CompanyId, Claim.ServiceId, 1), Clock),
                new object[] { state, Claim.ExpiresAtUtc, CancellationToken.None })!;
        }
        internal TransactionEndEventData EndEvent => new(new EventDefinition(Db.GetService<ILoggingOptions>(), new EventId(9002), LogLevel.Debug,
            "OwnedObserverColdCommit", _ => (_, _) => { }), (_, _) => "Cold event", Transaction, Db, Guid.NewGuid(), Guid.NewGuid(),
            true, Claim.ExpiresAtUtc, TimeSpan.Zero);
        public void Dispose() { Transaction.Dispose(); Db.Dispose(); }
    }
    private sealed class InertTransaction : DbTransaction
    {
        public override IsolationLevel IsolationLevel => IsolationLevel.Serializable;
        protected override DbConnection? DbConnection => null;
        public override void Commit() => throw new InvalidOperationException("No SQL commit permitted in cold control.");
        public override void Rollback() => throw new InvalidOperationException("No SQL rollback permitted in cold control.");
    }
}
