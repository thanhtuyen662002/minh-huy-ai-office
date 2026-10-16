extern alias GroupReferenceProof;

using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Reflection;
using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlClient.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;
using Proof = GroupReferenceProof::MinhHuy.AIOffice.GroupReference.RuntimeProof.GroupOriginalAdmissionRuntimeProof;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

// Real delegated DataTableReader consumption and synthetic owned connection
// controls only. No SQL, authenticated authority, image or model qualification.
public sealed class OwnedAcceptedTransactionObservationTests
{
    private const string Query = "SELECT CASE WHEN EXISTS (SELECT 1 FROM [aioffice].[GroupModelAttemptResults] AS [g] WHERE [g].[TenantId] = @p0 AND [g].[CompanyId] = @p1 AND [g].[BindingId] = @p2 AND [g].[BatchId] = @p3 AND [g].[PlanSha256] = @p4 AND [g].[OperationId] = @p5) THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END";

    [Theory]
    [InlineData("bool")]
    [InlineData("object")]
    [InlineData("generic")]
    [InlineData("async-generic")]
    [InlineData("index")]
    [InlineData("name")]
    [InlineData("values")]
    [InlineData("sync")]
    public async Task FreshReadOnlyTransactionDisposedWithoutCommitQualifiesOnlyItsOwnInvocation(string api)
    {
        using var f = new Fixture(); f.Dispatch();
        await using (var reader = f.Observation.Executed(f.Db, f.Command, Values(true)))
        {
            Assert.True(api == "sync" ? reader.Read() : await reader.ReadAsync());
            switch (api)
            {
                case "bool": case "sync": Assert.True(reader.GetBoolean(0)); break;
                case "object": Assert.Equal(true, reader.GetValue(0)); break;
                case "generic": Assert.True(reader.GetFieldValue<bool>(0)); break;
                case "async-generic": Assert.True(await reader.GetFieldValueAsync<bool>(0)); break;
                case "index": Assert.Equal(true, reader[0]); break;
                case "name": Assert.Equal(true, reader["Value"]); break;
                case "values": var result = new object[1]; Assert.Equal(1, reader.GetValues(result)); Assert.Equal(true, result[0]); break;
            }
            Assert.False(api == "sync" ? reader.Read() : await reader.ReadAsync());
        }
        f.Complete(); f.Observation.RequireRefused(f.Db, true);
        f.Observation.End(); f.Observation.Begin(f.Db, f.Scope, f.Batch, f.Plan, f.Operation);
        Assert.Throws<Proof.OwnedAcceptedObservationFailure>(() => f.Observation.RequireRefused(f.Db, true));
    }

    [Fact]
    public async Task SuccessfulPreflightCommitCannotBeMaskedByLaterConsumedPermitRefusal()
    {
        using var f = new Fixture(); await f.Consume();
        using var later = f.Connection.CreateCommand(); later.Transaction = f.Transaction; later.CommandText = "SELECT 1";
        f.Observation.Query(f.Db, later); f.Observation.Committed(f.Db, f.Transaction); f.Complete();
        var error = Assert.ThrowsAny<IOException>(() => f.Observation.RequireRefused(f.Db, true));
        Assert.Equal("FAIL owned original admission runtime phase-admitted-execution-accepted-transaction kind-accepted-preflight-committed",
            Proof.FailureDiagnostic("admitted-execution-accepted-transaction", error));
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("dispatch-only")]
    [InlineData("command-failed")]
    [InlineData("not-executed")]
    [InlineData("no-true-consumption")]
    [InlineData("no-eof")]
    [InlineData("no-dispose")]
    [InlineData("not-refused")]
    public async Task OriginalDispatchOnlyAndIncompleteReaderShapesNeverQualify(string fault)
    {
        using var f = new Fixture(); f.Dispatch();
        DbDataReader? reader = null;
        try
        {
            if (fault == "command-failed") f.Observation.CommandFailed(f.Command, new InvalidOperationException("PRIVATE_CANARY"));
            if (fault is not ("dispatch-only" or "command-failed" or "not-executed"))
            {
                reader = f.Observation.Executed(f.Db, f.Command, Values(true));
                Assert.True(await reader.ReadAsync());
                if (fault != "no-true-consumption") Assert.True(reader.GetBoolean(0));
                if (fault != "no-eof") Assert.False(await reader.ReadAsync());
                if (fault != "no-dispose") await reader.DisposeAsync();
            }
            f.Complete();
            Assert.Throws<Proof.OwnedAcceptedObservationFailure>(() => f.Observation.RequireRefused(f.Db, fault != "not-refused"));
        }
        finally { if (reader is not null) await reader.DisposeAsync(); }
    }

