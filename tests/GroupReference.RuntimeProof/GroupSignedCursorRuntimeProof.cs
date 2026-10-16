using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Platform.Configuration;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.GroupReference.RuntimeProof;

internal static class GroupSignedCursorRuntimeProof
{
    internal const string Success = "PASS owned signed frontier cursor SQL trusted seven field foreign key nonempty partial null closure three tuple attacks four permitted noop updates complete original cursor unchanged";
    internal const string CatalogSql = """
        SELECT CASE WHEN EXISTS (
            SELECT 1 FROM sys.foreign_keys fk
            WHERE fk.name=N'FK_GroupTerminalFrontierStates_SignedAdvance'
                AND fk.parent_object_id=OBJECT_ID(N'aioffice.GroupTerminalFrontierStates')
                AND fk.referenced_object_id=OBJECT_ID(N'aioffice.GroupTerminalAdvances')
                AND fk.is_disabled=0 AND fk.is_not_trusted=0 AND fk.delete_referential_action=0
                AND (SELECT COUNT(*) FROM sys.foreign_key_columns f WHERE f.constraint_object_id=fk.object_id)=7
                AND (SELECT COUNT(*) FROM sys.foreign_key_columns f
                    JOIN sys.columns c ON c.object_id=f.parent_object_id AND c.column_id=f.parent_column_id
                    JOIN sys.columns p ON p.object_id=f.referenced_object_id AND p.column_id=f.referenced_column_id
                    JOIN (VALUES(1,N'TenantId',N'TenantId'),(2,N'CompanyId',N'CompanyId'),(3,N'BindingId',N'BindingId'),
                        (4,N'Version',N'Version'),(5,N'ThroughSequence',N'ThroughSequence'),
                        (6,N'LastTerminalBatchId',N'BatchId'),(7,N'UpdatedAtUtc',N'AdvancedAtUtc')) v(ordinal,child,parent)
                        ON v.ordinal=f.constraint_column_id AND v.child=c.name AND v.parent=p.name
                    WHERE f.constraint_object_id=fk.object_id)=7)
            AND EXISTS (SELECT 1 FROM sys.check_constraints c
                WHERE c.name=N'CK_GroupTerminalFrontierStates_Signed'
                    AND c.parent_object_id=OBJECT_ID(N'aioffice.GroupTerminalFrontierStates')
                    AND c.is_disabled=0 AND c.is_not_trusted=0)
            THEN 1 ELSE 0 END AS [Value];
        """;

