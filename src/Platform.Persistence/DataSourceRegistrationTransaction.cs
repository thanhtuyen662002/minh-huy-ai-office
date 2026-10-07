using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace MinhHuy.AIOffice.Platform.Persistence;

// SQL isolation is connection session state, not merely transaction state.
// Keep the connection pinned until rollback/commit and restoration finish.
public sealed class DataSourceRegistrationTransaction : IAsyncDisposable
{
    private readonly PlatformDbContext database;
    private readonly DbConnection? connection;
    private readonly bool ownsConnection;
    private readonly bool sqlServer;
    private int restoreLevel = 2;
    private IDbContextTransaction? transaction;
    private bool disposed;
    private bool restoreRequired;

    private DataSourceRegistrationTransaction(PlatformDbContext database)
    {
        this.database = database;
        if (!database.Database.IsRelational()) return;
        connection = database.Database.GetDbConnection();
        ownsConnection = connection.State != ConnectionState.Open;
        sqlServer = database.Database.IsSqlServer();
        restoreRequired = ownsConnection && sqlServer;
    }

    public static async Task<DataSourceRegistrationTransaction> BeginAsync(PlatformDbContext database, CancellationToken cancellationToken = default)
    {
        if (database.Database.CurrentTransaction is not null || System.Transactions.Transaction.Current is not null)
            throw new InvalidOperationException("Registration requires its own transaction.");
        var scope = new DataSourceRegistrationTransaction(database);
        if (scope.connection is null) return scope;
        try
        {
            if (scope.ownsConnection) await database.Database.OpenConnectionAsync(cancellationToken);
            else if (scope.sqlServer)
            {
                // Preserve an explicitly caller-owned open session. A newly
                // rented connection always returns to our ReadCommitted baseline.
                await using var command = scope.connection.CreateCommand();
                command.CommandText = "SELECT transaction_isolation_level FROM sys.dm_exec_sessions WHERE session_id=@@SPID;";
                command.CommandTimeout = 5;
                var previousLevel = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
                if (previousLevel is < 1 or > 5) throw DataSourceSecretBindingService.Unavailable();
                scope.restoreLevel = previousLevel;
            }
            scope.restoreRequired = scope.sqlServer;
            scope.transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            return scope;
        }
        catch
        {
            await scope.DisposeAsync();
            throw;
        }
    }

    public Task CommitAsync(CancellationToken cancellationToken = default) =>
        transaction?.CommitAsync(cancellationToken) ?? Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        if (connection is null) return;
        var discard = false;
        try
        {
            if (transaction is not null) await transaction.DisposeAsync();
            transaction = null;
            if (restoreRequired && connection.State == ConnectionState.Open)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = restoreLevel switch
                {
                    1 => "SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;",
                    2 => "SET TRANSACTION ISOLATION LEVEL READ COMMITTED;",
                    3 => "SET TRANSACTION ISOLATION LEVEL REPEATABLE READ;",
                    4 => "SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;",
                    5 => "SET TRANSACTION ISOLATION LEVEL SNAPSHOT;",
                    _ => throw DataSourceSecretBindingService.Unavailable()
                };
                command.CommandTimeout = 5;
                // Caller cancellation must not leave a poisoned pooled session.
                await command.ExecuteNonQueryAsync(CancellationToken.None);
            }
        }
        catch (Exception error) when (error is DbException or InvalidOperationException)
        {
            discard = true;
            // Do not let another request inherit unsafe session state even if
            // restoration/rollback fails. This invalidates this pool, not data.
            if (connection is SqlConnection sql) SqlConnection.ClearPool(sql);
        }
        finally
        {
            if (ownsConnection) await database.Database.CloseConnectionAsync();
            else if (discard) await connection.CloseAsync();
        }
    }
}
