using System.Data.Common;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;

namespace MinhHuy.AIOffice.Platform.Persistence;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public class CustomerErpConnectionInput
{
    public string Host { get; init; } = "";
    public int Port { get; init; } = 1433;
    public string Database { get; init; } = "";
    public string UserName { get; init; } = "";
    public string Password { get; init; } = "";
    public bool Encrypt { get; init; } = true;
    public bool TrustServerCertificate { get; init; }
    public override string ToString() => "Customer ERP credentials (redacted)";

    public string BuildConnectionString()
    {
        // Only a DNS name or literal IP plus a separate port. Reject SQL client
        // aliases, named instances, protocols and connection-string injection.
        if (string.IsNullOrWhiteSpace(Host) || Host.Length > 253 || Host != Host.Trim()
            || Uri.CheckHostName(Host) == UriHostNameType.Unknown || Host.Any(c => c is ';' or ',' or '\\' or '/' or '=' || char.IsWhiteSpace(c))
            || Port is < 1 or > 65535 || string.IsNullOrWhiteSpace(Database) || Database.Length > 128
            || Database.Any(char.IsControl) || string.IsNullOrWhiteSpace(UserName) || UserName.Length > 128
            || UserName.Any(char.IsControl) || string.IsNullOrEmpty(Password) || Password.Length > 1024 || Password.Contains('\0'))
            throw new ArgumentException("Invalid ERP connection fields.");
        return new SqlConnectionStringBuilder
        {
            DataSource = $"tcp:{(Host.Contains(':') ? $"[{Host}]" : Host)},{Port}",
            InitialCatalog = Database,
            UserID = UserName,
            Password = Password,
            IntegratedSecurity = false,
            PersistSecurityInfo = false,
            Encrypt = Encrypt ? SqlConnectionEncryptOption.Mandatory : SqlConnectionEncryptOption.Optional,
            TrustServerCertificate = TrustServerCertificate,
            ConnectTimeout = 5,
            ConnectRetryCount = 0,
            Pooling = false,
            ApplicationName = "AI Office customer ERP"
        }.ConnectionString;
    }
}

/// <summary><c>Findings</c> names the profile conditions that refused the account (codes plus permission/role names),
/// so an operator can see why; it never carries server diagnostics, data or credentials.</summary>
public sealed record CustomerErpConnectionResult(string Code, string? ServerVersion = null, int? TableCount = null,
    IReadOnlyList<ErpReadOnlyFinding>? Findings = null);
public interface ICustomerErpConnectionVerifier
{
    Task<CustomerErpConnectionResult> VerifyAsync(string connectionString, CancellationToken cancellationToken);
}

public sealed class SqlCustomerErpConnectionVerifier(ISqlConnectionFactory factory) : ICustomerErpConnectionVerifier
{
    public async Task<CustomerErpConnectionResult> VerifyAsync(string connectionString, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await using var connection = factory.Create(connectionString);
            await connection.OpenAsync(deadline.Token);
            await using (var elevated = connection.CreateCommand())
            {
                elevated.CommandText = "SELECT CASE WHEN ISNULL(IS_SRVROLEMEMBER(N'sysadmin'),1)<>0 THEN 1 WHEN ISNULL(IS_ROLEMEMBER(N'db_owner'),1)<>0 THEN 2 WHEN USER_NAME()=N'dbo' THEN 3 ELSE 0 END;";
                elevated.CommandTimeout = 5;
                var level = await elevated.ExecuteScalarAsync(deadline.Token);
                if (!Equals(level, 0))
                    return new("not_read_only", Findings: [new(level switch { 1 => "sysadmin", 2 => "db_owner", 3 => "dbo_user", _ => "check_failed" }, true, null)]);
            }
            await using (var metadata = connection.CreateCommand())
            {
                metadata.CommandText = "SELECT CASE WHEN HAS_PERMS_BY_NAME(NULL,NULL,N'VIEW ANY DEFINITION')=1 AND HAS_PERMS_BY_NAME(DB_NAME(),N'DATABASE',N'VIEW DEFINITION')=1 THEN 1 ELSE 0 END;";
                metadata.CommandTimeout = 5;
                if (!Equals(await metadata.ExecuteScalarAsync(deadline.Token), 1)) return new("missing_view_definition");
            }
            // Explain a refusal by named condition. Only a write/admin right is reported as "not read-only";
            // other findings mean the database uses something outside the qualified read profile.
            var findings = await ErpReadOnlyConnectionVerifier.DiagnoseAsync(connection, deadline.Token);
            if (findings.Count > 0)
                return new(findings.Any(f => f.WriteRisk) ? "not_read_only"
                    : findings.All(f => f.Code == "missing_view_definition") ? "missing_view_definition" : "unsupported_database",
                    Findings: findings);
            return await ErpReadOnlyConnectionVerifier.ReadAsync(connection, async () =>
            {
                await using var command = connection.CreateCommand();
                command.CommandTimeout = 5;
                command.CommandText = "SELECT COUNT(*) FROM sys.tables WHERE is_ms_shipped=0;";
                var count = Convert.ToInt32(await command.ExecuteScalarAsync(deadline.Token));
                // Fixed server version, never exception/server/client supplied diagnostics.
                return new CustomerErpConnectionResult("ok", connection.ServerVersion, count);
            }, deadline.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return new("unreachable"); }
        catch (ErpReadOnlyCredentialsException) { return new("not_read_only", Findings: [new("check_failed", false, null)]); }
        catch (SqlException error) { return new(MapSqlError(error.Number)); }
        catch (Exception error) when (error is DbException or InvalidOperationException or ArgumentException) { return new("unreachable"); }
    }

    public static string MapSqlError(int number) => number switch
    {
        18456 or 18452 => "login_failed",
        4060 or 911 => "database_not_found",
        _ => "unreachable"
    };
}
