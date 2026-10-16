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

_permission_spec = importlib.util.spec_from_file_location('owned_original_admission_permissions',Path(__file__).with_name('smoke-group-original-admission-permissions.py'))
permission_proof = importlib.util.module_from_spec(_permission_spec)
_permission_spec.loader.exec_module(permission_proof)

_guard_spec = importlib.util.spec_from_file_location('owned_accepted_guard_counterfactual',Path(__file__).with_name('smoke-group-accepted-guard-counterfactual.py'))
guard_counterfactual = importlib.util.module_from_spec(_guard_spec)
_guard_spec.loader.exec_module(guard_counterfactual)

RUNTIME_LINES = {
    'admission-original-prepare': 'PASS owned original admission actual two revisions inbox allocation no original sidecar attempt result or effects',
    'admission-original-expiry': 'PASS owned original admission actual both rows flushed expiry observation retained across immediate rewind rollback witness committed old claim refused legitimate replacement retains zero effects or HTTP',
    'admission-original-commit': 'PASS owned original admission actual eighteen SQL constraint controls shipping gate tokenizer own carrier atomic two row rollback committed reply loss fresh context exact immutable replay every column deny encrypted accepted result zero HTTP',
    'admission-original-recover': 'PASS owned original admission separate process epoch three accepted result FIRST recovered with sidecar SELECT unavailable empty released catalog zero admission calls or HTTP',
    'admission-original-resume': 'PASS owned original admission current epoch three exact original epoch two plan sidecar snapshots legacy input identities chunks hashes restored zero provider'
}
SUMMARY = 'PASS actual original admission atomic SQL rollback lost reply immutable replay every column deny expiry rollback durable witness rewind refusal epoch replacement accepted result FIRST sidecar SELECT deny restored both cleanup gates no provider or authentic profile qualification'
READ_RUNTIME_LINE = 'PASS owned original admission actual SQL exact original sidecar select own row and four single selector counterfactual empty reads same locked transaction unchanged original decoder zero keys or provider'
READ_SUMMARY = 'PASS actual original admission shipping sidecar SQL predicate positive and tenant company binding batch counterfactual empty reads same owned authority and transaction no foreign authorized API claim both cleanup gates unchanged graphs zero keys or provider'
JOURNAL_RUNTIME_LINE = 'PASS owned original admission actual current gate prepared journal and companion both flushed rollback no rows committed exact scoped provenance current snapshot deadline capped replay no second permit actual current SQL before and after synthetic adapter outside SQL once protected accepted result FIRST zero HTTP no model qualification'
JOURNAL_SUMMARY = 'PASS actual admitted original model journal authentic sidecar same transaction two row flush rollback committed exact companion replay no second permit actual Gateway execution current SQL before after wait one use outside SQL accepted result FIRST without sidecar or companion SELECT retained full graphs both cleanup gates synthetic adapter zero HTTP no model qualification'
JOURNAL_EXPIRY_RUNTIME_LINE = 'PASS owned admitted journal authentic expiry observed UTC before claim issuance actual entry exact witness old interval refused replacement current no second attempt accepted graphs retained zero provider'

BATCH_COMMIT_LINE = 'PASS owned restored batch actual original SQL restored accepted result FIRST real claimed processor three automatic notes one AI two host four evidence outbox terminal and signed frontier current epoch four cold model factory'
BATCH_RECOVER_LINE = 'PASS owned restored batch separate process expired claim terminal FIRST original SELECT denied original effects and signed frontier unchanged cold model factory'
BATCH_SUMMARY = 'PASS actual restored batch original recovery accepted result automatic note evidence outbox terminal signed frontier expired claim terminal FIRST original SELECT denied fresh process unchanged complete scopes permissions both cleanup gates cold model factory no model qualification'
COMPLETION_TABLES = (("GroupBatchTerminalReceipts","BatchId"),("GroupTerminalAdvances","Version"),
    ("GroupTerminalAdvanceKeys","KeyId"),("GroupTerminalFrontierStates","BindingId"))


