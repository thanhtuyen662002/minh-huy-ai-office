using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed class BindingStorePermissionVerifier(PlatformDbContext database)
{
    public const string RuntimeRole = "aioffice_binding_runtime";
    public const string Table = "aioffice.DataSourceSecretBindings";

    // Check effective rights as the current connection identity, including SQL
    // Server's column-GRANT exception to a table-level DENY. Never cache a grant.
    internal const string VerificationSql = """
        SELECT CASE WHEN OBJECT_ID(N'aioffice.DataSourceSecretBindings',N'U') IS NOT NULL
          AND EXISTS (SELECT 1 FROM sys.objects o WHERE o.object_id=OBJECT_ID(N'aioffice.DataSourceSecretBindings')
            AND o.principal_id=DATABASE_PRINCIPAL_ID(N'aioffice_binding_operator_owner'))
          AND IS_ROLEMEMBER(N'aioffice_binding_runtime')=1
          AND USER_NAME()<>N'dbo'
          AND IS_SRVROLEMEMBER(N'sysadmin')=0
          AND IS_SRVROLEMEMBER(N'dbcreator')=0
          AND IS_SRVROLEMEMBER(N'securityadmin')=0
          AND IS_SRVROLEMEMBER(N'serveradmin')=0
          AND IS_ROLEMEMBER(N'db_owner')=0
          AND IS_ROLEMEMBER(N'db_securityadmin')=0
          AND IS_ROLEMEMBER(N'db_ddladmin')=0
          AND HAS_PERMS_BY_NAME(NULL,NULL,N'CONTROL SERVER')=0
          AND HAS_PERMS_BY_NAME(NULL,NULL,N'IMPERSONATE ANY LOGIN')=0
          AND HAS_PERMS_BY_NAME(NULL,NULL,N'ALTER ANY LOGIN')=0
          AND HAS_PERMS_BY_NAME(NULL,NULL,N'ALTER ANY SERVER ROLE')=0
          AND HAS_PERMS_BY_NAME(DB_NAME(),N'DATABASE',N'CONTROL')=0
          AND HAS_PERMS_BY_NAME(DB_NAME(),N'DATABASE',N'VIEW DEFINITION')=1
          AND HAS_PERMS_BY_NAME(DB_NAME(),N'DATABASE',N'ALTER')=0
          AND HAS_PERMS_BY_NAME(DB_NAME(),N'DATABASE',N'ALTER ANY ROLE')=0
          AND HAS_PERMS_BY_NAME(DB_NAME(),N'DATABASE',N'ALTER ANY USER')=0
          AND HAS_PERMS_BY_NAME(DB_NAME(),N'DATABASE',N'ALTER ANY SCHEMA')=0
          AND HAS_PERMS_BY_NAME(DB_NAME(),N'DATABASE',N'ALTER ANY DATABASE DDL TRIGGER')=0
          AND HAS_PERMS_BY_NAME(DB_NAME(),N'DATABASE',N'CREATE PROCEDURE')=0
          AND HAS_PERMS_BY_NAME(DB_NAME(),N'DATABASE',N'CREATE FUNCTION')=0
          AND HAS_PERMS_BY_NAME(DB_NAME(),N'DATABASE',N'CREATE ASSEMBLY')=0
          AND HAS_PERMS_BY_NAME(DB_NAME(),N'DATABASE',N'CREATE VIEW')=0
          AND HAS_PERMS_BY_NAME(DB_NAME(),N'DATABASE',N'CREATE SYNONYM')=0
          -- SQL Server has no DATABASE permission named IMPERSONATE ANY USER.
          -- CONTROL and the effective USER impersonation inventory below cover
          -- elevation; an invalid permission query returns NULL, not absence.
          AND HAS_PERMS_BY_NAME(N'aioffice_binding_runtime',N'ROLE',N'ALTER')=0
          AND NOT EXISTS (SELECT 1 FROM sys.database_principals p WHERE p.type='R'
            AND ISNULL(HAS_PERMS_BY_NAME(p.name,N'ROLE',N'ALTER'),1)<>0)
          AND NOT EXISTS (SELECT 1 FROM sys.schemas s WHERE s.name IN (N'aioffice',N'dbo')
            AND (ISNULL(HAS_PERMS_BY_NAME(s.name,N'SCHEMA',N'ALTER'),1)<>0
              OR ISNULL(HAS_PERMS_BY_NAME(s.name,N'SCHEMA',N'CONTROL'),1)<>0
              OR ISNULL(HAS_PERMS_BY_NAME(s.name,N'SCHEMA',N'TAKE OWNERSHIP'),1)<>0))
          AND NOT EXISTS (SELECT 1 FROM sys.database_principals p
            WHERE p.type IN ('S','U','G','E','X') AND p.principal_id<>USER_ID()
              AND ISNULL(HAS_PERMS_BY_NAME(p.name,N'USER',N'IMPERSONATE'),1)<>0)
          AND NOT EXISTS (SELECT 1 FROM sys.server_principals p
            WHERE p.type IN ('S','U','G','E','X') AND p.principal_id<>SUSER_ID()
              AND ISNULL(HAS_PERMS_BY_NAME(p.name,N'LOGIN',N'IMPERSONATE'),1)<>0)
          AND HAS_PERMS_BY_NAME(N'aioffice.DataSourceSecretBindings',N'OBJECT',N'SELECT')=1
          AND HAS_PERMS_BY_NAME(N'aioffice.DataSourceSecretBindings',N'OBJECT',N'INSERT')=0
          AND HAS_PERMS_BY_NAME(N'aioffice.DataSourceSecretBindings',N'OBJECT',N'UPDATE')=0
          AND HAS_PERMS_BY_NAME(N'aioffice.DataSourceSecretBindings',N'OBJECT',N'DELETE')=0
          AND HAS_PERMS_BY_NAME(N'aioffice.DataSourceSecretBindings',N'OBJECT',N'ALTER')=0
          AND HAS_PERMS_BY_NAME(N'aioffice.DataSourceSecretBindings',N'OBJECT',N'CONTROL')=0
          AND HAS_PERMS_BY_NAME(N'aioffice.DataSourceSecretBindings',N'OBJECT',N'TAKE OWNERSHIP')=0
          AND NOT EXISTS (SELECT 1 FROM sys.columns c
            WHERE c.object_id=OBJECT_ID(N'aioffice.DataSourceSecretBindings')
              AND ISNULL(HAS_PERMS_BY_NAME(N'aioffice.DataSourceSecretBindings',N'OBJECT',
                N'UPDATE',c.name,N'COLUMN'),1)<>0)
          -- Deliberately conservative: SQL ownership chains bypass direct DENY.
          -- This runtime uses ad-hoc EF queries, not user procedures/functions.
          AND NOT EXISTS (SELECT 1 FROM sys.objects o
            WHERE o.is_ms_shipped=0 AND o.type IN ('P','PC','FN','FS','FT','AF','TF','IF')
              AND (ISNULL(HAS_PERMS_BY_NAME(QUOTENAME(SCHEMA_NAME(o.schema_id))+N'.'+QUOTENAME(o.name),
                    N'OBJECT',N'EXECUTE'),1)<>0
                OR ISNULL(HAS_PERMS_BY_NAME(QUOTENAME(SCHEMA_NAME(o.schema_id))+N'.'+QUOTENAME(o.name),
                    N'OBJECT',N'SELECT'),1)<>0))
          AND NOT EXISTS (SELECT 1 FROM sys.objects o WHERE o.is_ms_shipped=0 AND o.type IN ('V','SN')
            AND (ISNULL(HAS_PERMS_BY_NAME(QUOTENAME(SCHEMA_NAME(o.schema_id))+N'.'+QUOTENAME(o.name),N'OBJECT',N'INSERT'),1)<>0
              OR ISNULL(HAS_PERMS_BY_NAME(QUOTENAME(SCHEMA_NAME(o.schema_id))+N'.'+QUOTENAME(o.name),N'OBJECT',N'UPDATE'),1)<>0
              OR ISNULL(HAS_PERMS_BY_NAME(QUOTENAME(SCHEMA_NAME(o.schema_id))+N'.'+QUOTENAME(o.name),N'OBJECT',N'DELETE'),1)<>0))
          AND NOT EXISTS (SELECT 1 FROM sys.triggers t WHERE t.is_disabled=0 AND t.is_ms_shipped=0)
          THEN 1 ELSE 0 END;
        """;

    public async Task RequireReadOnlyAsync(CancellationToken cancellationToken = default)
    {
        // The nonrelational provider is used only by synthetic tests. Shipping
        // DI registers SQL Server; its permission proof is required on every use.
        if (!database.Database.IsRelational()) return;
        var connection = database.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        try
        {
            if (opened) await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = database.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandText = VerificationSql;
            command.CommandTimeout = 10;
            if (!Equals(await command.ExecuteScalarAsync(cancellationToken), 1))
                throw DataSourceSecretBindingService.Unavailable();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is DbException or InvalidOperationException)
        {
            throw DataSourceSecretBindingService.Unavailable();
        }
        finally
        {
            if (opened && connection.State == ConnectionState.Open) await connection.CloseAsync();
        }
    }
}
