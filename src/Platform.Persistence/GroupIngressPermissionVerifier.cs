namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed class GroupIngressPermissionVerifier(PlatformDbContext database)
{
    // Fixed server-owned identifiers only. No event, model or HTTP value can
    // choose a table or a permission proof. Migrations freeze their own SQL.
    private static readonly string[] Registry = ["GroupConnectorAccounts", "GroupServices", "GroupBindings", "GroupServiceGrants", "GroupReaderGrants"];
    private static readonly string[] AppendOnly = ["GroupMessages", "GroupMessageRevisions", "GroupIngressReceipts"];
    private static readonly IReadOnlyDictionary<string, string[]> Mutable = new Dictionary<string, string[]>
    {
        ["GroupListenerLeases"] = ["TenantId", "CompanyId", "ConnectorAccountId"],
        ["GroupSourceStates"] = ["TenantId", "CompanyId", "BindingId"],
        ["GroupCoverageGaps"] = ["TenantId", "CompanyId", "BindingId", "Id", "AfterCommittedSequence", "Reason", "OpenedAtUtc"],
        ["GroupIngressOutbox"] = ["TenantId", "CompanyId", "BindingId", "Id", "MessageId", "Revision", "CommittedSequence"]
    };

    internal static string VerificationSql { get; } = BuildProof();

    public async Task RequireSafeRuntimeAsync(CancellationToken cancellationToken = default)
    {
        var global = new BindingStorePermissionVerifier(database);
        await global.RequireReadOnlyAsync(cancellationToken);
        await global.RequireProofAsync(VerificationSql, cancellationToken);
    }

    private static string BuildProof()
    {
        var conditions = new List<string>();
        foreach (var table in Registry.Concat(AppendOnly).Concat(Mutable.Keys))
        {
            var name = $"aioffice.{table}";
            var isRegistry = Registry.Contains(table, StringComparer.Ordinal);
            var isMutable = Mutable.TryGetValue(table, out var columns);
            conditions.Add($"OBJECT_ID(N'{name}',N'U') IS NOT NULL");
            conditions.Add($"EXISTS (SELECT 1 FROM sys.objects o WHERE o.object_id=OBJECT_ID(N'{name}') AND o.principal_id=DATABASE_PRINCIPAL_ID(N'aioffice_binding_operator_owner'))");
            conditions.Add($"HAS_PERMS_BY_NAME(N'{name}',N'OBJECT',N'SELECT')=1");
            conditions.Add($"HAS_PERMS_BY_NAME(N'{name}',N'OBJECT',N'INSERT')={(isRegistry ? 0 : 1)}");
            conditions.Add($"HAS_PERMS_BY_NAME(N'{name}',N'OBJECT',N'UPDATE')={(isMutable ? 1 : 0)}");
            foreach (var permission in new[] { "DELETE", "ALTER", "CONTROL", "TAKE OWNERSHIP" })
                conditions.Add($"HAS_PERMS_BY_NAME(N'{name}',N'OBJECT',N'{permission}')=0");
            var filter = isMutable ? $" AND c.name IN ({string.Join(",", columns!.Select(x => $"N'{x}'"))})" : "";
            conditions.Add($"NOT EXISTS (SELECT 1 FROM sys.columns c WHERE c.object_id=OBJECT_ID(N'{name}'){filter} AND ISNULL(HAS_PERMS_BY_NAME(N'{name}',N'OBJECT',N'UPDATE',c.name,N'COLUMN'),1)<>0)");
        }
        return $"SELECT CASE WHEN {string.Join("\n AND ", conditions)} THEN 1 ELSE 0 END;";
    }
}
