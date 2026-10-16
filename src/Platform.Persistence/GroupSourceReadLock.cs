using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

// A transaction/scope-bound lock mode, never a cached permission verdict.
// Only the accepted-result read unit acquires it. Every fence still executes
// its fresh effective-rights proof and actual Shared transaction lock command.
internal sealed class GroupSourceReadLock
{
    private static readonly ConditionalWeakTable<DbTransaction, GroupSourceReadLock> ReadTransactions = new();
    private readonly PlatformDbContext database;
    private readonly DbTransaction transaction;
    private readonly GroupScope scope;

    private GroupSourceReadLock(PlatformDbContext database, DbTransaction transaction, GroupScope scope)
    { this.database = database; this.transaction = transaction; this.scope = scope; }

    internal static async Task<GroupSourceReadLock> AcquireAsync(PlatformDbContext database, GroupScope scope, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); scope.Validate();
        var transaction = RequireTransaction(database);
        await GroupSourceTransactionLock.RequireSharedAsync(database, scope, token);
        var read = new GroupSourceReadLock(database, transaction, scope);
        ReadTransactions.Add(transaction, read);
        return read;
    }

    internal async Task RequireAsync(PlatformDbContext current, GroupScope source, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(current, database) || source != scope
            || !ReferenceEquals(RequireTransaction(current), transaction)) throw Unavailable();
        await GroupSourceTransactionLock.RequireSharedAsync(current, source, token);
    }

    internal static bool IsReadTransaction(DbTransaction transaction) => ReadTransactions.TryGetValue(transaction, out _);

    private static DbTransaction RequireTransaction(PlatformDbContext database)
    {
        if (!database.Database.IsSqlServer() || database.Database.CurrentTransaction is not { } sql
            || !sql.SupportsSavepoints || System.Transactions.Transaction.Current is not null
            || database.ChangeTracker.HasChanges()) throw Unavailable();
        var transaction = sql.GetDbTransaction();
        if (transaction.IsolationLevel != IsolationLevel.Serializable
            || !ReferenceEquals(transaction.Connection, database.Database.GetDbConnection())
            || transaction.Connection?.State != ConnectionState.Open) throw Unavailable();
        return transaction;
    }
    private static InvalidOperationException Unavailable() => new("Group original result read transaction is unavailable.");
}
