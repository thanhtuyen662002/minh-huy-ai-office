namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed class GroupBatchTerminalPermissionVerifier(PlatformDbContext database)
{
    private static readonly string[] WritableFrontierColumns = ["ThroughSequence", "LastTerminalBatchId", "Version", "UpdatedAtUtc"];
    internal static string VerificationSql { get; } = BuildProof();

    public async Task RequireSafeRuntimeAsync(CancellationToken cancellationToken = default)
    {
        await new GroupWorkNotePermissionVerifier(database).RequireSafeRuntimeAsync(cancellationToken);
        await new BindingStorePermissionVerifier(database).RequireProofAsync(VerificationSql, cancellationToken);
    }

    // Fixed identifiers only. Effective column rights are checked because SQL
    // Server column GRANT can override a table DENY; new columns fail closed.
    private static string BuildProof()
    {
        var conditions = new List<string>();
        foreach (var table in new[] { "GroupBatchTerminalReceipts", "GroupTerminalFrontierStates" })
        {
            var name = "aioffice." + table;
            var mutable = table == "GroupTerminalFrontierStates";
            conditions.Add($"OBJECT_ID(N'{name}',N'U') IS NOT NULL");
            conditions.Add($"EXISTS (SELECT 1 FROM sys.objects o WHERE o.object_id=OBJECT_ID(N'{name}') AND o.principal_id=DATABASE_PRINCIPAL_ID(N'aioffice_binding_operator_owner'))");
            conditions.Add($"HAS_PERMS_BY_NAME(N'{name}',N'OBJECT',N'SELECT')=1");
            conditions.Add($"HAS_PERMS_BY_NAME(N'{name}',N'OBJECT',N'INSERT')=1");
            if (!mutable) conditions.Add($"HAS_PERMS_BY_NAME(N'{name}',N'OBJECT',N'UPDATE')=0");
            foreach (var permission in new[] { "DELETE", "ALTER", "CONTROL", "TAKE OWNERSHIP" })
                conditions.Add($"HAS_PERMS_BY_NAME(N'{name}',N'OBJECT',N'{permission}')=0");
            if (mutable)
                foreach (var column in WritableFrontierColumns)
                    conditions.Add($"HAS_PERMS_BY_NAME(N'{name}',N'OBJECT',N'UPDATE',N'{column}',N'COLUMN')=1");
            var filter = mutable ? $" AND c.name NOT IN ({string.Join(",", WritableFrontierColumns.Select(x => $"N'{x}'"))})" : "";
            conditions.Add($"NOT EXISTS (SELECT 1 FROM sys.columns c WHERE c.object_id=OBJECT_ID(N'{name}'){filter} AND ISNULL(HAS_PERMS_BY_NAME(N'{name}',N'OBJECT',N'UPDATE',c.name,N'COLUMN'),1)<>0)");
        }
        return $"SELECT CASE WHEN {string.Join("\n AND ", conditions)} THEN 1 ELSE 0 END;";
    }
}
