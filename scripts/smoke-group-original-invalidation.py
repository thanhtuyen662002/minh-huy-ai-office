"""Owned zero-contributor invalidation foundation; no continuation/provider/model qualification."""
from datetime import datetime, timedelta
import json
import os
import re
import subprocess
import uuid

ADMISSION_LINE = 'PASS owned original admission actual two revisions inbox allocation no original sidecar attempt result or effects'
PREPARE_LINE = 'PASS owned original invalidation zero prepare actual missing original current metadata legacy V2 epoch one two candidates empty history zero checkpoint keys effects or provider'
COMMIT_LINE = 'PASS owned original invalidation zero commit actual Core edit beyond original cutoff one immutable checkpoint committed reply loss exact original graph legacy reads and starts refused zero checkpoint keys effects or provider'
REPLAY_LINE = 'PASS owned original invalidation zero separate process exact epoch one checkpoint replay original bytes digest claims allocation unchanged zero keys effects or provider'
SUMMARY = 'PASS actual original invalidation zero contributor legacy V2 missing then current metadata actual Core edit original cutoff two immutable quarantine checkpoint committed reply loss fresh process exact replay original claims allocation retained graphs permissions both cleanup gates no replacement continuation provider model or production qualification'
EXTRA = (('GroupAutomaticInputPlans', 'BatchId'), ('GroupOriginalAdmissionSidecars', 'BatchId'),
    ('GroupModelAttemptAdmissions', 'RequestId'), ('GroupModelAttemptStarts', 'RequestId'),
    ('GroupModelAttemptResults', 'RequestId'), ('GroupModelAttemptFailures', 'RequestId'),
    ('GroupWorkRawDispositions', 'BatchId,CommittedSequence'), ('GroupBatchTerminalReceipts', 'BatchId'),
    ('GroupTerminalAdvances', 'Version'), ('GroupTerminalAdvanceKeys', 'KeyId'), ('GroupTerminalFrontierStates', 'BindingId'),
    ('GroupOriginalInvalidations', 'BatchId'))
ORIGINAL_TABLES = ('GroupAutomaticInputPlans', 'GroupOriginalAdmissionSidecars', 'GroupModelAttemptAdmissions',
    'GroupModelAttemptStarts', 'GroupModelAttemptResults', 'GroupModelAttemptFailures',
    'GroupWorkCommitReceipts', 'GroupWorkSourceDispositions', 'GroupWorkRawDispositions',
    'GroupCustomerRequests', 'GroupRequestRevisions', 'GroupRequestEvidence', 'GroupNotesCommittedOutbox',
    'GroupNotesCommittedItems', 'GroupBatchTerminalReceipts', 'GroupTerminalAdvances', 'GroupTerminalAdvanceKeys',
    'GroupTerminalFrontierStates')


