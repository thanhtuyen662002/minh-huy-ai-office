namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed class CustomerErpStorePermissions(PlatformDbContext database)
{
    public Task RequireAsync(CancellationToken token) => new BindingStorePermissionVerifier(database).RequireProofAsync("""
        SELECT CASE WHEN
          HAS_PERMS_BY_NAME(N'aioffice.ProtectedDataSourceSecrets',N'OBJECT',N'SELECT')=1
          AND HAS_PERMS_BY_NAME(N'aioffice.ProtectedDataSourceSecrets',N'OBJECT',N'INSERT')=1
          AND HAS_PERMS_BY_NAME(N'aioffice.ProtectedDataSourceSecrets',N'OBJECT',N'UPDATE')=1
          AND HAS_PERMS_BY_NAME(N'aioffice.CustomerErpProbeAudits',N'OBJECT',N'INSERT')=1
          AND HAS_PERMS_BY_NAME(N'aioffice.CustomerErpProbeAudits',N'OBJECT',N'UPDATE')=0
          AND HAS_PERMS_BY_NAME(N'aioffice.CustomerErpProbeAudits',N'OBJECT',N'DELETE')=0
          AND HAS_PERMS_BY_NAME(N'aioffice.CustomerErpProbeAudits',N'OBJECT',N'ALTER')=0
          AND HAS_PERMS_BY_NAME(N'aioffice.CustomerErpProbeAudits',N'OBJECT',N'CONTROL')=0
          AND NOT EXISTS (SELECT 1 FROM sys.columns c WHERE c.object_id=OBJECT_ID(N'aioffice.CustomerErpProbeAudits')
            AND ISNULL(HAS_PERMS_BY_NAME(N'aioffice.CustomerErpProbeAudits',N'OBJECT',N'UPDATE',c.name,N'COLUMN'),1)<>0)
          THEN 1 ELSE 0 END;
        """, token);
}
