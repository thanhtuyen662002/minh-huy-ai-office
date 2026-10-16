namespace MinhHuy.AIOffice.Platform.Persistence;

// Apply only to new admission operations. Legacy original/result recovery
// must not gain a dependency on this new table or current model eligibility.
internal sealed class GroupOriginalAdmissionPermissionVerifier(PlatformDbContext database)
{
    internal static string VerificationSql { get; } = Build();
    internal static string EffectiveVerificationSql { get; } = GroupEffectivePermissionProof.All(
        GroupAutomaticInputPlanPermissionVerifier.EffectiveVerificationSql, VerificationSql);
    internal Task RequireSafeRuntimeAsync(CancellationToken token) =>
        new BindingStorePermissionVerifier(database).RequireProofAsync(EffectiveVerificationSql, token);
    private static string Build()
    {
        const string name = "aioffice.GroupOriginalAdmissionSidecars";
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
        return $"SELECT CASE WHEN {string.Join("\n AND ", conditions)} THEN 1 ELSE 0 END;";
    }
}
