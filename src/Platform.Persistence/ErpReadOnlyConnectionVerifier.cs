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
    internal const string VerificationSql = """
        SELECT CASE WHEN
          USER_NAME() <> N'dbo'
          AND ISNULL(IS_SRVROLEMEMBER(N'sysadmin'),1)=0
          AND ISNULL(IS_ROLEMEMBER(N'db_owner'),1)=0
          AND HAS_PERMS_BY_NAME(NULL,NULL,N'VIEW ANY DEFINITION')=1
          AND HAS_PERMS_BY_NAME(DB_NAME(),N'DATABASE',N'VIEW DEFINITION')=1
          AND HAS_PERMS_BY_NAME(DB_NAME(),N'DATABASE',N'CONNECT')=1
          -- Object/schema metadata DENY can hide an executable despite the
          -- database grant. Inspect denies for every effective principal token.
          AND NOT EXISTS (SELECT 1 FROM sys.database_permissions p
            JOIN sys.user_token t ON t.principal_id=p.grantee_principal_id
            WHERE p.state='D' AND p.permission_name IN
              (N'VIEW DEFINITION',N'VIEW SECURITY DEFINITION',N'VIEW PERFORMANCE DEFINITION'))
          AND EXISTS (SELECT 1 FROM sys.fn_my_permissions(NULL,N'SERVER')
            WHERE permission_name=N'CONNECT SQL')
          AND NOT EXISTS (SELECT 1 FROM sys.fn_my_permissions(NULL,N'SERVER')
            WHERE permission_name NOT IN (N'CONNECT SQL',N'VIEW ANY DATABASE',
              N'VIEW ANY DEFINITION',N'VIEW ANY SECURITY DEFINITION',N'VIEW ANY PERFORMANCE DEFINITION'))
          AND EXISTS (SELECT 1 FROM sys.fn_my_permissions(NULL,N'DATABASE')
            WHERE permission_name=N'VIEW DEFINITION')
          AND NOT EXISTS (SELECT 1 FROM sys.fn_my_permissions(NULL,N'DATABASE')
            WHERE permission_name NOT IN (N'CONNECT',N'SELECT',N'VIEW DEFINITION',
              N'VIEW SECURITY DEFINITION',N'VIEW PERFORMANCE DEFINITION',
              N'VIEW ANY COLUMN ENCRYPTION KEY DEFINITION',N'VIEW ANY COLUMN MASTER KEY DEFINITION'))
          AND NOT EXISTS (SELECT 1 FROM sys.server_principals p
            WHERE p.type IN ('S','U','G','E','X') AND p.principal_id<>SUSER_ID()
              AND ISNULL(HAS_PERMS_BY_NAME(p.name,N'LOGIN',N'IMPERSONATE'),1)<>0)
          AND NOT EXISTS (SELECT 1 FROM sys.database_principals p
            WHERE p.type IN ('S','U','G','E','X') AND p.principal_id<>USER_ID()
              AND ISNULL(HAS_PERMS_BY_NAME(p.name,N'USER',N'IMPERSONATE'),1)<>0)
          AND NOT EXISTS (SELECT 1 FROM sys.database_principals p WHERE p.type='R'
            AND (ISNULL(HAS_PERMS_BY_NAME(p.name,N'ROLE',N'ALTER'),1)<>0
              OR ISNULL(HAS_PERMS_BY_NAME(p.name,N'ROLE',N'CONTROL'),1)<>0
              OR ISNULL(HAS_PERMS_BY_NAME(p.name,N'ROLE',N'TAKE OWNERSHIP'),1)<>0))
          AND NOT EXISTS (SELECT 1 FROM sys.schemas s
            WHERE ISNULL(HAS_PERMS_BY_NAME(s.name,N'SCHEMA',N'VIEW DEFINITION'),0)<>1
              OR EXISTS (SELECT 1 FROM sys.fn_my_permissions(s.name,N'SCHEMA')
                WHERE permission_name NOT IN (N'SELECT',N'VIEW DEFINITION',
                  N'VIEW SECURITY DEFINITION',N'VIEW PERFORMANCE DEFINITION')))
          AND NOT EXISTS (SELECT 1 FROM sys.objects o WHERE o.is_ms_shipped=0
            AND o.type IN ('U','V','P','PC','FN','FS','FT','AF','TF','IF','SN','SO','SQ')
            AND (ISNULL(HAS_PERMS_BY_NAME(QUOTENAME(SCHEMA_NAME(o.schema_id))+N'.'+QUOTENAME(o.name),
                  N'OBJECT',N'VIEW DEFINITION'),0)<>1
              OR EXISTS (SELECT 1 FROM sys.fn_my_permissions(
                QUOTENAME(SCHEMA_NAME(o.schema_id))+N'.'+QUOTENAME(o.name),N'OBJECT')
                WHERE permission_name NOT IN (N'SELECT',N'VIEW DEFINITION',
                  N'VIEW SECURITY DEFINITION',N'VIEW PERFORMANCE DEFINITION'))))
          -- Column GRANT can override a table DENY. Check effective column rights.
          AND NOT EXISTS (SELECT 1 FROM sys.columns c JOIN sys.objects o ON o.object_id=c.object_id
            WHERE o.is_ms_shipped=0
              AND ISNULL(HAS_PERMS_BY_NAME(QUOTENAME(SCHEMA_NAME(o.schema_id))+N'.'+QUOTENAME(o.name),
                N'OBJECT',N'UPDATE',c.name,N'COLUMN'),1)<>0)
          -- Procedures, CLR/SQL functions and aggregates may bypass direct rights
          -- through ownership/elevated execution. This profile executes none.
          AND NOT EXISTS (SELECT 1 FROM sys.objects o
            WHERE o.is_ms_shipped=0 AND o.type IN ('P','PC','FN','FS','FT','AF','TF','IF')
              AND (ISNULL(HAS_PERMS_BY_NAME(QUOTENAME(SCHEMA_NAME(o.schema_id))+N'.'+QUOTENAME(o.name),
                    N'OBJECT',N'EXECUTE'),1)<>0
                OR ISNULL(HAS_PERMS_BY_NAME(QUOTENAME(SCHEMA_NAME(o.schema_id))+N'.'+QUOTENAME(o.name),
                    N'OBJECT',N'SELECT'),1)<>0))
          -- A view/computed expression can call a function without a direct
          -- function grant. Such module graphs require a separate qualification.
          AND NOT EXISTS (SELECT 1 FROM sys.objects o WHERE o.is_ms_shipped=0
            AND o.type IN ('FN','FS','FT','AF','TF','IF'))
          -- Synonyms can hide cross-database/linked-server targets and permissions.
          AND NOT EXISTS (SELECT 1 FROM sys.synonyms)
          THEN 1 ELSE 0 END;
        """;

    public static async Task RequireAsync(DbConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (connection.State != ConnectionState.Open) throw Unqualified();
            await using var command = connection.CreateCommand();
            command.CommandText = VerificationSql;
            command.CommandTimeout = CommandTimeoutSeconds;
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

public sealed class ErpReadOnlyCredentialsException() : UnauthorizedAccessException(
    "ERP read credentials are not qualified for read-only use.");
