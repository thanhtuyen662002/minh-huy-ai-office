namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed class RegistrationAuditPermissionVerifier(PlatformDbContext database)
{
    internal const string VerificationSql = """
        SELECT CASE WHEN OBJECT_ID(N'aioffice.DataSourceRegistrationAudits',N'U') IS NOT NULL
          AND EXISTS (SELECT 1 FROM sys.objects o
            WHERE o.object_id=OBJECT_ID(N'aioffice.DataSourceRegistrationAudits')
              AND o.principal_id=DATABASE_PRINCIPAL_ID(N'aioffice_binding_operator_owner'))
          AND HAS_PERMS_BY_NAME(N'aioffice.DataSourceRegistrationAudits',N'OBJECT',N'SELECT')=1
          AND HAS_PERMS_BY_NAME(N'aioffice.DataSourceRegistrationAudits',N'OBJECT',N'INSERT')=1
          AND HAS_PERMS_BY_NAME(N'aioffice.DataSourceRegistrationAudits',N'OBJECT',N'UPDATE')=0
          AND HAS_PERMS_BY_NAME(N'aioffice.DataSourceRegistrationAudits',N'OBJECT',N'DELETE')=0
          AND HAS_PERMS_BY_NAME(N'aioffice.DataSourceRegistrationAudits',N'OBJECT',N'ALTER')=0
          AND HAS_PERMS_BY_NAME(N'aioffice.DataSourceRegistrationAudits',N'OBJECT',N'CONTROL')=0
          AND HAS_PERMS_BY_NAME(N'aioffice.DataSourceRegistrationAudits',N'OBJECT',N'TAKE OWNERSHIP')=0
          AND NOT EXISTS (SELECT 1 FROM sys.columns c
            WHERE c.object_id=OBJECT_ID(N'aioffice.DataSourceRegistrationAudits')
              AND ISNULL(HAS_PERMS_BY_NAME(N'aioffice.DataSourceRegistrationAudits',N'OBJECT',
                N'UPDATE',c.name,N'COLUMN'),1)<>0)
          THEN 1 ELSE 0 END;
        """;

    // The global binding-store proof rejects server/database/schema escalation,
    // executable ownership chains and triggers before this table-specific proof.
    public Task RequireAppendOnlyAsync(CancellationToken cancellationToken = default) =>
        new BindingStorePermissionVerifier(database).RequireProofAsync(VerificationSql, cancellationToken);
}
