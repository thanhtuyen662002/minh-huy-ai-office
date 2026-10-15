using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlClient.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.GroupReference.RuntimeProof;

internal static partial class GroupOriginalAdmissionRuntimeProof
{
    internal enum AcceptedStage { Binding, Provider, BeforeQuery, Dispatch, Execute, Read, Dispose, PostQuery, Transaction, Complete }
    internal enum AcceptedKind { Identity, Shape, Incomplete, Value, Reader, Cancelled, SqlConstraint, SqlPermission, SqlTimeout, SqlDeadlock, SqlOther, Other }
    internal sealed class OwnedAcceptedObservationFailure(AcceptedStage stage, AcceptedKind kind) : IOException
    {
        internal string PublicKind => "accepted-query-" + (stage switch
        {
            AcceptedStage.Binding => "binding",
            AcceptedStage.Provider => "provider",
            AcceptedStage.BeforeQuery => "before-query",
            AcceptedStage.Dispatch => "dispatch",
            AcceptedStage.Execute => "execute",
            AcceptedStage.Read => "read",
            AcceptedStage.Dispose => "dispose",
            AcceptedStage.PostQuery => "post-query",
            AcceptedStage.Transaction => "transaction",
            _ => "complete"
        }) + "-" + (kind switch
        {
            AcceptedKind.Identity => "identity",
            AcceptedKind.Shape => "shape",
            AcceptedKind.Incomplete => "incomplete",
            AcceptedKind.Value => "value",
            AcceptedKind.Reader => "reader",
            AcceptedKind.Cancelled => "cancelled",
            AcceptedKind.SqlConstraint => "sql-constraint",
            AcceptedKind.SqlPermission => "sql-permission",
            AcceptedKind.SqlTimeout => "sql-timeout",
            AcceptedKind.SqlDeadlock => "sql-deadlock",
            AcceptedKind.SqlOther => "sql-other",
            _ => "other"
        });
    }
    // Test observation only. Dispatch/setup never proves an actual true scalar.
    internal sealed class AcceptedTransactionObservation
    {
        private PlatformDbContext? database;
        private DbConnection? connection;
        private DbTransaction? transaction;
        private DbCommand? command;
        private string? sql;
        private object[] parameters = [];
        private int starts, dispatches, executions, rows, values, ends, commits, laterCommands;
        private bool closed;
        private OwnedAcceptedObservationFailure? failure;
        private AcceptedProviderListener? provider;
        private readonly object providerGate = new();
        private readonly Dictionary<Guid, (DbCommand Command, AcceptedStage Stage)> providerCommands = [];
        private Guid targetProviderOperation;
        private int providerBefore, providerAfter;
        internal bool Armed { get; private set; }
        internal void Begin(PlatformDbContext db, GroupScope scope, Guid batch, string plan, Guid operation)
        {
            if (Armed || db is null || batch == Guid.Empty || operation == Guid.Empty || plan is null
                || plan.Length != 64 || plan.Any(x => x is not (>= '0' and <= '9' or >= 'A' and <= 'F')))
                throw new OwnedAcceptedObservationFailure(AcceptedStage.Binding, AcceptedKind.Identity);
            scope.Validate(); database = db; connection = db.Database.GetDbConnection(); transaction = null; command = null; sql = null;
            parameters = [scope.TenantId, scope.CompanyId, scope.SourceBindingId, batch, plan, operation];
            starts = dispatches = executions = rows = values = ends = commits = laterCommands = 0;
            failure = null; closed = false; Armed = true;
            lock (providerGate) { providerCommands.Clear(); targetProviderOperation = Guid.Empty; providerBefore = providerAfter = 0; }
            // Mechanical runtime requirement, never a caller/config bypass.
            if (connection is SqlConnection) provider = new(this);
        }
        internal void End()
        {
            provider?.Dispose(); provider = null; Armed = false;
            lock (providerGate) { providerCommands.Clear(); }
        }
        internal void Started(DbContext? db, DbConnection current, DbTransaction value)
        {
            if (!Armed) return;
            if (++starts != 1 || !ReferenceEquals(database, db) || !ReferenceEquals(connection, current)
                || current.State != ConnectionState.Open || !ReferenceEquals(value.Connection, current)
                || value.IsolationLevel != IsolationLevel.Serializable) Reject(AcceptedStage.Transaction, AcceptedKind.Identity);
            transaction = value;
        }
        internal void Query(DbContext? db, DbCommand value, bool suppressed = false)
        {
            if (!Armed) return;
            if (command is not null)
            {
                if (ReferenceEquals(command, value)) Reject(AcceptedStage.Dispatch, AcceptedKind.Shape);
                laterCommands++;
            }
            if (!value.CommandText.Contains("FROM [aioffice].[GroupModelAttemptResults]", StringComparison.Ordinal)) return;
            RequireIdentity(db, value, AcceptedStage.Dispatch);
            if (suppressed || ++dispatches != 1 || !value.CommandText.Contains("EXISTS", StringComparison.Ordinal))
                Reject(AcceptedStage.Dispatch, AcceptedKind.Shape);
            command = value; sql = value.CommandText;
        }
        internal DbDataReader Executed(DbContext? db, DbCommand value, DbDataReader reader)
        {
            if (!Armed || !ReferenceEquals(command, value)) Reject(AcceptedStage.Execute, AcceptedKind.Identity);
            RequireIdentity(db, value, AcceptedStage.Execute);
            if (++executions != 1 || value.CommandText != sql) Reject(AcceptedStage.Execute, AcceptedKind.Shape);
            try { if (reader.FieldCount != 1) Reject(AcceptedStage.Execute, AcceptedKind.Shape); }
            catch (Exception error) { Failed(AcceptedStage.Execute, error); throw; }
            return new AcceptedObservedReader(reader, this);
        }
        internal bool IsTarget(DbCommand value) => Armed && ReferenceEquals(command, value);
        internal void CommandFailed(DbCommand value, Exception error)
        {
            if (Armed) Failed(command is null ? AcceptedStage.BeforeQuery
                : !ReferenceEquals(command, value) ? AcceptedStage.PostQuery : AcceptedStage.Execute, error);
        }
        internal void Failed(AcceptedStage stage, Exception error)
        {
            var kind = error switch
            {
                SqlException { Number: 547 } => AcceptedKind.SqlConstraint,
                SqlException { Number: 229 } => AcceptedKind.SqlPermission,
                SqlException { Number: -2 } => AcceptedKind.SqlTimeout,
                SqlException { Number: 1205 } => AcceptedKind.SqlDeadlock,
                SqlException => AcceptedKind.SqlOther,
                OperationCanceledException => AcceptedKind.Cancelled,
                _ => AcceptedKind.Reader
            };
            Mark(stage, kind);
        }
        internal void ThrowIfFailed() { if (failure is not null) throw failure; }
        internal void Read(bool available)
        {
            if (!Armed || executions != 1 || closed || ends != 0) Mark(AcceptedStage.Read, AcceptedKind.Shape);
            if (available) { if (++rows != 1) Mark(AcceptedStage.Read, AcceptedKind.Shape); }
            else if (++ends != 1 || rows != 1 || values != 1) Mark(AcceptedStage.Read, AcceptedKind.Incomplete);
        }
        internal void Scalar(int ordinal, object? value)
        {
            if (!Armed || executions != 1 || rows != 1 || ends != 0 || closed || ordinal != 0 || ++values != 1)
                Mark(AcceptedStage.Read, AcceptedKind.Shape);
            if (value is not true) Mark(AcceptedStage.Read, AcceptedKind.Value);
        }
        internal void Closed()
        {
            if (rows != 1 || values != 1 || ends != 1) Mark(AcceptedStage.Dispose, AcceptedKind.Incomplete);
            closed = true;
        }
        internal void ExtraResult() => Mark(AcceptedStage.Read, AcceptedKind.Shape);
        internal void Committed(DbContext? db, DbTransaction value)
        {
            if (!Armed) return;
            if (!ReferenceEquals(database, db) || !ReferenceEquals(transaction, value) || ++commits != 1)
                Reject(AcceptedStage.Transaction, AcceptedKind.Identity);
        }
        internal void RequireRefused(PlatformDbContext db, bool refused)
        {
            ThrowIfFailed();
            if (connection is SqlConnection)
            {
                if (provider?.HasTrustedListener != true) Reject(AcceptedStage.Provider, AcceptedKind.Incomplete);
                RequireProviderCounts();
            }
            if (!Armed || !refused || !ReferenceEquals(database, db) || starts != 1 || dispatches != 1 || executions != 1
                || rows != 1 || values != 1 || ends != 1 || !closed || transaction is null || transaction.Connection is not null
                || db.Database.CurrentTransaction is not null || db.ChangeTracker.HasChanges()
                || db.Database.GetDbConnection().State != ConnectionState.Closed) Reject(AcceptedStage.Complete, AcceptedKind.Incomplete);
            if (commits == 1) throw new OwnedAcceptedPreflightCommitted();
            // True is refused at the guard before further SQL. A final-fence
            // failure after guard deletion cannot masquerade as that refusal.
            if (laterCommands != 0) Reject(AcceptedStage.PostQuery, AcceptedKind.Incomplete);
        }
        internal void ProviderUnknown() => Mark(AcceptedStage.Provider, AcceptedKind.Shape);
        // Normalized event seam for cold controls; the runtime enters ONLY
        // through sealed, typed SqlClient payloads from its own listener.
        internal void ProviderEvent(ProviderCommandEvent evidence)
        {
            lock (providerGate)
            {
                if (!Armed) return;
                if (!Enum.IsDefined(evidence.Kind)) { Mark(AcceptedStage.Provider, AcceptedKind.Shape); return; }
                if (evidence.Kind == ProviderEventKind.Before)
                {
                    if (!ReferenceEquals(evidence.Command.Connection, connection)) return;
                    var current = database?.Database.CurrentTransaction?.GetDbTransaction();
                    if (transaction is null && current is null) return; // Before the owned unit starts.
                    if (!ReferenceEquals(current, transaction) || !ReferenceEquals(evidence.Command.Transaction, transaction))
                    {
                        // Only the fixed session restoration after complete
                        // owned-unit disposal is outside this SQL observation.
                        if (current is null && transaction?.Connection is null && evidence.Command.Transaction is null
                            && connection?.State == ConnectionState.Open && evidence.Command.CommandText is
                                ("SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;" or "SET TRANSACTION ISOLATION LEVEL READ COMMITTED;"
                                or "SET TRANSACTION ISOLATION LEVEL REPEATABLE READ;" or "SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;"
                                or "SET TRANSACTION ISOLATION LEVEL SNAPSHOT;")) return;
                        Mark(command is null ? AcceptedStage.BeforeQuery : AcceptedStage.PostQuery, AcceptedKind.Identity); return;
                    }
                    if (evidence.Id == Guid.Empty || providerCommands.Count >= 1024 || !providerCommands.TryAdd(evidence.Id, (evidence.Command, command is null ? AcceptedStage.BeforeQuery
                            : ReferenceEquals(command, evidence.Command) ? AcceptedStage.Execute : AcceptedStage.PostQuery)))
                    { Mark(AcceptedStage.Provider, AcceptedKind.Shape); return; }
                    if (ReferenceEquals(command, evidence.Command))
                    {
                        if (++providerBefore != 1) Mark(AcceptedStage.Provider, AcceptedKind.Shape);
                        targetProviderOperation = evidence.Id;
                    }
                    else if (command is not null) laterCommands++;
                    return;
                }
                if (!providerCommands.TryGetValue(evidence.Id, out var original))
                {
                    if (transaction is not null && ReferenceEquals(evidence.Command.Connection, connection)
                        && (ReferenceEquals(database?.Database.CurrentTransaction?.GetDbTransaction(), transaction)
                            || ReferenceEquals(evidence.Command.Transaction, transaction)))
                        Mark(AcceptedStage.Provider, AcceptedKind.Incomplete);
                    return;
                }
                if (!ReferenceEquals(original.Command, evidence.Command)) { Mark(AcceptedStage.Provider, AcceptedKind.Identity); return; }
                if (evidence.Kind == ProviderEventKind.Error)
                {
                    Failed(original.Stage, evidence.Error ?? new IOException());
                }
                else if (evidence.Id == targetProviderOperation && ++providerAfter != 1) Mark(AcceptedStage.Provider, AcceptedKind.Shape);
            }
        }
        internal void RequireProviderCounts()
        {
            lock (providerGate)
            {
                ThrowIfFailed();
                if (providerBefore != 1 || providerAfter != 1 || targetProviderOperation == Guid.Empty)
                    Reject(AcceptedStage.Provider, AcceptedKind.Incomplete);
            }
        }
        private void RequireIdentity(DbContext? db, DbCommand value, AcceptedStage stage)
        {
            if (!ReferenceEquals(database, db) || transaction is null || !ReferenceEquals(transaction, value.Transaction)
                || transaction.IsolationLevel != IsolationLevel.Serializable || !ReferenceEquals(value.Connection, connection)
                || connection?.State != ConnectionState.Open || database?.Database.CurrentTransaction is not { } current
                || !ReferenceEquals(current.GetDbTransaction(), transaction)
                || !HasExactPredicates(value))
                Reject(stage, AcceptedKind.Identity);
        }
        private bool HasExactPredicates(DbCommand value)
        {
            // The six original LINQ equalities must bind their OWN parameters.
            // Merely carrying six unused values cannot prove a scoped query.
            var match = Regex.Match(value.CommandText,
                @"FROM \[aioffice\]\.\[GroupModelAttemptResults\] AS (\[[A-Za-z0-9_]+\])\s+WHERE (.*?)\)\s+THEN CAST\(1 AS bit\)",
                RegexOptions.Singleline | RegexOptions.CultureInvariant);
            if (!match.Success || value.Parameters.Count != parameters.Length) return false;
            var parts = Regex.Replace(match.Groups[2].Value, @"\s+", " ").Split(" AND ", StringSplitOptions.None);
            string[] columns = ["TenantId", "CompanyId", "BindingId", "BatchId", "PlanSha256", "OperationId"];
            if (parts.Length != columns.Length) return false;
            var names = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < columns.Length; i++)
            {
                var prefix = match.Groups[1].Value + ".[" + columns[i] + "] = ";
                if (!parts[i].StartsWith(prefix, StringComparison.Ordinal)) return false;
                var name = parts[i][prefix.Length..];
                if (!Regex.IsMatch(name, @"^@[A-Za-z0-9_]+$", RegexOptions.CultureInvariant) || !names.Add(name)) return false;
                var parameter = value.Parameters.Cast<DbParameter>().SingleOrDefault(x => x.ParameterName == name);
                if (parameter is null || !Equals(parameter.Value, parameters[i])) return false;
            }
            return true;
        }
        private void Mark(AcceptedStage stage, AcceptedKind kind) => Interlocked.CompareExchange(ref failure, new(stage, kind), null);
        [DoesNotReturn]
        private void Reject(AcceptedStage stage, AcceptedKind kind) { Mark(stage, kind); throw failure!; }
    }

    internal enum ProviderEventKind { Before, After, Error }
    internal readonly record struct ProviderCommandEvent(ProviderEventKind Kind, Guid Id, DbCommand Command, Exception? Error = null)
    {
        public override string ToString() => "Owned SqlClient command diagnostic (private metadata).";
    }
    internal static ProviderCommandEvent? DecodeProviderEvent(KeyValuePair<string, object?> item) => item switch
    {
        { Key: SqlClientCommandBefore.Name, Value: SqlClientCommandBefore before } when before.Command is not null
            => new(ProviderEventKind.Before, before.OperationId, before.Command),
        { Key: SqlClientCommandAfter.Name, Value: SqlClientCommandAfter after } when after.Command is not null
            => new(ProviderEventKind.After, after.OperationId, after.Command),
        { Key: SqlClientCommandError.Name, Value: SqlClientCommandError error } when error.Command is not null && error.Exception is not null
            => new(ProviderEventKind.Error, error.OperationId, error.Command, error.Exception),
        _ => null
    };
    // SqlClient6.1.6 emits target Reader After before completing its returned
    // reader Task. True/EOF/disposal are checked independently. Raw scalar and
    // nonquery Before events detect later SQL regardless of After ordering.
    internal sealed class AcceptedProviderListener : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly AcceptedTransactionObservation observation;
        private readonly object gate = new();
        private readonly List<IDisposable> subscriptions = [];
        private readonly IDisposable all;
        private bool disposed;
        internal bool HasTrustedListener { get; private set; }
        internal AcceptedProviderListener(AcceptedTransactionObservation observation)
        { this.observation = observation; all = DiagnosticListener.AllListeners.Subscribe(this); }
        public void OnNext(DiagnosticListener listener)
        {
            lock (gate)
            {
                if (disposed || listener.Name != "SqlClientDiagnosticListener" || listener.GetType().Assembly != typeof(SqlConnection).Assembly
                    || listener.GetType().FullName != "Microsoft.Data.SqlClient.Diagnostics.SqlDiagnosticListener") return;
                HasTrustedListener = true;
                subscriptions.Add(listener.Subscribe(this, static name => name is SqlClientCommandBefore.Name or SqlClientCommandAfter.Name or SqlClientCommandError.Name));
            }
        }
        public void OnNext(KeyValuePair<string, object?> item)
        {
            lock (gate)
            {
                if (disposed) return;
                if (DecodeProviderEvent(item) is { } evidence) observation.ProviderEvent(evidence);
                else observation.ProviderUnknown();
            }
        }
        public void OnCompleted() { lock (gate) { if (!disposed) observation.ProviderUnknown(); } }
        public void OnError(Exception error) { lock (gate) { if (!disposed) observation.ProviderUnknown(); } }
        public void Dispose()
        {
            IDisposable[] owned;
            lock (gate) { if (disposed) return; disposed = true; owned = subscriptions.ToArray(); subscriptions.Clear(); }
            all.Dispose(); foreach (var subscription in owned) subscription.Dispose();
        }
    }

    // Delegates without early reads, alternate values or swallowed faults.
    private sealed class AcceptedObservedReader(DbDataReader inner, AcceptedTransactionObservation observation) : DbDataReader
    {
        private T Forward<T>(Func<T> action)
        { try { return action(); } catch (Exception error) { observation.Failed(AcceptedStage.Read, error); throw; } }
        private T Scalar<T>(int ordinal, Func<T> action)
        { var value = Forward(action); observation.Scalar(ordinal, value); return value; }
        public override bool Read() { var value = Forward(inner.Read); observation.Read(value); return value; }
        public override async Task<bool> ReadAsync(CancellationToken token)
        {
            try { var value = await inner.ReadAsync(token); observation.Read(value); return value; }
            catch (Exception error) { observation.Failed(AcceptedStage.Read, error); throw; }
        }
        public override bool GetBoolean(int ordinal) => Scalar(ordinal, () => inner.GetBoolean(ordinal));
        public override object GetValue(int ordinal) => Scalar(ordinal, () => inner.GetValue(ordinal));
        public override T GetFieldValue<T>(int ordinal) => Scalar(ordinal, () => inner.GetFieldValue<T>(ordinal));
        public override async Task<T> GetFieldValueAsync<T>(int ordinal, CancellationToken token)
        {
            try { var value = await inner.GetFieldValueAsync<T>(ordinal, token); observation.Scalar(ordinal, value); return value; }
            catch (Exception error) { observation.Failed(AcceptedStage.Read, error); throw; }
        }
        public override int GetValues(object[] values)
        {
            var count = Forward(() => inner.GetValues(values));
            if (count == 1) observation.Scalar(0, values[0]); else observation.ExtraResult(); return count;
        }
        public override object this[int ordinal] => Scalar(ordinal, () => inner[ordinal]);
        public override object this[string name] => Scalar(Forward(() => inner.GetOrdinal(name)), () => inner[name]);
        public override bool NextResult() { var value = Forward(inner.NextResult); if (value) observation.ExtraResult(); return value; }
        public override async Task<bool> NextResultAsync(CancellationToken token)
        {
            try { var value = await inner.NextResultAsync(token); if (value) observation.ExtraResult(); return value; }
            catch (Exception error) { observation.Failed(AcceptedStage.Read, error); throw; }
        }
        public override void Close()
        { try { inner.Close(); observation.Closed(); } catch (Exception error) { observation.Failed(AcceptedStage.Dispose, error); throw; } }
        public override async Task CloseAsync()
        { try { await inner.CloseAsync(); observation.Closed(); } catch (Exception error) { observation.Failed(AcceptedStage.Dispose, error); throw; } }
        protected override void Dispose(bool disposing)
        { if (disposing) { try { inner.Dispose(); observation.Closed(); } catch (Exception error) { observation.Failed(AcceptedStage.Dispose, error); throw; } } }
        public override async ValueTask DisposeAsync()
        { try { await inner.DisposeAsync(); observation.Closed(); } catch (Exception error) { observation.Failed(AcceptedStage.Dispose, error); throw; } }
        public override int Depth => Forward(() => inner.Depth);
        public override int FieldCount => Forward(() => inner.FieldCount);
        public override int VisibleFieldCount => Forward(() => inner.VisibleFieldCount);
        public override bool HasRows => Forward(() => inner.HasRows);
        public override bool IsClosed => Forward(() => inner.IsClosed);
        public override int RecordsAffected => Forward(() => inner.RecordsAffected);
        public override string GetName(int ordinal) => Forward(() => inner.GetName(ordinal));
        public override string GetDataTypeName(int ordinal) => Forward(() => inner.GetDataTypeName(ordinal));
        public override Type GetFieldType(int ordinal) => Forward(() => inner.GetFieldType(ordinal));
        public override int GetOrdinal(string name) => Forward(() => inner.GetOrdinal(name));
        public override bool IsDBNull(int ordinal) => Forward(() => inner.IsDBNull(ordinal));
        public override async Task<bool> IsDBNullAsync(int ordinal, CancellationToken token)
        { try { return await inner.IsDBNullAsync(ordinal, token); } catch (Exception error) { observation.Failed(AcceptedStage.Read, error); throw; } }
        public override byte GetByte(int ordinal) => Scalar(ordinal, () => inner.GetByte(ordinal));
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => Forward(() => inner.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length));
        public override char GetChar(int ordinal) => Scalar(ordinal, () => inner.GetChar(ordinal));
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => Forward(() => inner.GetChars(ordinal, dataOffset, buffer, bufferOffset, length));
        public override Guid GetGuid(int ordinal) => Scalar(ordinal, () => inner.GetGuid(ordinal));
        public override short GetInt16(int ordinal) => Scalar(ordinal, () => inner.GetInt16(ordinal));
        public override int GetInt32(int ordinal) => Scalar(ordinal, () => inner.GetInt32(ordinal));
        public override long GetInt64(int ordinal) => Scalar(ordinal, () => inner.GetInt64(ordinal));
        public override float GetFloat(int ordinal) => Scalar(ordinal, () => inner.GetFloat(ordinal));
        public override double GetDouble(int ordinal) => Scalar(ordinal, () => inner.GetDouble(ordinal));
        public override decimal GetDecimal(int ordinal) => Scalar(ordinal, () => inner.GetDecimal(ordinal));
        public override string GetString(int ordinal) => Scalar(ordinal, () => inner.GetString(ordinal));
        public override DateTime GetDateTime(int ordinal) => Scalar(ordinal, () => inner.GetDateTime(ordinal));
        public override DataTable? GetSchemaTable() => Forward(inner.GetSchemaTable);
        public override System.Collections.IEnumerator GetEnumerator() => Forward(inner.GetEnumerator);
    }
    private sealed class OwnedAcceptedPreflightCommitted : IOException;
    internal sealed class AcceptedTransactionProbe(AcceptedTransactionObservation observation) : DbTransactionInterceptor
    {
        public override ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection, TransactionEndEventData data,
            DbTransaction result, CancellationToken token = default)
        { observation.Started(data.Context, connection, result); return ValueTask.FromResult(result); }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData data, CancellationToken token = default)
        { observation.Committed(data.Context, transaction); return Task.CompletedTask; }
    }
}
