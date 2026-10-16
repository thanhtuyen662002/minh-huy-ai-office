namespace MinhHuy.AIOffice.Platform.Persistence;

internal sealed class GroupOriginalInvalidationPermissionVerifier(PlatformDbContext database)
{
    internal static string VerificationSql { get; } = Build();
    internal static string EffectiveVerificationSql { get; } = GroupEffectivePermissionProof.All(
        GroupAutomaticInputPlanPermissionVerifier.EffectiveVerificationSql, VerificationSql);
    internal Task RequireSafeRuntimeAsync(CancellationToken token) =>
        new BindingStorePermissionVerifier(database).RequireProofAsync(EffectiveVerificationSql, token);
    private static string Build()
    {
        const string name = "aioffice.GroupOriginalInvalidations";
        var conditions = new List<string>
        {
            $"OBJECT_ID(N'{name}',N'U') IS NOT NULL",
            $"EXISTS (SELECT 1 FROM sys.objects o WHERE o.object_id=OBJECT_ID(N'{name}') AND o.principal_id=DATABASE_PRINCIPAL_ID(N'aioffice_binding_operator_owner'))",
            $"HAS_PERMS_BY_NAME(N'{name}',N'OBJECT',N'SELECT')=1",
            $"HAS_PERMS_BY_NAME(N'{name}',N'OBJECT',N'INSERT')=1"
        };
        foreach (var permission in new[] { "UPDATE", "DELETE", "ALTER", "CONTROL", "TAKE OWNERSHIP" })
            conditions.Add($"HAS_PERMS_BY_NAME(N'{name}',N'OBJECT',N'{permission}')=0");
        conditions.Add($"NOT EXISTS (SELECT 1 FROM sys.columns c WHERE c.object_id=OBJECT_ID(N'{name}') AND ISNULL(HAS_PERMS_BY_NAME(N'{name}',N'OBJECT',N'UPDATE',c.name,N'COLUMN'),1)<>0)");
        // Empty-batch evidence cannot hide orphan rows behind disabled/untrusted FKs.
        conditions.Add("NOT EXISTS (SELECT 1 FROM sys.foreign_keys f WHERE f.parent_object_id IN (" +
            string.Join(",", new[] { "GroupOriginalInvalidations", "GroupWorkSourceDispositions", "GroupWorkRawDispositions", "GroupCustomerRequests",
                "GroupRequestRevisions", "GroupRequestEvidence", "GroupNotesCommittedOutbox", "GroupNotesCommittedItems" }.Select(x => $"OBJECT_ID(N'aioffice.{x}')")) +
            ") AND (f.is_disabled=1 OR f.is_not_trusted=1))");
        // Absence/dropped/rebound constraints must also fail. These exact
        // parent links let an empty scoped parent graph exclude hidden orphans.
        conditions.Add(TrustedLink("GroupRequestEvidence", "GroupRequestRevisions",
            [("TenantId", "TenantId"), ("CompanyId", "CompanyId"), ("BindingId", "BindingId"), ("RequestId", "RequestId"), ("RequestRevision", "Revision")]));
        conditions.Add(TrustedLink("GroupNotesCommittedItems", "GroupNotesCommittedOutbox",
            [("TenantId", "TenantId"), ("CompanyId", "CompanyId"), ("BindingId", "BindingId"), ("OutboxId", "Id")]));
        conditions.Add(TrustedLink("GroupNotesCommittedItems", "GroupRequestRevisions",
            [("TenantId", "TenantId"), ("CompanyId", "CompanyId"), ("BindingId", "BindingId"), ("RequestId", "RequestId"), ("RequestRevision", "Revision")]));
        return $"SELECT CASE WHEN {string.Join("\n AND ", conditions)} THEN 1 ELSE 0 END;";
    }

    private static string TrustedLink(string child, string parent, (string Child, string Parent)[] columns) =>
        $"EXISTS (SELECT 1 FROM sys.foreign_keys f WHERE f.parent_object_id=OBJECT_ID(N'aioffice.{child}') " +
        $"AND f.referenced_object_id=OBJECT_ID(N'aioffice.{parent}') AND f.is_disabled=0 AND f.is_not_trusted=0 " +
        $"AND (SELECT COUNT(*) FROM sys.foreign_key_columns c WHERE c.constraint_object_id=f.object_id)={columns.Length} AND " +
        string.Join(" AND ", columns.Select((pair, index) =>
            $"EXISTS (SELECT 1 FROM sys.foreign_key_columns c WHERE c.constraint_object_id=f.object_id AND c.constraint_column_id={index + 1} " +
            $"AND COL_NAME(c.parent_object_id,c.parent_column_id)=N'{pair.Child}' AND COL_NAME(c.referenced_object_id,c.referenced_column_id)=N'{pair.Parent}')")) + ")";
}