    [Theory]
    [InlineData("unarmed")]
    [InlineData("no-start")]
    [InlineData("no-query")]
    [InlineData("wrong-query")]
    [InlineData("duplicate-query")]
    public void MissingOrAmbiguousActualObservationNeverQualifiesRefusal(string fault)
    {
        using var f = new Fixture();
        if (fault is "unarmed" or "no-start") f.Observation.End();
        if (fault == "no-start") f.Observation.Begin(f.Db, f.Scope, f.Batch, f.Plan, f.Operation);
        if (fault == "wrong-query") { f.Command.CommandText = "SELECT 1"; f.Dispatch(); }
        if (fault == "duplicate-query")
        { f.Dispatch(); Assert.Throws<Proof.OwnedAcceptedObservationFailure>(() => f.Dispatch()); }
        f.Complete(); Assert.Throws<Proof.OwnedAcceptedObservationFailure>(() => f.Observation.RequireRefused(f.Db, true));
    }

    [Theory]
    [InlineData("false")]
    [InlineData("empty")]
    [InlineData("multiple")]
    [InlineData("non-bool")]
    [InlineData("multiple-columns")]
    [InlineData("duplicate-value")]
    public async Task OnlyOneActualTrueBooleanRowMayQualify(string fault)
    {
        using var f = new Fixture(); f.Dispatch();
        var table = new DataTable(); table.Columns.Add("Value", fault == "non-bool" ? typeof(int) : typeof(bool));
        if (fault == "multiple-columns") table.Columns.Add("Other", typeof(bool));
        if (fault != "empty") table.Rows.Add(fault == "non-bool" ? 1 : fault != "false");
        if (fault == "multiple") table.Rows.Add(true);
        var inner = table.CreateDataReader();
        if (fault == "multiple-columns")
            Assert.Throws<Proof.OwnedAcceptedObservationFailure>(() => f.Observation.Executed(f.Db, f.Command, inner));
        else
        {
            await using var reader = f.Observation.Executed(f.Db, f.Command, inner);
            while (await reader.ReadAsync())
            {
                _ = reader.GetValue(0);
                if (fault == "duplicate-value") _ = reader.GetBoolean(0);
            }
        }
        inner.Dispose(); f.Complete();
        Assert.Throws<Proof.OwnedAcceptedObservationFailure>(() => f.Observation.RequireRefused(f.Db, true));
    }

    [Theory]
    [InlineData("context")]
    [InlineData("connection")]
    [InlineData("transaction")]
    [InlineData("parameter")]
    [InlineData("swapped-parameters")]
    [InlineData("unscoped")]
    [InlineData("or-predicate")]
    [InlineData("sql-changed")]
    [InlineData("new-command")]
    [InlineData("suppressed")]
    [InlineData("duplicate-dispatch")]
    public void ExactDispatchAndExecutionObjectsScopeAndTextAreRequired(string fault)
    {
        using var f = new Fixture(); using var foreign = new Fixture();
        if (fault == "parameter") f.Command.Parameters.RemoveAt(0);
        if (fault == "swapped-parameters") (f.Command.Parameters[0].Value, f.Command.Parameters[1].Value) = (f.Command.Parameters[1].Value, f.Command.Parameters[0].Value);
        if (fault == "unscoped") f.Command.CommandText = "SELECT CASE WHEN EXISTS (SELECT 1 FROM [aioffice].[GroupModelAttemptResults]) THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END";
        if (fault == "or-predicate") f.Command.CommandText = f.Command.CommandText.Replace(" AND ", " OR ", StringComparison.Ordinal);
        if (fault == "connection") f.Command.Connection = foreign.Connection;
        if (fault == "transaction") f.Command.Transaction = foreign.Transaction;
        if (fault is "context" or "connection" or "transaction" or "parameter" or "suppressed" or "swapped-parameters" or "unscoped" or "or-predicate")
            Assert.Throws<Proof.OwnedAcceptedObservationFailure>(() => f.Observation.Query(fault == "context" ? foreign.Db : f.Db, f.Command, fault == "suppressed"));
        else
        {
            f.Dispatch();
            if (fault == "duplicate-dispatch") Assert.Throws<Proof.OwnedAcceptedObservationFailure>(() => f.Dispatch());
            else
            {
                using var reader = Values(true);
                if (fault == "sql-changed") f.Command.CommandText += " ";
                Assert.Throws<Proof.OwnedAcceptedObservationFailure>(() => f.Observation.Executed(f.Db, fault == "new-command" ? foreign.Command : f.Command, reader));
            }
        }
        f.Complete(); Assert.Throws<Proof.OwnedAcceptedObservationFailure>(() => f.Observation.RequireRefused(f.Db, true));
    }

