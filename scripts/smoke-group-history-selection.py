"""Owned current history selection and original nonempty recovery; no model evaluation."""
import json
import os
import re
import subprocess
import uuid

RUNTIME_LINES = {
    'history-selection-prepare': 'PASS owned history selection shipping four protected notes same current batch excluded actual serializable scoped metadata queries no provider or model',
    'history-selection-commit': 'PASS owned history selection actual next Core source code reply recent four prior notes bounded scoped SQL original nonempty history plan encrypted accepted result no provider or model',
    'history-selection-resume': 'PASS owned history selection separate process replacement claim persisted four original history identities and order current source brain protected accepted result byte exact no new selection start or provider'
}
SUMMARY = 'PASS actual history selection SQL four shipping protected prior notes next actual Core code reply recent bounded scoped metadata serializable source lock same batch exclusion original nonempty plan accepted result separate process replacement no reselection second start or provider retained complete scopes after both cleanup gates no model qualification'


def verify(*, directory, api, manifest, tenant, company, service, sql, prepare_source, append_followup, reference, automatic):
    reference.require_owned(directory, api)
    assert all(callable(value) for value in (sql, prepare_source, append_followup))
    tenant, company, service = (automatic.canonical(value) for value in (tenant, company, service))
    installation = automatic.canonical(manifest['AIOFFICE_INSTALLATION_ID'])
    password = manifest['AIOFFICE_RUNTIME_PASSWORD']
    assert type(password) is str and re.fullmatch(r'[A-Za-z0-9_-]{32,128}', password)
    base = f"TenantId='{tenant}' AND CompanyId='{company}'"
    sources = tuple(automatic.canonical(value) for value in sql(f'SELECT LOWER(CONVERT(char(36),Id)) FROM aioffice.GroupBindings WHERE {base} ORDER BY Id;').splitlines())
    accounts = tuple(automatic.canonical(value) for value in sql(f'SELECT LOWER(CONVERT(char(36),Id)) FROM aioffice.GroupConnectorAccounts WHERE {base} ORDER BY Id;').splitlines())
    assert sources and accounts and len(set(sources)) == len(sources) and len(set(accounts)) == len(accounts)
    source_list = ','.join("'"+value+"'" for value in sources)
    extra = (('GroupAutomaticInputPlans','BatchId'), ('GroupModelAttemptStarts','RequestId'),
        ('GroupModelAttemptResults','RequestId'), ('GroupModelAttemptFailures','RequestId'), ('GroupWorkRawDispositions','BatchId,CommittedSequence'))

    def digest(table, order, predicate):
        value = sql("SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),COALESCE("
            f"(SELECT * FROM aioffice.{table} WHERE {predicate} ORDER BY {order} FOR JSON PATH,INCLUDE_NULL_VALUES),N'[]'))),2);")
        assert type(value) is str and re.fullmatch(r'[0-9A-F]{64}', value), 'Owned history snapshot refused'
        return value

    def retained():
        return automatic.retained_snapshot(tenant=tenant, company=company, sources=sources, accounts=accounts, sql=sql) + tuple(
            digest(table, order, base+f' AND BindingId IN ({source_list})') for table, order in extra)

    before = retained()
    source = account = scope = first_batch = original_batch = None
    completed = original_notes = original_input = original_result = original_start = registry = original_prefix = original_authority = None
    suffix = uuid.uuid4().hex; image = 'aioffice-automatic-note-proof-'+suffix
    command = ['docker','run','--init','--rm','--name',image,'--label','aioffice.owned-proof='+suffix,
        '--network','aioffice-'+uuid.UUID(installation).hex+'_default','--read-only','--cap-drop','ALL',
        '--security-opt','no-new-privileges:true','--tmpfs','/tmp:rw,noexec,nosuid,size=16m','-i',
        '-e','CI','-e','GITHUB_ACTIONS','-e','AIOFFICE_OWNED_GROUP_REFERENCE_PROOF',
        '-e','AIOFFICE_GROUP_REFERENCE_PROOF_CONNECTION','-e','AIOFFICE_GROUP_REFERENCE_PROOF_SOURCE_KEY',image]

    def registry_graph():
        return tuple(digest(table, order, predicate) for table, order, predicate in (
            ('GroupBindings','Id',base+f" AND Id='{source}'"),
            ('GroupConnectorAccounts','Id',base+f" AND Id='{account}'"),
            ('GroupListenerLeases','ConnectorAccountId',base+f" AND ConnectorAccountId='{account}'")))

    def full_graph():
        return tuple(digest(table, order, scope) for table, order in automatic.SOURCE_TABLES+extra) + registry_graph()

    def notes_graph():
        return tuple(digest(table, order, scope) for table, order in automatic.SOURCE_TABLES[11:20])+(
            digest('GroupWorkRawDispositions','BatchId,CommittedSequence',scope),)

    def source_prefix():
        selected = f'SELECT MessageId FROM aioffice.GroupIngressOutbox WHERE {scope} AND CommittedSequence<=2'
        return (digest('GroupMessages','Id',scope+f' AND Id IN ({selected})'),)+tuple(
            digest(table,order,scope+f' AND MessageId IN ({selected})') for table,order in automatic.SOURCE_TABLES[:6]
            if table not in ('GroupMessages','GroupSourceStates','GroupCoverageGaps'))

    def authority_graph():
        return tuple(digest(table,order,scope) for table,order in (automatic.SOURCE_TABLES[4],)+automatic.SOURCE_TABLES[20:23])

    def first_batch_graph():
        # Freeze original rows separately from the growing second batch.
        # All receipt epochs are included; the original inbox prefix is fixed.
        assert first_batch is not None
        return tuple(digest(table, order, scope+predicate) for table, order, predicate in (
            ('GroupBatchAllocations','Id',f" AND Id='{first_batch}'"),
            ('GroupBatchAllocatedRevisions','CommittedSequence',f" AND BatchId='{first_batch}'"),
            ('GroupBatchClaimStates','BatchId',f" AND BatchId='{first_batch}'"),
            ('GroupBatchClaimReceipts','BatchId,Epoch',f" AND BatchId='{first_batch}'"),
            ('GroupIngressInbox','CommittedSequence',' AND CommittedSequence<=2')))

    def require_original_batch():
        if original_batch is not None:
            assert first_batch_graph() == original_batch, 'History changed original first batch ledger or inbox prefix'

    def require_completed():
        require_original_batch()
        assert retained() == before, 'History proof changed retained scopes'
        if completed is not None:
            assert full_graph() == completed, 'History cleanup changed completed scoped graph'

    failure = None
    try:
        built = subprocess.run(['docker','build','-f','tests/GroupReference.RuntimeProof/Dockerfile','-t',image,'.'],capture_output=True,text=True,timeout=300)
        assert built.returncode == 0, 'Owned history executable build failed'
        source, events, key = prepare_source()
        source = automatic.canonical(source); events = tuple(automatic.canonical(value) for value in events)
        reference.require_source_key(key)
        assert source not in sources and len(events) == len(set(events)) == 2
        account = automatic.canonical(sql(f"SELECT LOWER(CONVERT(char(36),ConnectorAccountId)) FROM aioffice.GroupBindings WHERE {base} AND Id='{source}';"))
        assert account not in accounts
        scope = base+f" AND BindingId='{source}'"
        require_completed()
        environment = dict(os.environ,AIOFFICE_OWNED_GROUP_REFERENCE_PROOF='true',
            AIOFFICE_GROUP_REFERENCE_PROOF_CONNECTION='Server=sql;Database=AIOfficeLocal;User ID=aioffice_runtime;Password='+password+';Encrypt=true;TrustServerCertificate=true',
            AIOFFICE_GROUP_REFERENCE_PROOF_SOURCE_KEY=key)
        event_id = events[0]
        for index, (mode, expected) in enumerate(RUNTIME_LINES.items()):
            if index == 1:
                # The callback posts a new actual Core event only after the
                # first process committed its shipping note graph. It must
                # retain those notes, original sources and other scopes.
                event_id = automatic.canonical(append_followup(source))
                require_original_batch()
                assert event_id not in events and notes_graph() == original_notes and source_prefix() == original_prefix
                assert authority_graph() == original_authority and registry_graph()[:2] == registry[:2], 'History followup changed source authority'
                assert retained() == before, 'History followup changed retained scopes'
                registry = registry_graph()
            configuration = json.dumps(dict(tenantId=tenant,companyId=company,serviceId=service,sourceId=source,eventId=event_id))
            result = subprocess.run([*command,mode],input=configuration,env=environment,capture_output=True,text=True,timeout=170)
            assert not result.stderr, 'Owned history executable emitted unexpected diagnostics'
            reference.require_reference_result(result,mode,[expected])
            require_original_batch()
            assert retained() == before, 'History child changed retained scopes'
            assert sql(f"SELECT CONCAT((SELECT COUNT(*) FROM aioffice.GroupCustomerRequests WHERE {scope}),N'|',"
                f"(SELECT COUNT(*) FROM aioffice.GroupWorkCommitReceipts WHERE {scope}),N'|',"
                f"(SELECT COUNT(*) FROM aioffice.GroupAutomaticInputPlans WHERE {scope}),N'|',"
                f"(SELECT COUNT(*) FROM aioffice.GroupModelAttemptStarts WHERE {scope}),N'|',"
                f"(SELECT COUNT(*) FROM aioffice.GroupModelAttemptResults WHERE {scope}),N'|',"
                f"(SELECT COUNT(*) FROM aioffice.GroupBatchAllocations WHERE {scope}),N'|',"
                f"(SELECT COUNT(*) FROM aioffice.GroupBatchClaimReceipts WHERE {scope}));") == ('4|1|0|0|0|1|1','4|1|1|1|1|2|2','4|1|1|1|1|2|3')[index]
            assert sql(f"SELECT COUNT(*) FROM aioffice.GroupSourceStates WHERE {scope} AND CommittedSequence={2 if index == 0 else 3}"
                f" AND ScheduledThroughSequence={2 if index == 0 else 3} AND FirstPendingAtUtc IS NULL AND LastPendingAtUtc IS NULL;") == '1'
            assert sql(f"SELECT COUNT(*) FROM aioffice.GroupModelAttemptFailures WHERE {scope};") == '0'
            if index == 0:
                first_batch = automatic.canonical(sql(f"SELECT LOWER(CONVERT(char(36),Id)) FROM aioffice.GroupBatchAllocations WHERE {scope} AND OperationId='{events[0]}';"))
                original_batch = first_batch_graph()
                original_notes = notes_graph(); registry = registry_graph()
                original_prefix = source_prefix(); original_authority = authority_graph()
            else:
                assert notes_graph() == original_notes and registry_graph() == registry, 'History changed original notes or registry'
                assert source_prefix() == original_prefix and authority_graph() == original_authority, 'History changed original source prefix or authority'
                assert sql(f"SELECT COUNT(*) FROM aioffice.GroupAutomaticInputPlans WHERE {scope} AND FormatVersion=2 AND OriginalClaimEpoch=1"
                    " AND DATALENGTH([Plan]) BETWEEN 256 AND 32000 AND PlanSha256=CONVERT(varchar(64),HASHBYTES('SHA2_256',[Plan]),2);") == '1'
                assert sql(f"SELECT COUNT(*) FROM aioffice.GroupModelAttemptResults WHERE {scope} AND FormatVersion=1"
                    " AND DATALENGTH(ProtectedResult) BETWEEN 229 AND 66029 AND SUBSTRING(ProtectedResult,1,1)=0x01"
                    " AND ProtectedResultSha256=CONVERT(varchar(64),HASHBYTES('SHA2_256',ProtectedResult),2);") == '1'
                plan, started, accepted = (digest(table, order, scope) for table, order in extra[:3])
                if index == 1: original_input, original_start, original_result = plan, started, accepted
                assert (plan, started, accepted) == (original_input, original_start, original_result), 'History recovery replaced original plan start or output'
            require_original_batch()
            completed = full_graph()
    except BaseException as error:
        failure = error
    finally:
        try: automatic.close_owned_container(directory=directory,api=api,name=image,suffix=suffix,reference=reference)
        except BaseException as error:
            if failure is None: failure = error
        try: require_completed()
        except BaseException as error:
            if failure is None: failure = error
        try:
            removed = subprocess.run(['docker','image','rm',image],capture_output=True,text=True,timeout=30)
            if removed.returncode != 0 and failure is None: failure = AssertionError('Owned history image cleanup failed')
        except BaseException as error:
            if failure is None: failure = error
        try: require_completed()
        except BaseException as error:
            if failure is None: failure = error
    if failure is not None: raise failure
    print(SUMMARY,flush=True)
