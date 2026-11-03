using System.Data;
using System.Data.Common;

namespace MinhHuy.AIOffice.Platform.Persistence;

/// <summary>
/// Conservative ad-hoc SQL read profile. This is a live credential proof, separate
/// from platform source policy. Legacy executable modules need their own reviewed
/// versioned profile; metadata absence never establishes their safety.
/// </summary>
public static class ErpReadOnlyConnectionVerifier
{
    public const int CommandTimeoutSeconds = 10;
    internal const int MinimumMajorVersion = 15, MaximumMajorVersion = 17;

    private const string ServerAllow = "N'CONNECT SQL',N'VIEW ANY DATABASE',N'VIEW ANY DEFINITION',N'VIEW ANY SECURITY DEFINITION',"
        + "N'VIEW ANY PERFORMANCE DEFINITION',N'VIEW ANY CRYPTOGRAPHICALLY SECURED DEFINITION'";
    private const string DatabaseAllow = "N'CONNECT',N'SELECT',N'VIEW DEFINITION',N'VIEW SECURITY DEFINITION',N'VIEW PERFORMANCE DEFINITION',"
        + "N'VIEW CRYPTOGRAPHICALLY SECURED DEFINITION',N'VIEW ANY COLUMN ENCRYPTION KEY DEFINITION',N'VIEW ANY COLUMN MASTER KEY DEFINITION'";
    private const string SecurableAllow = "N'SELECT',N'VIEW DEFINITION',N'VIEW SECURITY DEFINITION',N'VIEW PERFORMANCE DEFINITION'";
    private const string ObjectName = "QUOTENAME(SCHEMA_NAME(o.schema_id))+N'.'+QUOTENAME(o.name)";
    // Bounded, nonsecret detail for an operator: permission or role names only, never data or definitions.
    // Rights on an object come from the object itself (owner or object/column GRANT) or from schema, database and
    // server scope, which their own checks cover. Only objects with such object-level sources need the per-object
    // permission functions, which cost about a millisecond each on large ERP catalogs.
    private const string ObjectRightsSource = "(o.principal_id IN (SELECT principal_id FROM sys.user_token) OR EXISTS (SELECT 1 FROM sys.database_permissions dp WHERE dp.class=1 AND dp.major_id=o.object_id AND dp.state IN ('G','W') AND dp.grantee_principal_id IN (SELECT principal_id FROM sys.user_token)))";
    private static string Names(string query) => $"(SELECT LEFT(STRING_AGG(CONVERT(nvarchar(max),n),N', '),300) FROM (SELECT DISTINCT TOP (12) n FROM ({query}) d(n) ORDER BY n) x)";

    /// <summary>One named condition of the read-only profile. <c>WriteRisk</c> marks rights that could change
    /// data or security; other failures mean the database uses something outside the qualified profile.</summary>
    internal sealed record Check(string Code, bool WriteRisk, string Condition, string? Detail = null);

