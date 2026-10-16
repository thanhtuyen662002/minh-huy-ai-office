"""Owned fresh host-only first-original proof; no model or deployment."""
import json
import os
from pathlib import Path
import re
import subprocess
import uuid

PREPARE_LINE = 'PASS owned automatic no-work actual inbox allocation two empty plain sources zero claims effects no model'
COMMIT_LINE = 'PASS owned first empty batch actual missing original real claimed processor atomic V3 sidecar no work two dispositions terminal signed frontier cold model factory'
RECOVER_LINE = 'PASS owned first empty batch separate process expired claim terminal FIRST original SELECT denied immutable no work original sidecar signed frontier cold model factory'
SUMMARY = 'PASS actual first empty batch real first original V3 sidecar no work complete raw selected dispositions terminal signed frontier expired claim terminal FIRST original SELECT denied fresh processes retained full graphs permissions both cleanup gates cold model factory no model qualification'
EXTRA = (('GroupAutomaticInputPlans','BatchId'),('GroupOriginalAdmissionSidecars','BatchId'),
    ('GroupModelAttemptAdmissions','RequestId'),('GroupModelAttemptStarts','RequestId'),
    ('GroupModelAttemptResults','RequestId'),('GroupModelAttemptFailures','RequestId'),
    ('GroupWorkRawDispositions','BatchId,CommittedSequence'),('GroupBatchTerminalReceipts','BatchId'),
    ('GroupTerminalAdvances','Version'),('GroupTerminalAdvanceKeys','KeyId'),('GroupTerminalFrontierStates','BindingId'))
DENIED_TABLES = ('GroupAutomaticInputPlans','GroupOriginalAdmissionSidecars','GroupModelAttemptAdmissions')