    internal static async Task RunAsync(GroupScope scope, Guid allocationOperation, GroupExtractionWorkerBinding worker,
        DbContextOptions<PlatformDbContext> options, CancellationToken token)
    {
        OwnedGroupReferenceProofGuard.RequireOwned(Environment.GetEnvironmentVariable);
        // Authenticate the actual original signed root/current authority through
        // the existing store and independent observer before any SQL attack.
        await GroupFrontierRootRuntimeProof.RunAsync("frontier-root-replay", scope, allocationOperation, worker, options, token);
        await using var db = new PlatformDbContext(options);
        await new GroupTerminalAdvanceKeyPermissionVerifier(db).RequireSafeRuntimeAsync(token);
        var catalog = await db.Database.SqlQueryRaw<int>(CatalogSql).ToArrayAsync(token);
        if (catalog.Length != 1 || catalog[0] != 1) throw new InvalidOperationException();
        var original = await CursorAsync();
        foreach (var (sql, constraint) in Denials(scope))
        {
            var refused = false;
            await using (var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token))
            {
                try { await LockAsync(); await db.Database.ExecuteSqlInterpolatedAsync(sql, token); }
                catch (SqlException error) when (error.Number == 547 && error.Message.Contains(constraint, StringComparison.Ordinal))
                { refused = true; }
                finally { await transaction.RollbackAsync(CancellationToken.None); }
            }
            if (!refused) throw new InvalidOperationException();
            await UnchangedAsync();
        }
        foreach (var sql in Noops(scope))
        {
            await using (var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token))
            {
                try
                {
                    await LockAsync();
                    if (await db.Database.ExecuteSqlInterpolatedAsync(sql, token) != 1) throw new InvalidOperationException();
                }
                finally { await transaction.RollbackAsync(CancellationToken.None); }
            }
            await UnchangedAsync();
        }
        Console.WriteLine(Success);

        async Task<byte[]> CursorAsync() => JsonSerializer.SerializeToUtf8Bytes(await db.GroupTerminalFrontierStates.AsNoTracking()
            .SingleAsync(x => x.TenantId == scope.TenantId && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId, token));
        async Task LockAsync()
        {
            var held = await db.Database.SqlQuery<int>(SourceLockSql(scope)).ToArrayAsync(token);
            if (held.Length != 1 || held[0] is not (0 or 1)) throw new InvalidOperationException();
        }
        async Task UnchangedAsync()
        {
            if (db.Database.CurrentTransaction is not null || db.Database.GetDbConnection().State != ConnectionState.Closed
                || db.ChangeTracker.Entries().Any()) throw new InvalidOperationException();
            var current = await CursorAsync();
            if (!original.AsSpan().SequenceEqual(current)) throw new InvalidOperationException();
        }
    }

    internal static FormattableString SourceLockSql(GroupScope scope)
    {
        scope.Validate();
        var resource = $"aioffice:group-ingest:{scope.TenantId:N}/{scope.CompanyId:N}/{scope.SourceBindingId:N}";
        return $"DECLARE @rc int; EXEC @rc=sys.sp_getapplock @Resource={resource},@LockMode=N'Exclusive',@LockOwner=N'Transaction',@LockTimeout=5000; SELECT @rc AS [Value];";
    }

    internal static (FormattableString Sql, string Constraint)[] Denials(GroupScope scope)
    {
        scope.Validate();
        const string foreign = "FK_GroupTerminalFrontierStates_SignedAdvance";
        return [
            ($"UPDATE [aioffice].[GroupTerminalFrontierStates] SET [Version]=[Version]+1 WHERE [TenantId]={scope.TenantId} AND [CompanyId]={scope.CompanyId} AND [BindingId]={scope.SourceBindingId};", foreign),
            ($"UPDATE [aioffice].[GroupTerminalFrontierStates] SET [ThroughSequence]=[ThroughSequence]+1 WHERE [TenantId]={scope.TenantId} AND [CompanyId]={scope.CompanyId} AND [BindingId]={scope.SourceBindingId};", foreign),
            ($"UPDATE [aioffice].[GroupTerminalFrontierStates] SET [UpdatedAtUtc]=DATEADD(nanosecond,100,[UpdatedAtUtc]) WHERE [TenantId]={scope.TenantId} AND [CompanyId]={scope.CompanyId} AND [BindingId]={scope.SourceBindingId};", foreign),
            ($"UPDATE [aioffice].[GroupTerminalFrontierStates] SET [ThroughSequence]=0,[LastTerminalBatchId]=NULL WHERE [TenantId]={scope.TenantId} AND [CompanyId]={scope.CompanyId} AND [BindingId]={scope.SourceBindingId};", "CK_GroupTerminalFrontierStates_Signed")
        ];
    }

    internal static FormattableString[] Noops(GroupScope scope)
    {
        scope.Validate();
        return [
            $"UPDATE [aioffice].[GroupTerminalFrontierStates] SET [Version]=[Version] WHERE [TenantId]={scope.TenantId} AND [CompanyId]={scope.CompanyId} AND [BindingId]={scope.SourceBindingId};",
            $"UPDATE [aioffice].[GroupTerminalFrontierStates] SET [ThroughSequence]=[ThroughSequence] WHERE [TenantId]={scope.TenantId} AND [CompanyId]={scope.CompanyId} AND [BindingId]={scope.SourceBindingId};",
            $"UPDATE [aioffice].[GroupTerminalFrontierStates] SET [UpdatedAtUtc]=[UpdatedAtUtc] WHERE [TenantId]={scope.TenantId} AND [CompanyId]={scope.CompanyId} AND [BindingId]={scope.SourceBindingId};",
            $"UPDATE [aioffice].[GroupTerminalFrontierStates] SET [LastTerminalBatchId]=[LastTerminalBatchId] WHERE [TenantId]={scope.TenantId} AND [CompanyId]={scope.CompanyId} AND [BindingId]={scope.SourceBindingId};"
        ];
    }
}
