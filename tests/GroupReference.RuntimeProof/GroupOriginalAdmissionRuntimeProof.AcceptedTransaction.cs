using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MinhHuy.AIOffice.Platform.Persistence;

namespace MinhHuy.AIOffice.GroupReference.RuntimeProof;

internal static partial class GroupOriginalAdmissionRuntimeProof
{
    // Test-only per-invocation SQL observation. No old execution completion
    // flags, carrier state or manually claimed refusal qualify this check.
    internal sealed class AcceptedTransactionObservation
    {
        private DbTransaction? transaction;
        private int starts, queries, commits;
        internal bool Armed { get; private set; }
        internal void Begin() { if (Armed) throw Invalid(); transaction = null; starts = queries = commits = 0; Armed = true; }
        internal void End() => Armed = false;
        internal void Started(DbTransaction value)
        {
            if (!Armed) return;
            if (++starts != 1 || value.IsolationLevel != IsolationLevel.Serializable) throw Invalid();
            transaction = value;
        }
        internal void Query(DbTransaction? value, string command)
        {
            if (!Armed || !command.Contains("[aioffice].[GroupModelAttemptResults]", StringComparison.Ordinal)) return;
            if (transaction is null || !ReferenceEquals(transaction, value) || !command.Contains("EXISTS", StringComparison.Ordinal)) throw Invalid();
            queries++;
        }
        internal void Committed(DbTransaction value)
        { if (Armed) { if (!ReferenceEquals(transaction, value)) throw Invalid(); commits++; } }
        internal void RequireRefused(PlatformDbContext db)
        {
            if (!Armed || starts != 1 || queries != 1 || transaction is null || transaction.Connection is not null
                || db.Database.CurrentTransaction is not null || db.ChangeTracker.HasChanges()
                || db.Database.GetDbConnection().State != ConnectionState.Closed) throw Invalid();
            if (commits != 0) throw new OwnedAcceptedPreflightCommitted();
        }
        private static InvalidOperationException Invalid() => new("Owned accepted-result transaction observation is not available.");
    }
    private sealed class OwnedAcceptedPreflightCommitted : IOException;
    private sealed class AcceptedTransactionProbe(AcceptedTransactionObservation observation) : DbTransactionInterceptor
    {
        public override ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection, TransactionEndEventData data,
            DbTransaction result, CancellationToken token = default)
        { observation.Started(result); return ValueTask.FromResult(result); }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData data, CancellationToken token = default)
        { observation.Committed(transaction); return Task.CompletedTask; }
    }
}