def verify(*, directory, api, manifest, tenant, company, service, sql, prepare_source, reference, automatic):
    reference.require_owned(directory,api)
    assert callable(sql) and callable(prepare_source)
    tenant,company,service=(automatic.canonical(x) for x in (tenant,company,service))
    installation=automatic.canonical(manifest['AIOFFICE_INSTALLATION_ID'])
    password=manifest['AIOFFICE_RUNTIME_PASSWORD']
    assert type(password) is str and re.fullmatch(r'[A-Za-z0-9_-]{32,128}',password)
    base=f"TenantId='{tenant}' AND CompanyId='{company}'"
    sources=tuple(automatic.canonical(x) for x in sql(f'SELECT LOWER(CONVERT(char(36),Id)) FROM aioffice.GroupBindings WHERE {base} ORDER BY Id;').splitlines())
    accounts=tuple(automatic.canonical(x) for x in sql(f'SELECT LOWER(CONVERT(char(36),Id)) FROM aioffice.GroupConnectorAccounts WHERE {base} ORDER BY Id;').splitlines())
    assert sources and accounts
    source_list=','.join("'"+x+"'" for x in sources)

    def digest_many(items):
        items=tuple(items);assert 1<=len(items)<=80
        statements=[]
        for i,(table,order,predicate) in enumerate(items):
            statements.append(f"SELECT CONCAT('{i:02d}|',CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),COALESCE("
                f"(SELECT * FROM aioffice.{table} WHERE {predicate} ORDER BY {order} FOR JSON PATH,INCLUDE_NULL_VALUES),N'[]'))),2));")
        rows=sql('/* owned-first-empty-digests */\n'+'\n'.join(statements)).splitlines()
        assert len(rows)==len(items),'First empty digest count differs'
        assert all(re.fullmatch(f'{i:02d}'+r'\|[0-9A-F]{64}',line) for i,line in enumerate(rows)),'First empty digest format differs'
        return tuple(line[3:] for line in rows)

    def retained():
        return automatic.retained_snapshot(tenant=tenant,company=company,sources=sources,accounts=accounts,sql=sql)+digest_many(
            (table,order,base+f' AND BindingId IN ({source_list})') for table,order in EXTRA)

    def permissions():
        value=sql("/* owned-first-empty-permissions */ SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),"
            "(SELECT * FROM sys.database_permissions ORDER BY class,major_id,minor_id,grantee_principal_id,grantor_principal_id,type FOR JSON PATH,INCLUDE_NULL_VALUES))),2);")
        assert re.fullmatch(r'[0-9A-F]{64}',value);return value

    original=retained();original_permissions=permissions()
    source=account=scope=completed=None
    suffix=uuid.uuid4().hex;image='aioffice-automatic-note-proof-'+suffix
    command=['docker','run','--init','--rm','--name',image,'--label','aioffice.owned-proof='+suffix,
        '--network','aioffice-'+uuid.UUID(installation).hex+'_default','--read-only','--cap-drop','ALL',
        '--security-opt','no-new-privileges:true','--tmpfs','/tmp:rw,noexec,nosuid,size=16m','-i',
        '-e','CI','-e','GITHUB_ACTIONS','-e','AIOFFICE_OWNED_GROUP_REFERENCE_PROOF',
        '-e','AIOFFICE_GROUP_REFERENCE_PROOF_CONNECTION','-e','AIOFFICE_GROUP_REFERENCE_PROOF_SOURCE_KEY',image]

    def graph():
        return digest_many([(t,o,scope) for t,o in automatic.SOURCE_TABLES+EXTRA]+[
            ('GroupBindings','Id',base+f" AND Id='{source}'"),
            ('GroupConnectorAccounts','Id',base+f" AND Id='{account}'"),
            ('GroupListenerLeases','ConnectorAccountId',base+f" AND ConnectorAccountId='{account}'")])

    def unchanged():
        assert retained()==original and permissions()==original_permissions,'First empty proof changed retained scopes or permissions'
        if completed is not None:assert graph()==completed,'First empty cleanup changed complete owned graph'

    failure=None
    try:
        built=subprocess.run(['docker','build','-f','tests/GroupReference.RuntimeProof/Dockerfile','-t',image,'.'],capture_output=True,text=True,timeout=300)
        assert built.returncode==0,'First empty executable build failed'
        source,events,key=prepare_source();source=automatic.canonical(source);events=tuple(automatic.canonical(x) for x in events)
        reference.require_source_key(key)
        assert source not in sources and len(events)==len(set(events))==2
        account=automatic.canonical(sql(f"SELECT LOWER(CONVERT(char(36),ConnectorAccountId)) FROM aioffice.GroupBindings WHERE {base} AND Id='{source}';"))
        assert account not in accounts;scope=base+f" AND BindingId='{source}'";unchanged()
        env=dict(os.environ,AIOFFICE_OWNED_GROUP_REFERENCE_PROOF='true',
            AIOFFICE_GROUP_REFERENCE_PROOF_CONNECTION='Server=sql;Database=AIOfficeLocal;User ID=aioffice_runtime;Password='+password+';Encrypt=true;TrustServerCertificate=true',
            AIOFFICE_GROUP_REFERENCE_PROOF_SOURCE_KEY=key)
        config=json.dumps(dict(tenantId=tenant,companyId=company,serviceId=service,sourceId=source,eventId=events[0]))

        def child(mode,line):
            reference.require_owned(directory,api)
            result=subprocess.run([*command,mode],input=config,env=env,capture_output=True,text=True,timeout=170)
            assert not result.stderr,'First empty child emitted private diagnostics'
            reference.require_reference_result(result,mode,[line])
            assert retained()==original,'First empty child changed retained scopes'
            if completed is not None:assert graph()==completed,'First empty child changed completed graph'

        child('automatic-no-work-prepare',PREPARE_LINE)
        assert sql(f"/* owned-first-empty-zero */ SELECT CONCAT((SELECT COUNT(*) FROM aioffice.GroupAutomaticInputPlans WHERE {scope}),N'|',"
            f"(SELECT COUNT(*) FROM aioffice.GroupOriginalAdmissionSidecars WHERE {scope}),N'|',"
            f"(SELECT COUNT(*) FROM aioffice.GroupBatchClaimReceipts WHERE {scope}),N'|',"
            f"(SELECT COUNT(*) FROM aioffice.GroupWorkCommitReceipts WHERE {scope}));")=='0|0|0|0'
        child('first-empty-commit',COMMIT_LINE)
        assert sql(f"/* owned-first-empty-counts */ SELECT CONCAT((SELECT COUNT(*) FROM aioffice.GroupAutomaticInputPlans WHERE {scope}),N'|',"
            f"(SELECT COUNT(*) FROM aioffice.GroupOriginalAdmissionSidecars WHERE {scope}),N'|',"
            f"(SELECT COUNT(*) FROM aioffice.GroupWorkCommitReceipts WHERE {scope}),N'|',"
            f"(SELECT COUNT(*) FROM aioffice.GroupWorkSourceDispositions WHERE {scope}),N'|',"
            f"(SELECT COUNT(*) FROM aioffice.GroupWorkRawDispositions WHERE {scope}),N'|',"
            f"(SELECT COUNT(*) FROM aioffice.GroupBatchTerminalReceipts WHERE {scope}),N'|',"
            f"(SELECT COUNT(*) FROM aioffice.GroupTerminalAdvances WHERE {scope}),N'|',"
            f"(SELECT COUNT(*) FROM aioffice.GroupTerminalAdvanceKeys WHERE {scope}),N'|',"
            f"(SELECT COUNT(*) FROM aioffice.GroupTerminalFrontierStates WHERE {scope}));")=='1|1|1|2|2|1|1|1|1'
        completed=graph();unchanged()
        # Explicitly require the exact owned baseline that the restoration below
        # reinstates; never grant over an arbitrary initial privilege state.
        table_ids=','.join("OBJECT_ID(N'aioffice."+x+"')" for x in DENIED_TABLES)
        assert sql("/* owned-first-empty-grants */ SELECT COUNT(*) FROM sys.database_permissions WHERE grantee_principal_id=USER_ID(N'aioffice_binding_runtime') "
            f"AND class=1 AND minor_id=0 AND type='SL' AND state='G' AND major_id IN ({table_ids});")=='3'
        first=None
        try:
            reference.require_owned(directory,api)
            assert sql('/* owned-first-empty-deny */ '+''.join(f'DENY SELECT ON OBJECT::aioffice.{t} TO aioffice_binding_runtime;' for t in DENIED_TABLES))==''
            denied_permissions=permissions()
            child('first-empty-recover',RECOVER_LINE)
            assert permissions()==denied_permissions,'First empty child changed denied permissions'
            assert graph()==completed,'First empty recovery changed full graph'
        except BaseException as error:first=error
        finally:
            try:
                reference.require_owned(directory,api)
                assert sql('/* owned-first-empty-restore */ '+''.join(f'REVOKE SELECT ON OBJECT::aioffice.{t} FROM aioffice_binding_runtime;GRANT SELECT ON OBJECT::aioffice.{t} TO aioffice_binding_runtime;' for t in DENIED_TABLES))==''
                assert permissions()==original_permissions,'First empty SELECT restoration differs'
            except BaseException as error:
                if first is None:first=error
        if first is not None:raise first
        unchanged()
    except BaseException as error:failure=error
    finally:
        try:automatic.close_owned_container(directory=directory,api=api,name=image,suffix=suffix,reference=reference)
        except BaseException as error:
            if failure is None:failure=error
        try:unchanged()
        except BaseException as error:
            if failure is None:failure=error
        try:
            removed=subprocess.run(['docker','image','rm',image],capture_output=True,text=True,timeout=30)
            assert removed.returncode==0,'First empty image cleanup failed'
        except BaseException as error:
            if failure is None:failure=error
        try:unchanged()
        except BaseException as error:
            if failure is None:failure=error
    if failure is not None:raise failure
    print(SUMMARY,flush=True)
