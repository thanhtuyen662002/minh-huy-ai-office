namespace MinhHuy.AIOffice.Platform.Persistence;

internal sealed class GroupModelAttemptPermissionVerifier(PlatformDbContext database)
{
    internal static string VerificationSql { get; } = BuildProof();

    internal async Task RequireSafeRuntimeAsync(CancellationToken token)
    {
        await new GroupWorkNotePermissionVerifier(database).RequireSafeRuntimeAsync(token);
        await new BindingStorePermissionVerifier(database).RequireProofAsync(VerificationSql, token);
    }

    private static string BuildProof()
    {
        var conditions = new List<string>();
        foreach (var table in new[] { "GroupModelAttemptStarts", "GroupModelAttemptFailures" })
        {
            var name = "aioffice." + table;
            conditions.Add($"OBJECT_ID(N'{name}',N'U') IS NOT NULL");
            conditions.Add($"EXISTS (SELECT 1 FROM sys.objects o WHERE o.object_id=OBJECT_ID(N'{name}') AND o.principal_id=DATABASE_PRINCIPAL_ID(N'aioffice_binding_operator_owner'))");
            conditions.Add($"HAS_PERMS_BY_NAME(N'{name}',N'OBJECT',N'SELECT')=1");
            conditions.Add($"HAS_PERMS_BY_NAME(N'{name}',N'OBJECT',N'INSERT')=1");
            foreach (var permission in new[] { "UPDATE", "DELETE", "ALTER", "CONTROL", "TAKE OWNERSHIP" })
                conditions.Add($"HAS_PERMS_BY_NAME(N'{name}',N'OBJECT',N'{permission}')=0");
            conditions.Add($"NOT EXISTS (SELECT 1 FROM sys.columns c WHERE c.object_id=OBJECT_ID(N'{name}') AND ISNULL(HAS_PERMS_BY_NAME(N'{name}',N'OBJECT',N'UPDATE',c.name,N'COLUMN'),1)<>0)");
        }
        return $"SELECT CASE WHEN {string.Join("\n AND ", conditions)} THEN 1 ELSE 0 END;";
    }
}
