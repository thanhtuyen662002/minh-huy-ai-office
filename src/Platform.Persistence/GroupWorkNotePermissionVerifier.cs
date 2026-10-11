namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed class GroupWorkNotePermissionVerifier(PlatformDbContext database)
{
    private static readonly string[] Registry = ["GroupEditorGrants", "GroupGlossaryEntries", "GroupGlossaryRevisions"];
    private static readonly string[] AppendOnly = ["GroupRequestRevisions", "GroupRequestEvidence", "GroupWorkCommitReceipts", "GroupWorkSourceDispositions", "GroupWorkRawDispositions", "GroupNotesCommittedItems"];
    private static readonly IReadOnlyDictionary<string, string[]> WritableColumns = new Dictionary<string, string[]>
    {
        ["GroupCustomerRequests"] = ["CurrentRevision", "BusinessStatus", "BusinessVersion", "AssignedToUserId", "CommittedDueAtUtc", "ConfirmedByUserId", "ConfirmedAtUtc", "UpdatedAtUtc"],
        ["GroupNotesCommittedOutbox"] = ["AvailableAtUtc", "PublishAttempts", "PublishedAtUtc"]
    };
    internal static string VerificationSql { get; } = BuildProof();

    public async Task RequireSafeRuntimeAsync(CancellationToken cancellationToken = default)
    {
        await new GroupIngressPermissionVerifier(database).RequireSafeRuntimeAsync(cancellationToken);
        await new BindingStorePermissionVerifier(database).RequireProofAsync(VerificationSql, cancellationToken);
    }

    // Fixed identifiers only; extra columns default immutable. Registry rights
    // remain operator-owned and effective column rights must be checked too.
    private static string BuildProof()
    {
        var conditions = new List<string>();
        foreach (var table in Registry.Concat(AppendOnly).Concat(WritableColumns.Keys))
        {
            var name = "aioffice." + table;
            var registry = Registry.Contains(table, StringComparer.Ordinal);
            var mutable = WritableColumns.TryGetValue(table, out var columns);
            conditions.Add($"OBJECT_ID(N'{name}',N'U') IS NOT NULL");
            conditions.Add($"EXISTS (SELECT 1 FROM sys.objects o WHERE o.object_id=OBJECT_ID(N'{name}') AND o.principal_id=DATABASE_PRINCIPAL_ID(N'aioffice_binding_operator_owner'))");
            conditions.Add($"HAS_PERMS_BY_NAME(N'{name}',N'OBJECT',N'SELECT')=1");
            conditions.Add($"HAS_PERMS_BY_NAME(N'{name}',N'OBJECT',N'INSERT')={(registry ? 0 : 1)}");
            if (mutable)
                foreach (var column in columns!) conditions.Add($"HAS_PERMS_BY_NAME(N'{name}',N'OBJECT',N'UPDATE',N'{column}',N'COLUMN')=1");
            else conditions.Add($"HAS_PERMS_BY_NAME(N'{name}',N'OBJECT',N'UPDATE')=0");
            foreach (var permission in new[] { "DELETE", "ALTER", "CONTROL", "TAKE OWNERSHIP" })
                conditions.Add($"HAS_PERMS_BY_NAME(N'{name}',N'OBJECT',N'{permission}')=0");
            var filter = mutable ? $" AND c.name NOT IN ({string.Join(",", columns!.Select(x => $"N'{x}'"))})" : "";
            conditions.Add($"NOT EXISTS (SELECT 1 FROM sys.columns c WHERE c.object_id=OBJECT_ID(N'{name}'){filter} AND ISNULL(HAS_PERMS_BY_NAME(N'{name}',N'OBJECT',N'UPDATE',c.name,N'COLUMN'),1)<>0)");
        }
        return $"SELECT CASE WHEN {string.Join("\n AND ", conditions)} THEN 1 ELSE 0 END;";
    }
}