def verify(*, directory, api, manifest, tenant, company, service, sql, prepare_source, edit_source, reference, automatic):
    reference.require_owned(directory, api)  # Before SQL, callbacks, configuration or Docker.
    assert all(callable(value) for value in (sql, prepare_source, edit_source))

    def canonical(value):
        assert type(value) is str and re.fullmatch(r'[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}', value), 'Invalidation identity shape differs'
        return automatic.canonical(value)

    tenant, company, service = (canonical(value) for value in (tenant, company, service))
    installation = canonical(manifest['AIOFFICE_INSTALLATION_ID'])
    password = manifest['AIOFFICE_RUNTIME_PASSWORD']
    assert type(password) is str and re.fullmatch(r'[A-Za-z0-9_-]{32,128}', password)
    base = f"TenantId='{tenant}' AND CompanyId='{company}'"
    sources = tuple(canonical(value) for value in sql(f'SELECT LOWER(CONVERT(char(36),Id)) FROM aioffice.GroupBindings WHERE {base} ORDER BY Id;').splitlines())
    accounts = tuple(canonical(value) for value in sql(f'SELECT LOWER(CONVERT(char(36),Id)) FROM aioffice.GroupConnectorAccounts WHERE {base} ORDER BY Id;').splitlines())
    assert sources and accounts and len(set(sources)) == len(sources) and len(set(accounts)) == len(accounts)
    source_list = ','.join("'" + value + "'" for value in sources)

    def digest_many(items):
        items = tuple(items); assert 1 <= len(items) <= 80
        statements = [f"SELECT CONCAT('{index:02d}|',CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),COALESCE("
            f"(SELECT * FROM aioffice.{table} WHERE {predicate} ORDER BY {order} FOR JSON PATH,INCLUDE_NULL_VALUES),N'[]'))),2));"
            for index, (table, order, predicate) in enumerate(items)]
        value = sql('/* owned-original-invalidation-digests */\n' + '\n'.join(statements))
        assert type(value) is str and len(value) <= 6000
        rows = value.splitlines()
        assert len(rows) == len(items), 'Invalidation graph digest count differs'
        assert all(re.fullmatch(f'{index:02d}' + r'\|[0-9A-F]{64}', line) for index, line in enumerate(rows)), 'Invalidation graph digest shape differs'
        return tuple(line[3:] for line in rows)

    def retained():
        return automatic.retained_snapshot(tenant=tenant, company=company, sources=sources, accounts=accounts, sql=sql) + digest_many(
            (table, order, base + f' AND BindingId IN ({source_list})') for table, order in EXTRA)

    def permissions():
        value = sql("/* owned-original-invalidation-permissions */ SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),"
            "(SELECT (SELECT * FROM sys.database_permissions ORDER BY class,major_id,minor_id,grantee_principal_id,grantor_principal_id,type FOR JSON PATH,INCLUDE_NULL_VALUES) AS rights,"
            "(SELECT role_principal_id,member_principal_id FROM sys.database_role_members ORDER BY role_principal_id,member_principal_id FOR JSON PATH) AS memberships,"
            "(SELECT object_id,principal_id FROM sys.objects WHERE schema_id=SCHEMA_ID(N'aioffice') ORDER BY object_id FOR JSON PATH,INCLUDE_NULL_VALUES) AS owners FOR JSON PATH,INCLUDE_NULL_VALUES))),2);")
        assert type(value) is str and re.fullmatch(r'[0-9A-F]{64}', value), 'Invalidation permission digest differs'
        return value

    def require_grants():
        assert sql("/* owned-original-invalidation-grants */ SELECT CONCAT("
            "(SELECT COUNT(*) FROM sys.database_permissions WHERE grantee_principal_id=USER_ID(N'aioffice_binding_runtime') AND class=1 AND minor_id=0 "
            "AND major_id=OBJECT_ID(N'aioffice.GroupOriginalInvalidations') AND type IN ('SL','IN') AND state='G'),N'|',"
            "(SELECT COUNT(*) FROM sys.database_permissions WHERE grantee_principal_id=USER_ID(N'aioffice_binding_runtime') AND class=1 AND minor_id=0 "
            "AND major_id=OBJECT_ID(N'aioffice.GroupOriginalInvalidations') AND type IN ('UP','DL','AL','TO') AND state='D'),N'|',"
            "(SELECT COUNT(*) FROM sys.database_permissions WHERE grantee_principal_id=USER_ID(N'aioffice_binding_runtime') AND class=1 AND minor_id>0 "
            "AND major_id=OBJECT_ID(N'aioffice.GroupOriginalInvalidations') AND type='UP' AND state='D'));") == '2|4|13', 'Invalidation exact grants differ'

    original = retained(); original_permissions = permissions(); require_grants()
    source = account = scope = completed = None
    suffix = uuid.uuid4().hex; image = 'aioffice-automatic-note-proof-' + suffix
    command = ['docker', 'run', '--init', '--rm', '--name', image, '--label', 'aioffice.owned-proof=' + suffix,
        '--network', 'aioffice-' + uuid.UUID(installation).hex + '_default', '--read-only', '--cap-drop', 'ALL',
        '--security-opt', 'no-new-privileges:true', '--tmpfs', '/tmp:rw,noexec,nosuid,size=16m', '-i',
        '-e', 'CI', '-e', 'GITHUB_ACTIONS', '-e', 'AIOFFICE_OWNED_GROUP_REFERENCE_PROOF',
        '-e', 'AIOFFICE_GROUP_REFERENCE_PROOF_CONNECTION', '-e', 'AIOFFICE_GROUP_REFERENCE_PROOF_SOURCE_KEY', image]

    def graph():
        items = [(table, order, scope) for table, order in automatic.SOURCE_TABLES + EXTRA] + [
            ('GroupBindings', 'Id', base + f" AND Id='{source}'"),
            ('GroupConnectorAccounts', 'Id', base + f" AND Id='{account}'"),
            ('GroupListenerLeases', 'ConnectorAccountId', base + f" AND ConnectorAccountId='{account}'"),
            ('GroupAccountCoverageGaps', 'ConnectorAccountId,ListenerEpoch,Reason', base + f" AND ConnectorAccountId='{account}'"),
            ('GroupListenerCommandReceipts', 'ServiceId,CredentialEpoch,Nonce', base + f" AND ConnectorAccountId='{account}'")]
        names = [item[0] for item in items]; assert len(names) == len(set(names)) and set(ORIGINAL_TABLES) <= set(names)
        return dict(zip(names, digest_many(items)))

    def unchanged():
        assert retained() == original and permissions() == original_permissions, 'Invalidation proof changed retained scopes or permissions'
        require_grants()
        if completed is not None: assert graph() == completed, 'Invalidation cleanup changed complete owned graph'

    def transition(before, after, allowed):
        assert before.keys() == after.keys() and all(before[name] == after[name] for name in before if name not in allowed), 'Invalidation stage changed an unrelated graph'

    def counts(plans, checkpoints, claims):
        tables = ORIGINAL_TABLES + ('GroupOriginalInvalidations', 'GroupBatchClaimStates', 'GroupBatchClaimReceipts')
        query = "/* owned-original-invalidation-counts */ SELECT CONCAT(" + ",N'|',".join(
            f'(SELECT COUNT(*) FROM aioffice.{table} WHERE {scope})' for table in tables) + ');'
        expected = [plans] + [0] * 17 + [checkpoints, claims, claims]
        assert sql(query) == '|'.join(map(str, expected)), 'Invalidation original/model/effect/claim counts differ'

    def allocation():
        assert sql(f"/* owned-original-invalidation-allocation */ SELECT CONCAT((SELECT COUNT(*) FROM aioffice.GroupBatchAllocations WHERE {scope} "
            "AND AfterSequence=0 AND AllocatedThroughSequence=2 AND ObservedCommittedThroughSequence=2 AND RawRevisionCount=2),N'|',"
            f"(SELECT COUNT(*) FROM aioffice.GroupBatchAllocatedRevisions WHERE {scope}),N'|',"
            f"(SELECT COUNT(*) FROM aioffice.GroupIngressInbox WHERE {scope}));") == '1|2|2', 'Invalidation original allocation differs'

    failure = None
    try:
        reference.require_owned(directory, api)
        built = subprocess.run(['docker', 'build', '-f', 'tests/GroupReference.RuntimeProof/Dockerfile', '-t', image, '.'], capture_output=True, text=True, timeout=300)
        assert built.returncode == 0, 'Invalidation executable build failed'
        reference.require_owned(directory, api)
        prepared_source = prepare_source()
        assert type(prepared_source) is tuple and len(prepared_source) == 3, 'Invalidation prepare callback shape differs'
        source, events, key = prepared_source; source = canonical(source)
        assert type(events) in (tuple, list) and len(events) == 2, 'Invalidation event pair differs'
        events = tuple(canonical(value) for value in events)
        reference.require_source_key(key)
        assert source not in sources and len(events) == len(set(events)) == 2, 'Invalidation fresh source/events differ'
        account = canonical(sql(f"SELECT LOWER(CONVERT(char(36),ConnectorAccountId)) FROM aioffice.GroupBindings WHERE {base} AND Id='{source}';"))
        assert account not in accounts; scope = base + f" AND BindingId='{source}'"; unchanged()
        first_message = canonical(sql(f"/* owned-original-invalidation-first */ SELECT LOWER(CONVERT(char(36),MessageId)) FROM aioffice.GroupMessageRevisions "
            f"WHERE {scope} AND CommittedSequence=1 AND Revision=1 AND Kind=1;"))
        environment = dict(os.environ, AIOFFICE_OWNED_GROUP_REFERENCE_PROOF='true',
            AIOFFICE_GROUP_REFERENCE_PROOF_CONNECTION='Server=sql;Database=AIOfficeLocal;User ID=aioffice_runtime;Password=' + password + ';Encrypt=true;TrustServerCertificate=true',
            AIOFFICE_GROUP_REFERENCE_PROOF_SOURCE_KEY=key)
        configuration = json.dumps(dict(tenantId=tenant, companyId=company, serviceId=service, sourceId=source, eventId=events[0]))

        def child(mode, line):
            reference.require_owned(directory, api)
            result = subprocess.run([*command, mode], input=configuration, env=environment, capture_output=True, text=True, timeout=170)
            assert type(result.stderr) is str and not result.stderr, 'Invalidation child emitted private diagnostics'
            reference.require_reference_result(result, mode, [line])
            assert retained() == original and permissions() == original_permissions, 'Invalidation child changed retained scopes or permissions'

        counts(0, 0, 0)
        fresh = graph(); child('admission-original-prepare', ADMISSION_LINE)
        counts(0, 0, 0); allocation(); allocated = graph()
        transition(fresh, allocated, {'GroupIngressInbox', 'GroupBatchAllocations', 'GroupBatchAllocatedRevisions', 'GroupSourceStates'})
        child('invalidation-zero-prepare', PREPARE_LINE)
        counts(1, 0, 1); allocation(); prepared = graph()
        transition(allocated, prepared, {'GroupBatchClaimStates', 'GroupBatchClaimReceipts', 'GroupAutomaticInputPlans'})
        assert sql(f"/* owned-original-invalidation-legacy */ SELECT COUNT(*) FROM aioffice.GroupAutomaticInputPlans WHERE {scope} "
            "AND FormatVersion=2 AND OriginalClaimEpoch=1 AND PlanSha256=CONVERT(varchar(64),HASHBYTES('SHA2_256',[Plan]),2);") == '1', 'Invalidation legacy original differs'
        completed = prepared; unchanged()
        reference.require_owned(directory, api)
        edit_result = edit_source(source)
        assert type(edit_result) is tuple and len(edit_result) == 2, 'Invalidation edit callback shape differs'
        status, ack = edit_result
        assert type(status) is int and status == 200 and type(ack) is dict and set(ack) == {
            'source', 'messageId', 'revision', 'committedSequence', 'committedAtUtc', 'wasAlreadyCommitted'}, 'Invalidation Core edit ACK shape differs'
        assert ack['source'] == dict(tenantId=tenant, companyId=company, sourceBindingId=source) and ack['messageId'] == first_message, 'Invalidation Core edit scope/message differs'
        assert type(ack['revision']) is int and ack['revision'] == 2 and type(ack['committedSequence']) is int and ack['committedSequence'] == 3 and ack['wasAlreadyCommitted'] is False, 'Invalidation Core edit revision differs'
        assert type(ack['committedAtUtc']) is str and len(ack['committedAtUtc']) <= 48, 'Invalidation Core edit time differs'
        try: committed = datetime.fromisoformat(ack['committedAtUtc'].replace('Z', '+00:00'))
        except ValueError: raise AssertionError('Invalidation Core edit time differs') from None
        assert committed.utcoffset() == timedelta(0), 'Invalidation Core edit time is not UTC'
        third = canonical(sql(f"/* owned-original-invalidation-third */ SELECT LOWER(CONVERT(char(36),Id)) FROM aioffice.GroupIngressOutbox WHERE {scope} AND MessageId='{first_message}' AND Revision=2 AND CommittedSequence=3;"))
        assert third not in events, 'Invalidation third event was reused'
        assert sql(f"/* owned-original-invalidation-edit */ SELECT CONCAT((SELECT COUNT(*) FROM aioffice.GroupMessageRevisions WHERE {scope}),N'|',"
            f"(SELECT COUNT(*) FROM aioffice.GroupIngressReceipts WHERE {scope}),N'|',(SELECT COUNT(*) FROM aioffice.GroupIngressOutbox WHERE {scope}),N'|',"
            f"(SELECT COUNT(*) FROM aioffice.GroupMessageRevisions WHERE {scope} AND MessageId='{first_message}' AND Revision=2 AND CommittedSequence=3 AND Kind=3),N'|',"
            f"(SELECT COUNT(*) FROM aioffice.GroupSourceStates WHERE {scope} AND CommittedSequence=3 AND ScheduledThroughSequence=2));") == '3|3|3|1|1', 'Invalidation actual edit graph differs'
        allocation(); counts(1, 0, 1); edited = graph()
        transition(prepared, edited, {'GroupMessageRevisions', 'GroupIngressReceipts', 'GroupSourceStates', 'GroupIngressOutbox', 'GroupListenerLeases'})
        completed = edited; unchanged()
        child('invalidation-zero-commit', COMMIT_LINE)
        counts(1, 1, 1); allocation(); committed_graph = graph()
        transition(edited, committed_graph, {'GroupOriginalInvalidations'})
        completed = committed_graph; unchanged()
        child('invalidation-zero-replay', REPLAY_LINE)
        counts(1, 1, 1); allocation(); unchanged()
    except BaseException as error: failure = error
    finally:
        try: automatic.close_owned_container(directory=directory, api=api, name=image, suffix=suffix, reference=reference)
        except BaseException as error:
            if failure is None: failure = error
        try: unchanged()
        except BaseException as error:
            if failure is None: failure = error
        try:
            reference.require_owned(directory, api)
            removed = subprocess.run(['docker', 'image', 'rm', image], capture_output=True, text=True, timeout=30)
            assert removed.returncode == 0, 'Invalidation image cleanup failed'
        except BaseException as error:
            if failure is None: failure = error
        try: unchanged()
        except BaseException as error:
            if failure is None: failure = error
    if failure is not None: raise failure
    print(SUMMARY, flush=True)