    // Every condition must be true. Each is evaluated on the live connection, never cached.
    internal static readonly Check[] Checks =
    [
        // Engine 15-17 share the catalog views and permission names used below; unknown permissions still fail closed.
        new("sql_version", false, $"CONVERT(int,SERVERPROPERTY(N'ProductMajorVersion')) BETWEEN {MinimumMajorVersion} AND {MaximumMajorVersion}",
            "CONVERT(nvarchar(20),SERVERPROPERTY(N'ProductVersion'))"),
        // Availability-group ownership is outside the qualified standalone profile; a filtered AG catalog cannot prove ownership absence.
        new("availability_group", false, "CONVERT(int,SERVERPROPERTY(N'IsHadrEnabled'))=0"),
        new("system_database", false, "DB_ID()>4"),
        new("dbo_user", true, "USER_NAME()<>N'dbo'"),
        new("sysadmin", true, "ISNULL(IS_SRVROLEMEMBER(N'sysadmin'),1)=0"),
        new("db_owner", true, "ISNULL(IS_ROLEMEMBER(N'db_owner'),1)=0"),
        // Fixed roles can administer membership without an explicit GRANT row. The qualified bootstrap uses direct grants.
        new("fixed_server_role", true, """
            NOT EXISTS (SELECT 1 FROM sys.server_principals p JOIN sys.login_token t ON t.principal_id=p.principal_id
              WHERE p.is_fixed_role=1 AND p.name<>N'public')
            """, Names("SELECT p.name FROM sys.server_principals p JOIN sys.login_token t ON t.principal_id=p.principal_id WHERE p.is_fixed_role=1 AND p.name<>N'public'")),
        new("fixed_database_role", true, """
            NOT EXISTS (SELECT 1 FROM sys.database_principals p JOIN sys.user_token t ON t.principal_id=p.principal_id
              WHERE p.is_fixed_role=1 AND p.name<>N'public')
            """, Names("SELECT p.name FROM sys.database_principals p JOIN sys.user_token t ON t.principal_id=p.principal_id WHERE p.is_fixed_role=1 AND p.name<>N'public'")),
        new("missing_view_definition", false, """
            HAS_PERMS_BY_NAME(NULL,NULL,N'VIEW ANY DEFINITION')=1
              AND HAS_PERMS_BY_NAME(DB_NAME(),N'DATABASE',N'VIEW DEFINITION')=1
              AND HAS_PERMS_BY_NAME(DB_NAME(),N'DATABASE',N'CONNECT')=1
              AND EXISTS (SELECT 1 FROM sys.fn_my_permissions(NULL,N'DATABASE') WHERE permission_name=N'VIEW DEFINITION')
              AND EXISTS (SELECT 1 FROM sys.fn_my_permissions(NULL,N'SERVER') WHERE permission_name=N'CONNECT SQL')
            """),
        // Object/schema metadata DENY can hide an executable despite the database grant.
        new("metadata_deny", false, """
            NOT EXISTS (SELECT 1 FROM sys.database_permissions p JOIN sys.user_token t ON t.principal_id=p.grantee_principal_id
              WHERE p.state='D' AND p.permission_name IN (N'VIEW DEFINITION',N'VIEW SECURITY DEFINITION',N'VIEW PERFORMANCE DEFINITION'))
            AND NOT EXISTS (SELECT 1 FROM sys.server_permissions p JOIN sys.login_token t ON t.principal_id=p.grantee_principal_id
              WHERE p.state='D' AND p.permission_name IN
                (N'VIEW DEFINITION',N'VIEW ANY DEFINITION',N'VIEW ANY SECURITY DEFINITION',N'VIEW ANY PERFORMANCE DEFINITION'))
            """),
        // Effective-name checks omit grant options and rights on subordinate securables. Inspect every token, including nested roles/public.
        new("grant_option", true, """
            NOT EXISTS (SELECT 1 FROM sys.database_permissions p JOIN sys.user_token t ON t.principal_id=p.grantee_principal_id WHERE p.state='W')
            AND NOT EXISTS (SELECT 1 FROM sys.server_permissions p JOIN sys.login_token t ON t.principal_id=p.grantee_principal_id WHERE p.state='W')
            """),
        new("database_grant_scope", true, """
            NOT EXISTS (SELECT 1 FROM sys.database_permissions p JOIN sys.user_token t ON t.principal_id=p.grantee_principal_id
              WHERE p.state='G' AND p.class NOT IN (0,1,3))
            """, Names("SELECT p.class_desc+N':'+p.permission_name FROM sys.database_permissions p JOIN sys.user_token t ON t.principal_id=p.grantee_principal_id WHERE p.state='G' AND p.class NOT IN (0,1,3)")),
        new("server_grant_scope", true, """
            NOT EXISTS (SELECT 1 FROM sys.server_permissions p JOIN sys.login_token t ON t.principal_id=p.grantee_principal_id
              WHERE p.state='G' AND p.class<>100
                AND NOT (p.class=105 AND p.permission_name=N'CONNECT'
                  AND EXISTS (SELECT 1 FROM sys.endpoints e WHERE e.endpoint_id=p.major_id AND e.type=2)))
            """, Names("""
                SELECT p.class_desc+N':'+p.permission_name FROM sys.server_permissions p JOIN sys.login_token t ON t.principal_id=p.grantee_principal_id
                WHERE p.state='G' AND p.class<>100 AND NOT (p.class=105 AND p.permission_name=N'CONNECT'
                  AND EXISTS (SELECT 1 FROM sys.endpoints e WHERE e.endpoint_id=p.major_id AND e.type=2))
                """)),
        // Ownership implies CONTROL without an explicit permission row.
        new("ownership", true, """
            NOT EXISTS (SELECT 1 FROM sys.server_principals p JOIN sys.login_token t ON t.principal_id=p.owning_principal_id)
            AND NOT EXISTS (SELECT 1 FROM sys.endpoints e JOIN sys.login_token t ON t.principal_id=e.principal_id)
            AND NOT EXISTS (SELECT 1 FROM (
              SELECT owning_principal_id AS owner_id FROM sys.database_principals
              UNION ALL SELECT principal_id FROM sys.types
              UNION ALL SELECT principal_id FROM sys.assemblies
              UNION ALL SELECT principal_id FROM sys.xml_schema_collections
              UNION ALL SELECT principal_id FROM sys.service_message_types
              UNION ALL SELECT principal_id FROM sys.service_contracts
              UNION ALL SELECT principal_id FROM sys.services
              UNION ALL SELECT principal_id FROM sys.remote_service_bindings
              UNION ALL SELECT principal_id FROM sys.routes
              UNION ALL SELECT principal_id FROM sys.fulltext_catalogs
              UNION ALL SELECT principal_id FROM sys.symmetric_keys
              UNION ALL SELECT principal_id FROM sys.certificates
              UNION ALL SELECT principal_id FROM sys.asymmetric_keys
              UNION ALL SELECT principal_id FROM sys.fulltext_stoplists
              UNION ALL SELECT principal_id FROM sys.registered_search_property_lists
              UNION ALL SELECT principal_id FROM sys.external_languages
              UNION ALL SELECT principal_id FROM sys.external_libraries
            ) o JOIN sys.user_token t ON t.principal_id=o.owner_id)
            AND NOT EXISTS (SELECT 1 FROM sys.database_scoped_credentials c
              WHERE ISNULL(HAS_PERMS_BY_NAME(c.name,N'DATABASE SCOPED CREDENTIAL',N'CONTROL'),1)<>0)
            """),
        new("server_permission", true, $"NOT EXISTS (SELECT 1 FROM sys.fn_my_permissions(NULL,N'SERVER') WHERE permission_name NOT IN ({ServerAllow}))",
            Names($"SELECT permission_name FROM sys.fn_my_permissions(NULL,N'SERVER') WHERE permission_name NOT IN ({ServerAllow})")),
        new("database_permission", true, $"NOT EXISTS (SELECT 1 FROM sys.fn_my_permissions(NULL,N'DATABASE') WHERE permission_name NOT IN ({DatabaseAllow}))",
            Names($"SELECT permission_name FROM sys.fn_my_permissions(NULL,N'DATABASE') WHERE permission_name NOT IN ({DatabaseAllow})")),
        new("impersonate", true, """
            NOT EXISTS (SELECT 1 FROM sys.server_principals p
              WHERE p.type IN ('S','U','G','E','X') AND p.principal_id<>SUSER_ID()
                AND ISNULL(HAS_PERMS_BY_NAME(p.name,N'LOGIN',N'IMPERSONATE'),1)<>0)
            AND NOT EXISTS (SELECT 1 FROM sys.database_principals p
              WHERE p.type IN ('S','U','G','E','X') AND p.principal_id<>USER_ID()
                AND ISNULL(HAS_PERMS_BY_NAME(p.name,N'USER',N'IMPERSONATE'),1)<>0)
            """),
        new("role_admin", true, """
            NOT EXISTS (SELECT 1 FROM sys.database_principals p WHERE p.type='R'
              AND (ISNULL(HAS_PERMS_BY_NAME(p.name,N'ROLE',N'ALTER'),1)<>0
                OR ISNULL(HAS_PERMS_BY_NAME(p.name,N'ROLE',N'CONTROL'),1)<>0
                OR ISNULL(HAS_PERMS_BY_NAME(p.name,N'ROLE',N'TAKE OWNERSHIP'),1)<>0))
            """),
        new("schema_hidden", false, "NOT EXISTS (SELECT 1 FROM sys.schemas s WHERE ISNULL(HAS_PERMS_BY_NAME(s.name,N'SCHEMA',N'VIEW DEFINITION'),0)<>1)"),
        new("schema_permission", true, $"""
            NOT EXISTS (SELECT 1 FROM sys.schemas s CROSS APPLY sys.fn_my_permissions(s.name,N'SCHEMA') p
              WHERE p.permission_name NOT IN ({SecurableAllow}))
            """, Names($"SELECT p.permission_name FROM sys.schemas s CROSS APPLY sys.fn_my_permissions(s.name,N'SCHEMA') p WHERE p.permission_name NOT IN ({SecurableAllow})")),
        new("object_hidden", false, $"""
            NOT EXISTS (SELECT 1 FROM sys.objects o WHERE o.is_ms_shipped=0
              AND o.type IN ('U','V','P','PC','FN','FS','FT','AF','TF','IF','SN','SO','SQ')
              AND ISNULL(HAS_PERMS_BY_NAME({ObjectName},N'OBJECT',N'VIEW DEFINITION'),0)<>1)
            """),
        // Executable-only rights (EXECUTE on a scalar function) are not write rights; procedures are checked below.
        new("object_permission", true, $"""
            NOT EXISTS (SELECT 1 FROM sys.objects o CROSS APPLY sys.fn_my_permissions({ObjectName},N'OBJECT') p
              WHERE o.is_ms_shipped=0 AND o.type IN ('U','V','P','PC','FN','FS','FT','AF','TF','IF','SN','SO','SQ') AND {ObjectRightsSource}
                AND p.permission_name NOT IN ({SecurableAllow})
                AND NOT (o.type IN ('FN','IF','TF') AND p.permission_name IN (N'EXECUTE',N'REFERENCES')))
            """, Names($"""
                SELECT p.permission_name FROM sys.objects o CROSS APPLY sys.fn_my_permissions({ObjectName},N'OBJECT') p
                WHERE o.is_ms_shipped=0 AND o.type IN ('U','V','P','PC','FN','FS','FT','AF','TF','IF','SN','SO','SQ') AND {ObjectRightsSource}
                  AND p.permission_name NOT IN ({SecurableAllow}) AND NOT (o.type IN ('FN','IF','TF') AND p.permission_name IN (N'EXECUTE',N'REFERENCES'))
                """)),
        // Column GRANT can override a table DENY. Check effective column rights.
        new("column_update", true, $"""
            NOT EXISTS (SELECT 1 FROM sys.columns c JOIN sys.objects o ON o.object_id=c.object_id
              WHERE o.is_ms_shipped=0 AND {ObjectRightsSource} AND ISNULL(HAS_PERMS_BY_NAME({ObjectName},N'OBJECT',N'UPDATE',c.name,N'COLUMN'),1)<>0)
            """),
        // Procedures and CLR modules may write through ownership chaining or elevated execution. This profile executes none.
        new("executable_procedure", true, $"""
            NOT EXISTS (SELECT 1 FROM sys.objects o WHERE o.is_ms_shipped=0 AND o.type IN ('P','PC','FS','FT','AF')
              AND (ISNULL(HAS_PERMS_BY_NAME({ObjectName},N'OBJECT',N'EXECUTE'),1)<>0
                OR ISNULL(HAS_PERMS_BY_NAME({ObjectName},N'OBJECT',N'SELECT'),1)<>0))
            """),
        // T-SQL functions cannot change database state (side-effecting statements are rejected at CREATE time), so
        // ordinary ERP functions are allowed. CLR functions can, and stay outside the profile.
        new("clr_function", false, "NOT EXISTS (SELECT 1 FROM sys.objects o WHERE o.is_ms_shipped=0 AND o.type IN ('FS','FT','AF'))"),
        new("extended_procedure", false, "NOT EXISTS (SELECT 1 FROM sys.objects o WHERE o.is_ms_shipped=0 AND o.type IN ('X','RF'))"),
        // Synonyms can hide cross-database/linked-server targets and permissions.
        new("synonym", false, "NOT EXISTS (SELECT 1 FROM sys.synonyms)"),
        // Views and functions that read another database or a linked server are reads, not write rights: SQL Server
        // checks the reader's own permissions there unless ownership chaining crosses databases. Refuse only the
        // settings that let this database's module owners lend their rights elsewhere.
        new("cross_database_chaining", false, """
            ISNULL((SELECT CONVERT(int,value_in_use) FROM sys.configurations WHERE name=N'cross db ownership chaining'),1)=0
            AND NOT EXISTS (SELECT 1 FROM sys.databases WHERE database_id=DB_ID() AND (is_db_chaining_on=1 OR is_trustworthy_on=1))
            """),
        new("view_external_rowset", false, """
            NOT EXISTS (SELECT 1 FROM sys.sql_modules m JOIN sys.objects o ON o.object_id=m.object_id
              WHERE o.is_ms_shipped=0 AND o.type IN ('V','FN','IF','TF')
                AND (m.definition IS NULL OR EXISTS (SELECT 1 FROM (SELECT UPPER(m.definition) COLLATE Latin1_General_100_BIN2) u(d)
                  WHERE u.d LIKE N'%OPENQUERY%' OR u.d LIKE N'%OPENROWSET%' OR u.d LIKE N'%OPENDATASOURCE%')))
            """),
        new("external_table", false, "NOT EXISTS (SELECT 1 FROM sys.external_tables)"),
        new("clr_assembly", false, "NOT EXISTS (SELECT 1 FROM sys.assemblies WHERE is_user_defined=1) AND NOT EXISTS (SELECT 1 FROM sys.types WHERE is_user_defined=1 AND is_assembly_type=1)"),
    ];