def verify(*, directory, api, manifest, tenant, company, service, sql, prepare_source, prepare_counterfactual_source, reference, automatic):
    reference.require_owned(directory, api)
    assert callable(sql) and callable(prepare_source) and callable(prepare_counterfactual_source)
    assert prepare_counterfactual_source is not prepare_source
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
        ('GroupModelAttemptFailures','RequestId'), ('GroupWorkRawDispositions','BatchId,CommittedSequence'),
        ('GroupModelAttemptAdmissions','RequestId'))

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
            (t,o,base+f' AND BindingId IN ({source_list})') for t,o in extra+COMPLETION_TABLES)

    def permissions():
        value = sql("SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),(SELECT * FROM sys.database_permissions "
            "WHERE major_id IN (OBJECT_ID(N'aioffice.GroupOriginalAdmissionSidecars'),OBJECT_ID(N'aioffice.GroupModelAttemptAdmissions'),OBJECT_ID(N'aioffice.GroupAutomaticInputPlans')) ORDER BY major_id,grantee_principal_id,minor_id,type FOR JSON PATH,INCLUDE_NULL_VALUES))),2);")
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
        return digest_many(tuple((t,o,scope) for t,o in automatic.SOURCE_TABLES+extra+COMPLETION_TABLES) + (
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
                    sql('DENY SELECT ON OBJECT::aioffice.GroupOriginalAdmissionSidecars TO aioffice_binding_runtime; DENY SELECT ON OBJECT::aioffice.GroupModelAttemptAdmissions TO aioffice_binding_runtime;')
                    result = subprocess.run([*command,mode],input=config,env=env,capture_output=True,text=True,timeout=170)
                    assert not result.stderr
                    reference.require_reference_result(result,mode,[expected])
                except BaseException as error: first = error
                finally:
                    try:
                        reference.require_owned(directory,api)
                        sql('REVOKE SELECT ON OBJECT::aioffice.GroupOriginalAdmissionSidecars FROM aioffice_binding_runtime; GRANT SELECT ON OBJECT::aioffice.GroupOriginalAdmissionSidecars TO aioffice_binding_runtime; REVOKE SELECT ON OBJECT::aioffice.GroupModelAttemptAdmissions FROM aioffice_binding_runtime; GRANT SELECT ON OBJECT::aioffice.GroupModelAttemptAdmissions TO aioffice_binding_runtime;')
                        assert permissions() == original_permissions, 'Admission SELECT restoration differs'
                    except BaseException as error:
                        if first is None: first = error
                if first is not None: raise first
            else:
                result = subprocess.run([*command,mode],input=config,env=env,capture_output=True,text=True,timeout=170)
                assert not result.stderr
                reference.require_reference_result(result,mode,[expected,JOURNAL_RUNTIME_LINE] if mode == 'admission-original-commit' else [expected])
            assert retained() == original and permissions() == original_permissions
            counts = sql(f"SELECT CONCAT((SELECT COUNT(*) FROM aioffice.GroupAutomaticInputPlans WHERE {scope}),N'|',"
                f"(SELECT COUNT(*) FROM aioffice.GroupOriginalAdmissionSidecars WHERE {scope}),N'|',"
                f"(SELECT COUNT(*) FROM aioffice.GroupModelAttemptStarts WHERE {scope}),N'|',"
                f"(SELECT COUNT(*) FROM aioffice.GroupModelAttemptResults WHERE {scope}),N'|',"
                f"(SELECT COUNT(*) FROM aioffice.GroupBatchClaimReceipts WHERE {scope}));")
            assert counts == ('0|0|0|0|0','0|0|0|0|1','1|1|1|1|2','1|1|1|1|3','1|1|1|1|3')[index]
            assert sql(f"SELECT COUNT(*) FROM aioffice.GroupModelAttemptAdmissions WHERE {scope};") == ('0' if index < 2 else '1')
            assert sql(f"SELECT COUNT(*) FROM aioffice.GroupCustomerRequests WHERE {scope};") == '0'
            assert sql(f"SELECT COUNT(*) FROM aioffice.GroupModelAttemptFailures WHERE {scope};") == '0'
            observed = require_fixed_source()
            if index == 0: stable = observed
            if index == 1:
                retired_claim = digest('GroupBatchClaimReceipts','BatchId,Epoch',scope+' AND Epoch=1')
                assert sql(f"SELECT COUNT(*) FROM aioffice.GroupBatchClaimStates WHERE {scope} AND Epoch=1 AND ExpiryObservedAtUtc=ExpiresAtUtc;") == '1'
            if index >= 2:
                durable = tuple(digest(t,o,scope) for t,o in extra[:4]+(extra[-1],))
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
        def run_permission_child(mode):
            assert mode in ('admission-original-permission','admission-original-resume')
            result = subprocess.run([*command,mode],input=config,env=env,capture_output=True,text=True,timeout=170)
            assert not result.stderr
            expected = permission_proof.REFUSAL_LINE if mode == 'admission-original-permission' else RUNTIME_LINES[mode]
            reference.require_reference_result(result,mode,[expected])
        permission_proof.verify(directory=directory,api=api,tenant=tenant,company=company,source=source,batch=batch,
            sql=sql,run_child=run_permission_child,reference=reference,assert_unchanged=unchanged)
        unchanged()
        result = subprocess.run([*command,'admission-original-sql-read'],input=config,env=env,capture_output=True,text=True,timeout=170)
        assert not result.stderr
        reference.require_reference_result(result,'admission-original-sql-read',[READ_RUNTIME_LINE])
        unchanged()
        result = subprocess.run([*command,'admission-original-resume'],input=config,env=env,capture_output=True,text=True,timeout=170)
        assert not result.stderr
        reference.require_reference_result(result,'admission-original-resume',[RUNTIME_LINES['admission-original-resume']])
        unchanged()
        counterfactual_summary = guard_counterfactual.verify(directory=directory,api=api,installation=installation,tenant=tenant,company=company,
            service=service,password=password,sql=sql,prepare_source=prepare_counterfactual_source,reference=reference,automatic=automatic,
            runtime_lines=RUNTIME_LINES,assert_unchanged=unchanged)
        assert counterfactual_summary == guard_counterfactual.SUMMARY
        before_journal_expiry = tuple(digest(t,o,scope) for t,o in extra)
        old_third_claim = digest('GroupBatchClaimReceipts','BatchId,Epoch',scope+' AND Epoch=3')
        result = subprocess.run([*command,'admission-original-journal-expiry'],input=config,env=env,capture_output=True,text=True,timeout=170)
        assert not result.stderr
        reference.require_reference_result(result,'admission-original-journal-expiry',[JOURNAL_EXPIRY_RUNTIME_LINE])
        assert tuple(digest(t,o,scope) for t,o in extra) == before_journal_expiry
        assert digest('GroupBatchClaimReceipts','BatchId,Epoch',scope+' AND Epoch=3') == old_third_claim
        assert sql(f"SELECT COUNT(*) FROM aioffice.GroupBatchClaimStates WHERE {scope} AND Epoch=4 AND ExpiryObservedAtUtc IS NULL;") == '1'
        assert sql(f"SELECT COUNT(*) FROM aioffice.GroupBatchClaimReceipts WHERE {scope};") == '4'
        require_fixed_source(); completed = graph(); unchanged()

        # The old immutable/permission/expiry oracles are complete before any
        # note is added. These phases now use the fixed real batch composition.
        before_completion = tuple(digest(t,o,scope) for index,(t,o) in enumerate(extra) if index != 5)
        result = subprocess.run([*command,'restored-batch-commit'],input=config,env=env,capture_output=True,text=True,timeout=170)
        assert not result.stderr
        reference.require_reference_result(result,'restored-batch-commit',[BATCH_COMMIT_LINE])
        assert tuple(digest(t,o,scope) for index,(t,o) in enumerate(extra) if index != 5) == before_completion
        counts = sql("SELECT CONCAT("+",N'|',".join(f"(SELECT COUNT(*) FROM aioffice.{table} WHERE {scope})" for table in (
            'GroupCustomerRequests','GroupRequestRevisions','GroupRequestEvidence','GroupWorkCommitReceipts','GroupWorkSourceDispositions',
            'GroupWorkRawDispositions','GroupNotesCommittedOutbox','GroupNotesCommittedItems','GroupBatchTerminalReceipts',
            'GroupTerminalAdvances','GroupTerminalAdvanceKeys','GroupTerminalFrontierStates'))+");")
        assert counts == '3|3|4|1|2|2|1|3|1|1|1|1', 'Restored completion graph differs'
        completed = graph(); unchanged()
        first = None
        try:
            reference.require_owned(directory,api)
            sql('DENY SELECT ON OBJECT::aioffice.GroupAutomaticInputPlans TO aioffice_binding_runtime; DENY SELECT ON OBJECT::aioffice.GroupOriginalAdmissionSidecars TO aioffice_binding_runtime; DENY SELECT ON OBJECT::aioffice.GroupModelAttemptAdmissions TO aioffice_binding_runtime;')
            result = subprocess.run([*command,'restored-batch-recover'],input=config,env=env,capture_output=True,text=True,timeout=170)
            assert not result.stderr
            reference.require_reference_result(result,'restored-batch-recover',[BATCH_RECOVER_LINE])
            assert graph() == completed, 'Terminal recovery changed original completion graph'
        except BaseException as error: first = error
        finally:
            try:
                reference.require_owned(directory,api)
                sql('REVOKE SELECT ON OBJECT::aioffice.GroupAutomaticInputPlans FROM aioffice_binding_runtime; GRANT SELECT ON OBJECT::aioffice.GroupAutomaticInputPlans TO aioffice_binding_runtime; REVOKE SELECT ON OBJECT::aioffice.GroupOriginalAdmissionSidecars FROM aioffice_binding_runtime; GRANT SELECT ON OBJECT::aioffice.GroupOriginalAdmissionSidecars TO aioffice_binding_runtime; REVOKE SELECT ON OBJECT::aioffice.GroupModelAttemptAdmissions FROM aioffice_binding_runtime; GRANT SELECT ON OBJECT::aioffice.GroupModelAttemptAdmissions TO aioffice_binding_runtime;')
                assert permissions() == original_permissions, 'Completion SELECT restoration differs'
            except BaseException as error:
                if first is None: first = error
        if first is not None: raise first
        unchanged()
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
    print(permission_proof.SUMMARY,flush=True)
    print(READ_SUMMARY,flush=True)
    print(JOURNAL_SUMMARY,flush=True)
    print(JOURNAL_EXPIRY_RUNTIME_LINE,flush=True)
    print(counterfactual_summary,flush=True)
    print(BATCH_SUMMARY,flush=True)
