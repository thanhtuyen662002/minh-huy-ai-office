"""Owned SQL permission-escalation controls; no provider or production action."""
import re
import uuid

REFUSAL_LINE = 'PASS owned original admission fresh effective permission verifier refuses unsafe sidecar privileges or accessible owner procedure no source plaintext or provider'
SUMMARY = 'PASS actual original admission column override table control and physical owner chain refused fresh runtime verifier exact permissions procedure cleanup healthy reads no model qualification'


def verify(*, directory, api, tenant, company, source, batch, sql, run_child, reference, assert_unchanged):
    reference.require_owned(directory,api)
    tenant,company,source,batch=(str(uuid.UUID(x)) for x in (tenant,company,source,batch))
    assert all(uuid.UUID(x).int for x in (tenant,company,source,batch))
    table='aioffice.GroupOriginalAdmissionSidecars';role='aioffice_binding_runtime'
    procedure='OwnedOriginalAdmissionChain_'+uuid.UUID(batch).hex
    qualified='aioffice.'+procedure
    assert re.fullmatch(r'OwnedOriginalAdmissionChain_[0-9a-f]{32}',procedure)
    assert sql(f"/* owned-admission-permission:absent */ SELECT COUNT(*) FROM sys.objects WHERE object_id=OBJECT_ID(N'{qualified}');")=='0'

    def inventory():
        value=sql("""/* owned-admission-permission:inventory */
            SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),CONCAT(
              (SELECT * FROM sys.database_permissions ORDER BY class,major_id,minor_id,grantee_principal_id,grantor_principal_id,type FOR JSON PATH,INCLUDE_NULL_VALUES),
              (SELECT o.object_id,o.name,o.type,o.principal_id,m.definition FROM sys.objects o LEFT JOIN sys.sql_modules m ON m.object_id=o.object_id
               WHERE o.is_ms_shipped=0 ORDER BY o.object_id FOR JSON PATH,INCLUDE_NULL_VALUES)))),2);
            """)
        assert re.fullmatch(r'[0-9A-F]{64}',value);return value

    baseline=inventory()
    column_mutation=f"REVOKE UPDATE ON OBJECT::{table} ([Sidecar]) FROM {role}; GRANT UPDATE ON OBJECT::{table} ([Sidecar]) TO {role};"
    column_restore=f"REVOKE UPDATE ON OBJECT::{table} ([Sidecar]) FROM {role}; DENY UPDATE ON OBJECT::{table} ([Sidecar]) TO {role};"
    definition=(f'CREATE PROCEDURE {qualified} @TenantId uniqueidentifier,@CompanyId uniqueidentifier,@BindingId uniqueidentifier,@BatchId uniqueidentifier AS '
        f'BEGIN SET NOCOUNT ON; UPDATE {table} SET Sidecar=Sidecar WHERE TenantId=@TenantId AND CompanyId=@CompanyId AND BindingId=@BindingId AND BatchId=@BatchId; SELECT @@ROWCOUNT; END;')
    # The existing SQL helper prepends SET/USE, so CREATE PROCEDURE must be
    # executed in its own nested batch. The procedure text is fixed host code.
    create="EXEC(N'"+definition.replace("'","''")+f"'); ALTER AUTHORIZATION ON OBJECT::{qualified} TO aioffice_binding_operator_owner; GRANT EXECUTE ON OBJECT::{qualified} TO {role};"
    cases=[('column-update',column_mutation,column_restore,f"HAS_PERMS_BY_NAME(N'{table}',N'OBJECT',N'UPDATE',N'Sidecar',N'COLUMN')"),
        ('table-control',f'GRANT CONTROL ON OBJECT::{table} TO {role};',f'REVOKE CONTROL ON OBJECT::{table} FROM {role};',f"HAS_PERMS_BY_NAME(N'{table}',N'OBJECT',N'CONTROL')"),
        ('owner-chain',create,f'DROP PROCEDURE IF EXISTS {qualified};',f"HAS_PERMS_BY_NAME(N'{qualified}',N'OBJECT',N'EXECUTE')")]
    run_child('admission-original-resume');assert_unchanged()
    for name,mutation,restore,effective in cases:
        failure=None
        try:
            reference.require_owned(directory,api)
            sql(f'/* owned-admission-permission:{name} */ '+mutation)
            assert sql(f"/* owned-admission-permission:effective */ EXECUTE AS USER=N'aioffice_runtime'; SELECT {effective}; REVERT;")=='1'
            if name=='owner-chain':
                # Show that this same owned procedure can bypass direct table
                # DENY through its owner chain, then roll back the no-op write.
                # Fresh runtime admission must refuse the unsafe configuration.
                assert sql(f"/* owned-admission-permission:physical-chain */ EXECUTE AS USER=N'aioffice_runtime'; BEGIN TRANSACTION; "
                    f"EXEC {qualified} @TenantId='{tenant}',@CompanyId='{company}',@BindingId='{source}',@BatchId='{batch}'; ROLLBACK TRANSACTION; REVERT;")=='1'
            run_child('admission-original-permission')
        except BaseException as error:failure=error
        finally:
            try:
                reference.require_owned(directory,api)
                sql(f'/* owned-admission-permission:restore-{name} */ '+restore)
                assert inventory()==baseline,'Admission permission/procedure restoration differs'
                assert_unchanged();run_child('admission-original-resume');assert_unchanged()
            except BaseException as error:
                if failure is None:failure=error
        if failure is not None:raise failure