    [Theory]
    [InlineData("context", "context")]
    [InlineData("connection", "connection")]
    [InlineData("transaction", "transaction-identity")]
    [InlineData("parameter", "predicates")]
    public void IdentityFailureProjectsOnlyTheFixedFailedBoundary(string fault, string category)
    {
        using var f = new Fixture(); using var foreign = new Fixture();
        if (fault == "connection") f.Command.Connection = foreign.Connection;
        if (fault == "transaction") f.Command.Transaction = foreign.Transaction;
        if (fault == "parameter") f.Command.Parameters.RemoveAt(0);
        var error = Assert.Throws<Proof.OwnedAcceptedObservationFailure>(() =>
            f.Observation.Query(fault == "context" ? foreign.Db : f.Db, f.Command));
        Assert.Equal("accepted-query-dispatch-" + category, error.PublicKind);
        Assert.Null(error.InnerException);
        f.Complete();
        Assert.Same(error, Assert.Throws<Proof.OwnedAcceptedObservationFailure>(() => f.Observation.RequireRefused(f.Db, true)));
    }

    [Theory]
    [InlineData("foreign-commit")]
    [InlineData("second-start")]
    [InlineData("double-arm")]
    [InlineData("weak-isolation")]
    public void AnotherTransactionOrInvocationCannotReplaceTheOwnedObservation(string fault)
    {
        using var f = new Fixture(); using var foreign = new Fixture();
        Assert.Throws<Proof.OwnedAcceptedObservationFailure>(() =>
        {
            if (fault == "foreign-commit") f.Observation.Committed(f.Db, foreign.Transaction);
            else if (fault == "double-arm") f.Observation.Begin(f.Db, f.Scope, f.Batch, f.Plan, f.Operation);
            else
            {
                using var tx = new ColdTransaction(f.Connection, fault == "weak-isolation" ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable);
                f.Observation.Started(f.Db, f.Connection, tx);
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LaterSqlWithoutCommitCannotMasqueradeAsTheAcceptedGuard(bool commandFailed)
    {
        using var f = new Fixture(); await f.Consume();
        using var next = f.Connection.CreateCommand(); next.Transaction = f.Transaction; next.CommandText = "SELECT 1";
        f.Observation.Query(f.Db, next);
        if (commandFailed) f.Observation.CommandFailed(next, new InvalidOperationException("PRIVATE_CANARY"));
        f.Complete(); var error = Assert.Throws<Proof.OwnedAcceptedObservationFailure>(() => f.Observation.RequireRefused(f.Db, true));
        Assert.Contains("accepted-query-post-query-", error.PublicKind);
    }

    [Theory]
    [InlineData("read")]
    [InlineData("getter")]
    [InlineData("dispose")]
    [InlineData("cancelled")]
    public async Task RealReaderFaultsRemainStickyAndNeverExposePrivateText(string fault)
    {
        using var f = new Fixture(); f.Dispatch();
        var inner = new FaultReader(Values(true), fault);
        var reader = f.Observation.Executed(f.Db, f.Command, inner);
        if (fault is "read" or "cancelled") await Assert.ThrowsAnyAsync<Exception>(() => reader.ReadAsync());
        else
        {
            Assert.True(await reader.ReadAsync());
            if (fault == "getter") Assert.Throws<InvalidOperationException>(() => reader.GetBoolean(0));
            else { Assert.True(reader.GetBoolean(0)); Assert.False(await reader.ReadAsync()); }
        }
        if (fault == "dispose") await Assert.ThrowsAsync<InvalidOperationException>(() => reader.DisposeAsync().AsTask());
        else await reader.DisposeAsync();
        f.Complete(); var error = Assert.Throws<Proof.OwnedAcceptedObservationFailure>(() => f.Observation.RequireRefused(f.Db, true));
        var line = Proof.FailureDiagnostic("admitted-execution-accepted-transaction", error);
        Assert.DoesNotContain("PRIVATE_CANARY", line); Assert.DoesNotContain(f.Plan, line); Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task PriorCommandFailureCannotBeErasedByALaterCompleteReaderOrCommit()
    {
        using var f = new Fixture(); f.Observation.CommandFailed(f.Command, new HostileException());
        await f.Consume(); f.Observation.Committed(f.Db, f.Transaction); f.Complete();
        var error = Assert.Throws<Proof.OwnedAcceptedObservationFailure>(() => f.Observation.RequireRefused(f.Db, true));
        Assert.Contains("accepted-query-before-query-reader", error.PublicKind); Assert.Equal(0, HostileException.Reads);
    }

    [Theory]
    [InlineData("refused")]
    [InlineData("committed")]
    [InlineData("command-failed")]
    public async Task ActualEfDispatcherDeliversTheRealReaderAndTransactionCallbacks(string outcome)
    {
        using var connection = new ReaderConnection { Fail = outcome == "command-failed" };
        var probe = new Proof.AdmittedExecutionSqlProbe();
        using var db = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>().UseSqlServer(connection)
            .AddInterceptors(probe, new Proof.AcceptedTransactionProbe(probe.Accepted)).Options);
        var scope = new GroupScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var batch = Guid.NewGuid(); var operation = Guid.NewGuid(); var plan = new string('A', 64);
        probe.Accepted.Begin(db, scope, batch, plan, operation);
        await using (var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable))
        {
            var query = db.GroupModelAttemptResults.AsNoTracking().AnyAsync(x => x.TenantId == scope.TenantId
                && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == batch
                && x.PlanSha256 == plan && x.OperationId == operation);
            if (outcome == "command-failed") await Assert.ThrowsAsync<InvalidOperationException>(() => query);
            else Assert.True(await query);
            if (outcome == "committed") await transaction.CommitAsync();
        }
        Assert.Equal(1, connection.ReaderCalls);
        if (outcome == "refused") probe.Accepted.RequireRefused(db, true);
        else
        {
            var error = Assert.ThrowsAny<IOException>(() => probe.Accepted.RequireRefused(db, true));
            var line = Proof.FailureDiagnostic("admitted-execution-accepted-transaction", error);
            Assert.Contains(outcome == "committed" ? "kind-accepted-preflight-committed" : "kind-accepted-query-execute-reader", line);
            Assert.DoesNotContain("PRIVATE_CANARY", line);
        }
    }

    [Theory]
    [InlineData("raw-before-target-after")]
    [InlineData("raw-after-consumption")]
    [InlineData("raw-error-after-tx-lost")]
    public async Task NormalizedRawAdoBeforeCannotHideBehindEfOrAfterOrdering(string timing)
    {
        using var f = new Fixture(); f.Dispatch(); var id = Guid.NewGuid();
        f.Observation.ProviderEvent(new(Proof.ProviderEventKind.Before, id, f.Command));
        using var raw = f.Connection.CreateCommand(); raw.Transaction = f.Transaction; raw.CommandText = "SELECT 1";
        if (timing == "raw-before-target-after") f.Observation.ProviderEvent(new(Proof.ProviderEventKind.Before, Guid.NewGuid(), raw));
        await f.ConsumeDispatched();
        if (timing == "raw-after-consumption") f.Observation.ProviderEvent(new(Proof.ProviderEventKind.Before, Guid.NewGuid(), raw));
        if (timing == "raw-error-after-tx-lost")
        {
            var rawId = Guid.NewGuid(); f.Observation.ProviderEvent(new(Proof.ProviderEventKind.Before, rawId, raw));
            raw.Transaction = null;
            f.Observation.ProviderEvent(new(Proof.ProviderEventKind.Error, rawId, raw, new HostileException()));
        }
        f.Observation.ProviderEvent(new(Proof.ProviderEventKind.After, id, f.Command));
        f.Complete(); var error = Assert.Throws<Proof.OwnedAcceptedObservationFailure>(() => f.Observation.RequireRefused(f.Db, true));
        Assert.Contains("post-query", error.PublicKind); Assert.Equal(0, HostileException.Reads);
    }

    [Theory]
    [InlineData("foreign-connection")]
    [InlineData("foreign-transaction")]
    [InlineData("no-transaction-cleanup")]
    [InlineData("missing-transaction-active")]
    public async Task ForeignConnectionAndDisposedCleanupExcludedButActiveTransactionMismatchRefuses(string boundary)
    {
        using var f = new Fixture(); using var other = new Fixture(); f.Dispatch(); var id = Guid.NewGuid();
        f.Observation.ProviderEvent(new(Proof.ProviderEventKind.Before, id, f.Command)); await f.ConsumeDispatched();
        if (boundary == "no-transaction-cleanup") { f.Complete(); f.Connection.Open(); }
        using var raw = (boundary == "foreign-connection" ? other.Connection : f.Connection).CreateCommand();
        raw.CommandText = "SET TRANSACTION ISOLATION LEVEL READ COMMITTED;";
        raw.Transaction = boundary == "foreign-connection" ? other.Transaction : boundary == "foreign-transaction" ? new ColdTransaction(f.Connection, IsolationLevel.Serializable) : null;
        var foreignId = Guid.NewGuid(); f.Observation.ProviderEvent(new(Proof.ProviderEventKind.Before, foreignId, raw));
        f.Observation.ProviderEvent(new(Proof.ProviderEventKind.After, foreignId, raw));
        f.Observation.ProviderEvent(new(Proof.ProviderEventKind.After, id, f.Command));
        if (boundary is "foreign-transaction" or "missing-transaction-active")
            Assert.Throws<Proof.OwnedAcceptedObservationFailure>(() => f.Observation.RequireProviderCounts());
        else f.Observation.RequireProviderCounts();
        f.Complete();
        if (boundary is "foreign-transaction" or "missing-transaction-active")
            Assert.Throws<Proof.OwnedAcceptedObservationFailure>(() => f.Observation.RequireRefused(f.Db, true));
        else f.Observation.RequireRefused(f.Db, true);
        if (boundary == "foreign-transaction") raw.Transaction?.Dispose();
    }

    [Theory]
    [InlineData("missing-before")]
    [InlineData("missing-after")]
    [InlineData("wrong-id")]
    [InlineData("duplicate-before")]
    [InlineData("duplicate-after")]
    [InlineData("wrong-command")]
    [InlineData("target-error")]
    [InlineData("unmatched-after-missing-tx")]
    [InlineData("unmatched-after-foreign-tx")]
    public async Task MissingAmbiguousOrErroredProviderCorrelationNeverQualifies(string fault)
    {
        using var f = new Fixture(); using var other = new Fixture(); f.Dispatch(); var id = Guid.NewGuid();
        if (fault != "missing-before") f.Observation.ProviderEvent(new(Proof.ProviderEventKind.Before, id, f.Command));
        if (fault == "duplicate-before") f.Observation.ProviderEvent(new(Proof.ProviderEventKind.Before, id, f.Command));
        await f.ConsumeDispatched();
        if (fault != "missing-after") f.Observation.ProviderEvent(new(Proof.ProviderEventKind.After,
            fault == "wrong-id" ? Guid.NewGuid() : id, fault == "wrong-command" ? other.Command : f.Command));
        if (fault == "duplicate-after") f.Observation.ProviderEvent(new(Proof.ProviderEventKind.After, id, f.Command));
        if (fault == "target-error") f.Observation.ProviderEvent(new(Proof.ProviderEventKind.Error, id, f.Command, new OperationCanceledException()));
        if (fault is "unmatched-after-missing-tx" or "unmatched-after-foreign-tx")
        {
            using var raw = f.Connection.CreateCommand();
            raw.Transaction = fault == "unmatched-after-foreign-tx" ? other.Transaction : null;
            f.Observation.ProviderEvent(new(Proof.ProviderEventKind.After, Guid.NewGuid(), raw));
        }
        Assert.Throws<Proof.OwnedAcceptedObservationFailure>(() => f.Observation.RequireProviderCounts());
    }

    [Fact]
    public void ActualSqlConnectionCannotSkipMissingProviderEvidenceWithoutOpeningSql()
    {
        using var connection = new SqlConnection(); using var db = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>().UseSqlServer(connection).Options);
        var observation = new Proof.AcceptedTransactionObservation();
        observation.Begin(db, new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), Guid.NewGuid(), new('A', 64), Guid.NewGuid());
        try
        {
            var error = Assert.Throws<Proof.OwnedAcceptedObservationFailure>(() => observation.RequireRefused(db, true));
            Assert.Equal("accepted-query-provider-incomplete", error.PublicKind); Assert.Equal(ConnectionState.Closed, connection.State);
        }
        finally { observation.End(); }
    }

    [Theory]
    [InlineData("before")]
    [InlineData("after")]
    [InlineData("error")]
    public void ActualSealedProducerPayloadsDecodeWithoutReadingExceptionOrForeignGetters(string kind)
    {
        using var command = new SqlCommand(); var id = Guid.NewGuid();
        var type = kind == "before" ? typeof(SqlClientCommandBefore) : kind == "after" ? typeof(SqlClientCommandAfter) : typeof(SqlClientCommandError);
        var args = new List<object?> { id, "cold", 0L, null, null, command };
        if (kind == "after") args.Add(null);
        if (kind == "error") args.Add(new HostileException());
        // Only the installed provider's known sealed metadata constructors;
        // no connection open, private payload reflection or fake SQL claim.
        var payload = type.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single().Invoke(args.ToArray());
        var name = kind == "before" ? SqlClientCommandBefore.Name : kind == "after" ? SqlClientCommandAfter.Name : SqlClientCommandError.Name;
        var evidence = Proof.DecodeProviderEvent(new(name, payload)); Assert.NotNull(evidence);
        Assert.Same(command, evidence.Value.Command); Assert.Equal(id, evidence.Value.Id);
        Assert.Equal("Owned SqlClient command diagnostic (private metadata).", evidence.Value.ToString());
        Assert.Null(Proof.DecodeProviderEvent(new("unknown", payload)));
        Assert.Null(Proof.DecodeProviderEvent(new(name, new HostilePayload()))); Assert.Equal(0, HostilePayload.Reads); Assert.Equal(0, HostileException.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TrustedListenerSubscriptionIsBoundedAndUnsubscribesBeforeLatePayload(bool disposed)
    {
        using var f = new Fixture(); await f.Consume();
        var type = typeof(SqlConnection).Assembly.GetType("Microsoft.Data.SqlClient.Diagnostics.SqlDiagnosticListener", throwOnError: true)!;
        using var publisher = (DiagnosticListener)Activator.CreateInstance(type, nonPublic: true)!;
        using var listener = new Proof.AcceptedProviderListener(f.Observation); Assert.True(listener.HasTrustedListener);
        if (disposed) listener.Dispose();
        publisher.Write(SqlClientCommandBefore.Name, new HostilePayload());
        f.Complete();
        if (disposed) f.Observation.RequireRefused(f.Db, true);
        else Assert.Throws<Proof.OwnedAcceptedObservationFailure>(() => f.Observation.RequireRefused(f.Db, true));
        Assert.Equal(0, HostilePayload.Reads);
    }

    [Fact]
    public async Task LateErrorKeepsItsOriginalBeforeQueryStageAndUnknownEventKindRefuses()
    {
        using var f = new Fixture(); using var raw = f.Connection.CreateCommand(); raw.Transaction = f.Transaction;
        var rawId = Guid.NewGuid(); f.Observation.ProviderEvent(new(Proof.ProviderEventKind.Before, rawId, raw));
        await f.Consume();
        f.Observation.ProviderEvent(new(Proof.ProviderEventKind.Error, rawId, raw, new HostileException()));
        var error = Assert.Throws<Proof.OwnedAcceptedObservationFailure>(() => f.Observation.RequireProviderCounts());
        Assert.Equal("accepted-query-before-query-reader", error.PublicKind); Assert.Equal(0, HostileException.Reads);
        using var other = new Fixture();
        other.Observation.ProviderEvent(new((Proof.ProviderEventKind)(-1), Guid.NewGuid(), other.Command));
        Assert.Throws<Proof.OwnedAcceptedObservationFailure>(() => other.Observation.RequireProviderCounts());
    }

    private sealed class HostilePayload
    {
        internal static int Reads;
        public object Command { get { Reads++; throw new Exception("PRIVATE_CANARY"); } }
        public override string ToString() { Reads++; throw new Exception("PRIVATE_CANARY"); }
    }

    private static DataTableReader Values(params bool[] values)
    {
        var table = new DataTable(); table.Columns.Add("Value", typeof(bool));
        foreach (var value in values) table.Rows.Add(value); return table.CreateDataReader();
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly BindingStorePermissionTests.SyntheticConnection Connection = new(1);
        internal readonly GroupScope Scope = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        internal readonly Guid Batch = Guid.NewGuid(), Operation = Guid.NewGuid();
        internal readonly string Plan = new('A', 64);
        internal readonly PlatformDbContext Db;
        internal readonly ColdTransaction Transaction;
        internal readonly DbCommand Command;
        internal readonly Proof.AcceptedTransactionObservation Observation = new();
        private readonly IDbContextTransaction owned;
        internal Fixture()
        {
            Db = new(new DbContextOptionsBuilder<PlatformDbContext>().UseSqlServer(Connection).Options);
            Connection.Open(); Transaction = new(Connection, IsolationLevel.Serializable); owned = Db.Database.UseTransaction(Transaction)!;
            Command = Connection.CreateCommand(); Command.CommandText = Query; Command.Transaction = Transaction;
            object[] args = [Scope.TenantId, Scope.CompanyId, Scope.SourceBindingId, Batch, Plan, Operation];
            for (var i = 0; i < args.Length; i++) { var parameter = Command.CreateParameter(); parameter.ParameterName = "@p" + i; parameter.Value = args[i]; Command.Parameters.Add(parameter); }
            Observation.Begin(Db, Scope, Batch, Plan, Operation); Observation.Started(Db, Connection, Transaction);
        }
        internal void Dispatch() => Observation.Query(Db, Command);
        internal async Task Consume()
        {
            Dispatch(); await ConsumeDispatched();
        }
        internal async Task ConsumeDispatched()
        {
            await using var reader = Observation.Executed(Db, Command, Values(true));
            Assert.True(await reader.ReadAsync()); Assert.True(reader.GetBoolean(0)); Assert.False(await reader.ReadAsync());
        }
        internal void Complete() { owned.Dispose(); Transaction.Dispose(); Connection.Close(); }
        public void Dispose() { Command.Dispose(); Complete(); Db.Dispose(); Connection.Dispose(); }
    }
    private sealed class ColdTransaction(DbConnection connection, IsolationLevel level, bool allowCommit = false) : DbTransaction
    {
        private DbConnection? current = connection;
        public override IsolationLevel IsolationLevel => level;
        protected override DbConnection? DbConnection => current;
        public override void Commit() { if (!allowCommit) throw new NotSupportedException(); }
        public override void Rollback() => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { current = null; base.Dispose(disposing); }
    }
    private sealed class ReaderConnection : DbConnection
    {
        private ConnectionState state;
        internal bool Fail;
        internal int ReaderCalls;
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString { get; set; } = "";
        public override string Database => "ColdAcceptedReader";
        public override string DataSource => "NoNetwork";
        public override string ServerVersion => "16.0";
        public override ConnectionState State => state;
        public override void Open() => state = ConnectionState.Open;
        public override void Close() => state = ConnectionState.Closed;
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => new ColdTransaction(this, isolationLevel, true);
        protected override DbCommand CreateDbCommand() => new ReaderCommand(this);
    }
    private sealed class ReaderCommand(ReaderConnection connection) : DbCommand
    {
        private readonly DbParameterCollection parameters = new Microsoft.Data.SqlClient.SqlCommand().Parameters;
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string CommandText { get; set; } = "";
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; } = connection;
        protected override DbTransaction? DbTransaction { get; set; }
        protected override DbParameterCollection DbParameterCollection => parameters;
        public override void Cancel() { }
        public override void Prepare() => throw new NotSupportedException();
        public override int ExecuteNonQuery() => throw new NotSupportedException();
        public override object ExecuteScalar() => throw new NotSupportedException();
        protected override DbParameter CreateDbParameter() => new Microsoft.Data.SqlClient.SqlParameter();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        { connection.ReaderCalls++; if (connection.Fail) throw new InvalidOperationException("PRIVATE_CANARY"); return Values(true); }
    }
    private sealed class HostileException : InvalidOperationException
    {
        internal static int Reads;
        public override string Message { get { Reads++; throw new Exception("PRIVATE_CANARY"); } }
        public override string ToString() { Reads++; throw new Exception("PRIVATE_CANARY"); }
    }
    private sealed class FaultReader(DbDataReader inner, string fault) : DbDataReader
    {
        public override bool Read() => fault == "read" ? throw new InvalidOperationException("PRIVATE_CANARY") : inner.Read();
        public override Task<bool> ReadAsync(CancellationToken token) => fault == "cancelled" ? Task.FromException<bool>(new OperationCanceledException()) : Task.FromResult(Read());
        public override bool GetBoolean(int ordinal) => fault == "getter" ? throw new InvalidOperationException("PRIVATE_CANARY") : inner.GetBoolean(ordinal);
        public override ValueTask DisposeAsync() { inner.Dispose(); if (fault == "dispose") throw new InvalidOperationException("PRIVATE_CANARY"); return ValueTask.CompletedTask; }
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); }
        public override void Close() => inner.Close();
        public override int Depth => inner.Depth;
        public override int FieldCount => inner.FieldCount;
        public override bool HasRows => inner.HasRows;
        public override bool IsClosed => inner.IsClosed;
        public override int RecordsAffected => inner.RecordsAffected;
        public override object this[int ordinal] => inner[ordinal];
        public override object this[string name] => inner[name];
        public override bool NextResult() => inner.NextResult();
        public override object GetValue(int ordinal) => inner.GetValue(ordinal);
        public override int GetValues(object[] values) => inner.GetValues(values);
        public override bool IsDBNull(int ordinal) => inner.IsDBNull(ordinal);
        public override string GetName(int ordinal) => inner.GetName(ordinal);
        public override int GetOrdinal(string name) => inner.GetOrdinal(name);
        public override string GetDataTypeName(int ordinal) => inner.GetDataTypeName(ordinal);
        public override Type GetFieldType(int ordinal) => inner.GetFieldType(ordinal);
        public override byte GetByte(int ordinal) => inner.GetByte(ordinal);
        public override long GetBytes(int ordinal, long offset, byte[]? buffer, int bufferOffset, int length) => inner.GetBytes(ordinal, offset, buffer, bufferOffset, length);
        public override char GetChar(int ordinal) => inner.GetChar(ordinal);
        public override long GetChars(int ordinal, long offset, char[]? buffer, int bufferOffset, int length) => inner.GetChars(ordinal, offset, buffer, bufferOffset, length);
        public override Guid GetGuid(int ordinal) => inner.GetGuid(ordinal);
        public override short GetInt16(int ordinal) => inner.GetInt16(ordinal);
        public override int GetInt32(int ordinal) => inner.GetInt32(ordinal);
        public override long GetInt64(int ordinal) => inner.GetInt64(ordinal);
        public override float GetFloat(int ordinal) => inner.GetFloat(ordinal);
        public override double GetDouble(int ordinal) => inner.GetDouble(ordinal);
        public override decimal GetDecimal(int ordinal) => inner.GetDecimal(ordinal);
        public override string GetString(int ordinal) => inner.GetString(ordinal);
        public override DateTime GetDateTime(int ordinal) => inner.GetDateTime(ordinal);
        public override System.Collections.IEnumerator GetEnumerator() => ((System.Collections.IEnumerable)inner).GetEnumerator();
    }
}
