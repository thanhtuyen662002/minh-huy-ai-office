"""Owned SQL original-admission persistence. Synthetic profile; no model eval."""
import json
import importlib.util
import os
from pathlib import Path
import re
import subprocess
import uuid

_corruption_spec = importlib.util.spec_from_file_location('owned_original_admission_corruption',Path(__file__).with_name('smoke-group-original-admission-corruption.py'))
corruption = importlib.util.module_from_spec(_corruption_spec)
_corruption_spec.loader.exec_module(corruption)

RUNTIME_LINES = {
    'admission-original-prepare': 'PASS owned original admission actual two revisions inbox allocation no original sidecar attempt result or effects',
    'admission-original-expiry': 'PASS owned original admission actual both rows flushed expiry observation retained across immediate rewind rollback witness committed old claim refused legitimate replacement retains zero effects or HTTP',
    'admission-original-commit': 'PASS owned original admission actual eighteen SQL constraint controls shipping gate tokenizer own carrier atomic two row rollback committed reply loss fresh context exact immutable replay every column deny encrypted accepted result zero HTTP',
    'admission-original-recover': 'PASS owned original admission separate process epoch three accepted result FIRST recovered with sidecar SELECT unavailable empty released catalog zero admission calls or HTTP',
    'admission-original-resume': 'PASS owned original admission current epoch three exact original epoch two plan sidecar snapshots legacy input identities chunks hashes restored zero provider'
}
SUMMARY = 'PASS actual original admission atomic SQL rollback lost reply immutable replay every column deny expiry rollback durable witness rewind refusal epoch replacement accepted result FIRST sidecar SELECT deny restored both cleanup gates no provider or authentic profile qualification'


