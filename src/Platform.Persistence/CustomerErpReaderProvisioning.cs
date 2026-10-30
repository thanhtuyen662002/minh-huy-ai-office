using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using Microsoft.Data.SqlClient;

namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed record CustomerErpReaderResult(string Code, string? UserName = null, string? Password = null)
{
    public override string ToString() => $"Customer ERP reader result {Code} (redacted)";
}

public interface ICustomerErpReaderProvisioner
{
    /// <summary>Uses administrator credentials once to create a new read-only login for <c>admin.Database</c>.
    /// The administrator credentials are never stored.</summary>
    Task<CustomerErpReaderResult> CreateAsync(CustomerErpConnectionInput admin, CancellationToken cancellationToken);
}

public sealed class SqlCustomerErpReaderProvisioner(ISqlConnectionFactory factory) : ICustomerErpReaderProvisioner
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";

    public async Task<CustomerErpReaderResult> CreateAsync(CustomerErpConnectionInput admin, CancellationToken cancellationToken)
    {
        var connectionString = admin.BuildConnectionString();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        // A fresh login per run never resets a reader that other saved databases on this server already use.
        var login = "aioffice_reader_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        var password = NewPassword();
        DbConnection? connection = null;
        try
        {
            connection = factory.Create(connectionString);
            await connection.OpenAsync(deadline.Token);
            var database = connection.Database;
            if (!Equals(await ScalarAsync(connection, "SELECT CASE WHEN HAS_PERMS_BY_NAME(NULL,NULL,N'ALTER ANY LOGIN')=1 AND HAS_PERMS_BY_NAME(DB_NAME(),N'DATABASE',N'ALTER ANY USER')=1 THEN 1 ELSE 0 END;", deadline.Token), 1))
                return new("not_permitted");
            // Server permissions can only be granted from master. Both names are generated here, never user input.
            connection.ChangeDatabase("master");
            await ExecuteAsync(connection, $"CREATE LOGIN [{login}] WITH PASSWORD = N'{password}', CHECK_POLICY = ON, DEFAULT_DATABASE = [master];", deadline.Token);
            try
            {
                await ExecuteAsync(connection, $"GRANT VIEW ANY DEFINITION TO [{login}];", deadline.Token);
                connection.ChangeDatabase(database);
                await ExecuteAsync(connection, $"CREATE USER [{login}] FOR LOGIN [{login}]; GRANT SELECT TO [{login}]; GRANT VIEW DEFINITION TO [{login}];", deadline.Token);
                return new("created", login, password);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Never leave a half-made login behind.
                await DropAsync(connection, login);
                throw;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return new("unreachable"); }
        catch (SqlException error) { return new(MapSqlError(error.Number)); }
        catch (Exception error) when (error is DbException or InvalidOperationException or ArgumentException) { return new("unreachable"); }
        finally { if (connection is not null) await connection.DisposeAsync(); }
    }

    public static string MapSqlError(int number) => number switch
    {
        // Permission denied, cannot alter/grant, or the principal cannot enter the database.
        15247 or 262 or 229 or 15151 or 4613 or 916 or 1031 => "not_permitted",
        15025 or 15023 => "conflict",
        15118 or 15116 or 15115 or 15114 => "unreachable",
        _ => SqlCustomerErpConnectionVerifier.MapSqlError(number)
    };

    private static string NewPassword()
    {
        // 32 characters from a SQL-literal-safe alphabet, always mixing upper, lower and digit for CHECK_POLICY.
        while (true)
        {
            var value = RandomNumberGenerator.GetString(Alphabet, 32);
            if (value.Any(char.IsUpper) && value.Any(char.IsLower) && value.Any(char.IsDigit)) return value;
        }
    }

    private static async Task<object?> ScalarAsync(DbConnection connection, string sql, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql; command.CommandTimeout = 5;
        return await command.ExecuteScalarAsync(token);
    }

    private static async Task ExecuteAsync(DbConnection connection, string sql, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql; command.CommandTimeout = 5;
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task DropAsync(DbConnection? connection, string login)
    {
        if (connection is null || connection.State != ConnectionState.Open) return;
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await ExecuteAsync(connection, $"IF USER_ID(N'{login}') IS NOT NULL DROP USER [{login}];", cleanup.Token); } catch (Exception) { }
        try { connection.ChangeDatabase("master"); await ExecuteAsync(connection, $"IF SUSER_ID(N'{login}') IS NOT NULL DROP LOGIN [{login}];", cleanup.Token); } catch (Exception) { }
    }
}