    // CI smoke scripts run this exact text from ErpReadOnlyVerification.sql; a unit test keeps that file in sync.
    internal static readonly string VerificationSql =
        "SELECT CASE WHEN\n          " + string.Join("\n          AND ", Checks.Select(c => $"({c.Condition.Trim().Replace("\r", "")})"))
        + "\n          THEN 1 ELSE 0 END;";

    // One row per failed condition. A NULL condition fails, exactly as in VerificationSql.
    internal static readonly string DiagnosticSql = string.Join("\nUNION ALL\n", Checks.Select(c =>
        $"SELECT N'{c.Code}' AS code, CONVERT(nvarchar(300),{c.Detail ?? "NULL"}) COLLATE DATABASE_DEFAULT AS detail WHERE CASE WHEN ({c.Condition.Trim()}) THEN 1 ELSE 0 END=0")) + ";";

    internal const string VersionSql = "SELECT CONVERT(int,SERVERPROPERTY(N'ProductMajorVersion')), CONVERT(nvarchar(20),SERVERPROPERTY(N'ProductVersion'));";

    /// <summary>Explains a refused credential for the connection-test UI: which named conditions failed, with
    /// permission/role names where they help. Never used to grant access; reads still go through <see cref="RequireAsync"/>.</summary>
    public static async Task<IReadOnlyList<ErpReadOnlyFinding>> DiagnoseAsync(DbConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        try
        {
            await using (var version = connection.CreateCommand())
            {
                version.CommandText = VersionSql;
                version.CommandTimeout = CommandTimeoutSeconds;
                await using var reader = await version.ExecuteReaderAsync(cancellationToken);
                // Older engines lack catalog views used by the profile; the full batch would not even compile there.
                if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(0)
                    || reader.GetInt32(0) is < MinimumMajorVersion or > MaximumMajorVersion)
                    return [new("sql_version", false, reader.IsDBNull(1) ? null : reader.GetString(1))];
            }
            var findings = new List<ErpReadOnlyFinding>();
            await using var command = connection.CreateCommand();
            command.CommandText = DiagnosticSql;
            command.CommandTimeout = CommandTimeoutSeconds * 4;
            await using (var rows = await command.ExecuteReaderAsync(cancellationToken))
                while (await rows.ReadAsync(cancellationToken) && findings.Count < Checks.Length)
                {
                    var code = rows.GetString(0);
                    var check = Checks.FirstOrDefault(c => c.Code == code);
                    if (check is null) continue;
                    findings.Add(new(check.Code, check.WriteRisk, rows.IsDBNull(1) ? null : Bound(rows.GetString(1))));
                }
            return findings;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OperationCanceledException and not Microsoft.Data.SqlClient.SqlException { Number: -2 })
        {
            // A command timeout propagates so the caller can say the check was slow. Diagnostics never grant access; any other failure is reported as a fixed code without server text.
            return [new("check_failed", false, null)];
        }
    }

    private static string? Bound(string value)
    {
        var text = new string(value.Where(c => !char.IsControl(c)).Take(300).ToArray()).Trim();
        return text.Length == 0 ? null : text;
    }

    public static async Task RequireAsync(DbConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (connection.State != ConnectionState.Open) throw Unqualified();
            await using var command = connection.CreateCommand();
            command.CommandText = VerificationSql;
            // Large ERP catalogs (tens of thousands of modules) take seconds to prove; data reads keep their own limit.
            command.CommandTimeout = CommandTimeoutSeconds * 3;
            if (!Equals(await command.ExecuteScalarAsync(cancellationToken), 1)) throw Unqualified();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is DbException or InvalidOperationException or ArgumentException)
        {
            throw Unqualified();
        }
    }

    public static async Task<T> ReadAsync<T>(DbConnection connection, Func<Task<T>> read,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(read);
        await RequireAsync(connection, cancellationToken);
        var evidence = await read();
        // The callback must finish/dispose its reader before this live proof.
        // A mid-read privilege change discards the evidence instead of exposing it.
        await RequireAsync(connection, cancellationToken);
        return evidence;
    }

    public static ErpReadOnlyCredentialsException Unqualified() => new();
}

public sealed record ErpReadOnlyFinding(string Code, bool WriteRisk, string? Detail);

public sealed class ErpReadOnlyCredentialsException() : UnauthorizedAccessException(
    "ERP read credentials are not qualified for read-only use.");