def verify(*, directory, api, manifest, tenant, company, service, sql, prepare_source, reference, automatic):
    reference.require_owned(directory, api)
    assert callable(sql) and callable(prepare_source)
    tenant, company, service = (automatic.canonical(x) for x in (tenant, company, service))
    installation = automatic.canonical(manifest['AIOFFICE_INSTALLATION_ID'])
    password = manifest['AIOFFICE_RUNTIME_PASSWORD']
    assert type(password) is str and re.fullmatch(r'[A-Za-z0-9_-]{32,128}', password)
    base = f"TenantId='{tenant}' AND CompanyId='{company}'"
    sources = tuple(automatic.canonical(x) for x in sql(f'SELECT LOWER(CONVERT(char(36),Id)) FROM aioffice.GroupBindings WHERE {base} ORDER BY Id;').splitlines())
    accounts = tuple(automatic.canonical(x) for x in sql(f'SELECT LOWER(CONVERT(char(36),Id)) FROM aioffice.GroupConnectorAccounts WHERE {base} ORDER BY Id;').splitlines())
    assert sources and accounts
    source_list = ','.join("'"+x+"'" for x in sources)
    extra = (('GroupAutomaticInputPlans','BatchId'), ('GroupOriginalAdmissionSidecars','BatchId'),
        ('GroupModelAttemptStarts','RequestId'), ('GroupModelAttemptResults','RequestId'),
        ('GroupModelAttemptFailures','RequestId'), ('GroupWorkRawDispositions','BatchId,CommittedSequence'))

    def digest(table, order, predicate):
        value = sql("SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),COALESCE("
            f"(SELECT * FROM aioffice.{table} WHERE {predicate} ORDER BY {order} FOR JSON PATH,INCLUDE_NULL_VALUES),N'[]'))),2);")
        assert type(value) is str and re.fullmatch(r'[0-9A-F]{64}', value)
        return value

    def digest_many(items):
        items = tuple(items); assert 1 <= len(items) <= 80
        statements = []
        for index,(table,order,predicate) in enumerate(items):
            statements.append(f"SELECT CONCAT('{index:02d}|',CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),COALESCE("
                f"(SELECT * FROM aioffice.{table} WHERE {predicate} ORDER BY {order} FOR JSON PATH,INCLUDE_NULL_VALUES),N'[]'))),2));")
        rows = sql('/* owned-original-admission-digest-batch */\n'+'\n'.join(statements)).splitlines()
        assert len(rows) == len(items), 'Admission digest batch row count differs'
        for index,row in enumerate(rows):
            assert re.fullmatch(f'{index:02d}'+r'\|[0-9A-F]{64}',row), 'Admission digest batch index or hash differs'
        return tuple(row[3:] for row in rows)

    def retained():
        return automatic.retained_snapshot(tenant=tenant,company=company,sources=sources,accounts=accounts,sql=sql) + digest_many(
            (t,o,base+f' AND BindingId IN ({source_list})') for t,o in extra)

    def permissions():
        value = sql("SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),(SELECT * FROM sys.database_permissions "
            "WHERE major_id=OBJECT_ID(N'aioffice.GroupOriginalAdmissionSidecars') ORDER BY grantee_principal_id,minor_id,type FOR JSON PATH,INCLUDE_NULL_VALUES))),2);")
        assert re.fullmatch(r'[0-9A-F]{64}', value)
        return value

    original = retained(); original_permissions = permissions()
    source = scope = account = completed = immutable = stable = original_claim = retired_claim = None
    suffix = uuid.uuid4().hex; image = 'aioffice-automatic-note-proof-'+suffix
    command = ['docker','run','--init','--rm','--name',image,'--label','aioffice.owned-proof='+suffix,
        '--network','aioffice-'+uuid.UUID(installation).hex+'_default','--read-only','--cap-drop','ALL',
        '--security-opt','no-new-privileges:true','--tmpfs','/tmp:rw,noexec,nosuid,size=16m','-i',
        '-e','CI','-e','GITHUB_ACTIONS','-e','AIOFFICE_OWNED_GROUP_REFERENCE_PROOF',
        '-e','AIOFFICE_GROUP_REFERENCE_PROOF_CONNECTION','-e','AIOFFICE_GROUP_REFERENCE_PROOF_SOURCE_KEY',image]

    def graph():
        return digest_many(tuple((t,o,scope) for t,o in automatic.SOURCE_TABLES+extra) + (
            ('GroupBindings','Id',base+f" AND Id='{source}'"),
            ('GroupConnectorAccounts','Id',base+f" AND Id='{account}'"),
            ('GroupListenerLeases','ConnectorAccountId',base+f" AND ConnectorAccountId='{account}'")))

    def unchanged():
        assert retained() == original and permissions() == original_permissions, 'Admission proof changed retained scopes or permissions'
        if completed is not None: assert graph() == completed, 'Admission cleanup changed complete owned graph'

    def require_fixed_source():
        current = digest_many((t,o,scope) for index,(t,o) in enumerate(automatic.SOURCE_TABLES) if index not in (9,10))
        if stable is not None: assert current == stable, 'Admission child changed original source authority notes or outbox'
        if original_claim is not None:
            assert digest('GroupBatchClaimReceipts','BatchId,Epoch',scope+' AND Epoch=2') == original_claim, 'Admission restart changed original claim'
        if retired_claim is not None:
            assert digest('GroupBatchClaimReceipts','BatchId,Epoch',scope+' AND Epoch=1') == retired_claim, 'Admission replaced retired original claim'
        return current

    failure = None
    try:
        built = subprocess.run(['docker','build','-f','tests/GroupReference.RuntimeProof/Dockerfile','-t',image,'.'],capture_output=True,text=True,timeout=300)
        assert built.returncode == 0, 'Owned admission executable build failed'
        source, events, key = prepare_source()
        source = automatic.canonical(source); events = tuple(automatic.canonical(x) for x in events)
        reference.require_source_key(key)
        assert source not in sources and len(events) == len(set(events)) == 2
        account = automatic.canonical(sql(f"SELECT LOWER(CONVERT(char(36),ConnectorAccountId)) FROM aioffice.GroupBindings WHERE {base} AND Id='{source}';"))
        assert account not in accounts
        scope = base+f" AND BindingId='{source}'"
        env = dict(os.environ,AIOFFICE_OWNED_GROUP_REFERENCE_PROOF='true',
            AIOFFICE_GROUP_REFERENCE_PROOF_CONNECTION='Server=sql;Database=AIOfficeLocal;User ID=aioffice_runtime;Password='+password+';Encrypt=true;TrustServerCertificate=true',
            AIOFFICE_GROUP_REFERENCE_PROOF_SOURCE_KEY=key)
        config = json.dumps(dict(tenantId=tenant,companyId=company,serviceId=service,sourceId=source,eventId=events[0]))
        for index,(mode,expected) in enumerate(RUNTIME_LINES.items()):
            if mode == 'admission-original-recover':
                first = None
                try:
                    reference.require_owned(directory,api)
                    sql('DENY SELECT ON OBJECT::aioffice.GroupOriginalAdmissionSidecars TO aioffice_binding_runtime;')
                    result = subprocess.run([*command,mode],input=config,env=env,capture_output=True,text=True,timeout=170)
                    assert not result.stderr
                    reference.require_reference_result(result,mode,[expected])
                except BaseException as error: first = error
                finally:
                    try:
                        reference.require_owned(directory,api)
                        sql('REVOKE SELECT ON OBJECT::aioffice.GroupOriginalAdmissionSidecars FROM aioffice_binding_runtime; GRANT SELECT ON OBJECT::aioffice.GroupOriginalAdmissionSidecars TO aioffice_binding_runtime;')
                        assert permissions() == original_permissions, 'Admission SELECT restoration differs'
                    except BaseException as error:
                        if first is None: first = error
                if first is not None: raise first
            else:
                result = subprocess.run([*command,mode],input=config,env=env,capture_output=True,text=True,timeout=170)
                assert not result.stderr
                reference.require_reference_result(result,mode,[expected])
            assert retained() == original and permissions() == original_permissions
            counts = sql(f"SELECT CONCAT((SELECT COUNT(*) FROM aioffice.GroupAutomaticInputPlans WHERE {scope}),N'|',"
                f"(SELECT COUNT(*) FROM aioffice.GroupOriginalAdmissionSidecars WHERE {scope}),N'|',"
                f"(SELECT COUNT(*) FROM aioffice.GroupModelAttemptStarts WHERE {scope}),N'|',"
                f"(SELECT COUNT(*) FROM aioffice.GroupModelAttemptResults WHERE {scope}),N'|',"
                f"(SELECT COUNT(*) FROM aioffice.GroupBatchClaimReceipts WHERE {scope}));")
            assert counts == ('0|0|0|0|0','0|0|0|0|1','1|1|1|1|2','1|1|1|1|3','1|1|1|1|3')[index]
            assert sql(f"SELECT COUNT(*) FROM aioffice.GroupCustomerRequests WHERE {scope};") == '0'
            assert sql(f"SELECT COUNT(*) FROM aioffice.GroupModelAttemptFailures WHERE {scope};") == '0'
            observed = require_fixed_source()
            if index == 0: stable = observed
            if index == 1:
                retired_claim = digest('GroupBatchClaimReceipts','BatchId,Epoch',scope+' AND Epoch=1')
                assert sql(f"SELECT COUNT(*) FROM aioffice.GroupBatchClaimStates WHERE {scope} AND Epoch=1 AND ExpiryObservedAtUtc=ExpiresAtUtc;") == '1'
            if index >= 2:
                durable = tuple(digest(t,o,scope) for t,o in extra[:4])
                if index == 2:
                    immutable = durable
                    original_claim = digest('GroupBatchClaimReceipts','BatchId,Epoch',scope+' AND Epoch=2')
                assert durable == immutable, 'Admission restart replaced original plan sidecar start or result'
                assert sql(f"SELECT COUNT(*) FROM aioffice.GroupOriginalAdmissionSidecars s INNER JOIN aioffice.GroupAutomaticInputPlans p "
                    "ON s.TenantId=p.TenantId AND s.CompanyId=p.CompanyId AND s.BindingId=p.BindingId AND s.BatchId=p.BatchId "
                    f"WHERE s.TenantId='{tenant}' AND s.CompanyId='{company}' AND s.BindingId='{source}' "
                    "AND s.PlanSha256=p.PlanSha256 AND s.CreatedAtUtc=p.CreatedAtUtc AND s.OriginalClaimEpoch=p.OriginalClaimEpoch "
                    "AND s.OriginalClaimOperationId=p.OriginalClaimOperationId AND s.OriginalClaimEpoch=2 "
                    "AND s.SidecarSha256=CONVERT(varchar(64),HASHBYTES('SHA2_256',s.Sidecar),2);") == '1'
            completed = graph()
        def run_corruption_child(mode):
            assert mode in ('admission-original-refuse','admission-original-resume')
            result = subprocess.run([*command,mode],input=config,env=env,capture_output=True,text=True,timeout=170)
            assert not result.stderr
            expected = corruption.REFUSAL_LINE if mode == 'admission-original-refuse' else RUNTIME_LINES[mode]
            reference.require_reference_result(result,mode,[expected])
        batch = automatic.canonical(sql(f"SELECT LOWER(CONVERT(char(36),BatchId)) FROM aioffice.GroupOriginalAdmissionSidecars WHERE {scope};"))
        corruption.verify(directory=directory,api=api,tenant=tenant,company=company,source=source,batch=batch,
            sql=sql,run_child=run_corruption_child,reference=reference,assert_unchanged=unchanged)
    except BaseException as error: failure = error
    finally:
        try: automatic.close_owned_container(directory=directory,api=api,name=image,suffix=suffix,reference=reference)
        except BaseException as error:
            if failure is None: failure = error
        try: unchanged()
        except BaseException as error:
            if failure is None: failure = error
        try:
            removed = subprocess.run(['docker','image','rm',image],capture_output=True,text=True,timeout=30)
            assert removed.returncode == 0, 'Owned admission image cleanup failed'
        except BaseException as error:
            if failure is None: failure = error
        try: unchanged()
        except BaseException as error:
            if failure is None: failure = error
    if failure is not None: raise failure
    for line in RUNTIME_LINES.values(): print(line,flush=True)
    print(SUMMARY,flush=True)
    print(corruption.SUMMARY,flush=True)
