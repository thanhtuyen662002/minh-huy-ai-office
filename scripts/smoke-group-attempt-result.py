"""Owned encrypted attempt-result SQL proof; empty history and no provider/model evaluation."""
import json
import os
import re
import subprocess
import uuid

PREPARE_LINES = [
    "PASS owned automatic note actual inbox and allocation exact two media references empty effects and claims",
    "PASS owned attempt result source allocation no claims plans starts results effects provider or model"
]
RUNTIME_LINES = {
    "attempt-result-commit": "PASS owned attempt result actual original SQL start encrypted accepted output committed reply interrupted fresh context byte exact replay one result zero failures no notes provider or model",
    "attempt-result-resume": "PASS owned attempt result separate process current replacement claim original plan start output retained four fresh concurrent reads retry failure suppressed no overwrite notes provider or model",
    "attempt-result-deadline": "PASS owned attempt result actual SQL flushed insert at original deadline transaction rollback detached zero results fresh expired source read durable witness clock rewind refused no notes provider or model"
}
SUMMARY = 'PASS actual attempt result empty history original SQL plan start encrypted accepted output interrupted commit reply fresh context separate process replacement four concurrent reads retry failure suppression deadline flushed rollback separate source expiry witness retained complete scopes no provider tokenizer automatic DI or model'


def verify(*, directory, api, manifest, tenant, company, service, sql, prepare_source, reference, automatic):
    reference.require_owned(directory, api)
    assert callable(sql) and callable(prepare_source)
    tenant, company, service = (automatic.canonical(value) for value in (tenant, company, service))
    installation = automatic.canonical(manifest["AIOFFICE_INSTALLATION_ID"])
    password = manifest["AIOFFICE_RUNTIME_PASSWORD"]
    assert type(password) is str and re.fullmatch(r"[A-Za-z0-9_-]{32,128}", password)
    base = f"TenantId='{tenant}' AND CompanyId='{company}'"
    sources = tuple(automatic.canonical(value) for value in sql(
        f"SELECT LOWER(CONVERT(char(36),Id)) FROM aioffice.GroupBindings WHERE {base} ORDER BY Id;").splitlines())
    accounts = tuple(automatic.canonical(value) for value in sql(
        f"SELECT LOWER(CONVERT(char(36),Id)) FROM aioffice.GroupConnectorAccounts WHERE {base} ORDER BY Id;").splitlines())
    assert sources and accounts and len(set(sources)) == len(sources) and len(set(accounts)) == len(accounts)
    source_list = ','.join("'"+value+"'" for value in sources)

    def hash_rows(table, order, predicate, columns="*"):
        value = sql("SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),COALESCE("
            f"(SELECT {columns} FROM aioffice.{table} WHERE {predicate} ORDER BY {order} FOR JSON PATH,INCLUDE_NULL_VALUES),N'[]'))),2);")
        assert type(value) is str and re.fullmatch(r"[0-9A-F]{64}", value), "Owned attempt result snapshot refused"
        return value

    def retained():
        return automatic.retained_snapshot(tenant=tenant, company=company, sources=sources, accounts=accounts, sql=sql) + tuple(
            hash_rows(table, order, base+f" AND BindingId IN ({source_list})") for table, order in (
                ("GroupModelAttemptResults", "RequestId"), ("GroupAutomaticInputPlans", "BatchId"), ("GroupModelAttemptStarts", "RequestId"),
                ("GroupModelAttemptFailures", "RequestId"), ("GroupWorkRawDispositions", "BatchId,CommittedSequence")))

    retained_before = retained()
    suffix = uuid.uuid4().hex
    image = "aioffice-automatic-note-proof-"+suffix
    command = ["docker", "run", "--init", "--rm", "--name", image, "--label", "aioffice.owned-proof="+suffix,
        "--network", "aioffice-"+uuid.UUID(installation).hex+"_default", "--read-only", "--cap-drop", "ALL",
        "--security-opt", "no-new-privileges:true", "--tmpfs", "/tmp:rw,noexec,nosuid,size=16m", "-i",
        "-e", "CI", "-e", "GITHUB_ACTIONS", "-e", "AIOFFICE_OWNED_GROUP_REFERENCE_PROOF",
        "-e", "AIOFFICE_GROUP_REFERENCE_PROOF_CONNECTION", "-e", "AIOFFICE_GROUP_REFERENCE_PROOF_SOURCE_KEY", image]
    failure = None
    completed_graph = None
    completed_scope = None
    completed_source = completed_account = completed_registry = None
    deadline_graph = deadline_scope = deadline_registry = None
    deadline_source = deadline_account = None
    seen_sources = set(sources)
    seen_accounts = set(accounts)
    def full_graph(predicate):
        return tuple(hash_rows(table, order, predicate) for table, order in automatic.SOURCE_TABLES) + tuple(
            hash_rows(table, order, predicate) for table, order in (
                ("GroupModelAttemptResults", "RequestId"), ("GroupAutomaticInputPlans", "BatchId"), ("GroupModelAttemptStarts", "RequestId"),
                ("GroupModelAttemptFailures", "RequestId"), ("GroupWorkRawDispositions", "BatchId,CommittedSequence")))
    def registry(source, account):
        return (
            hash_rows("GroupBindings", "Id", base+f" AND Id='{source}'"),
            hash_rows("GroupConnectorAccounts", "Id", base+f" AND Id='{account}'"),
            hash_rows("GroupListenerLeases", "ConnectorAccountId", base+f" AND ConnectorAccountId='{account}'"))
    def require_completed():
        assert retained() == retained_before, "Attempt result cleanup changed retained scopes"
        if deadline_graph is not None:
            assert full_graph(deadline_scope) == deadline_graph, "Cleanup changed completed deadline result scope"
            assert registry(deadline_source, deadline_account) == deadline_registry, "Cleanup changed completed deadline registry"
        if completed_graph is not None:
            assert full_graph(completed_scope) == completed_graph, "Expiry fixture changed completed attempt result scope"
            assert registry(completed_source, completed_account) == completed_registry, "Expiry fixture changed completed original registry"

    try:
        built = subprocess.run(["docker", "build", "-f", "tests/GroupReference.RuntimeProof/Dockerfile", "-t", image, "."],
            capture_output=True, text=True, timeout=300)
        assert built.returncode == 0, "Owned attempt result executable build failed"
        for fixture_index in range(2):
            source, events, key = prepare_source()
            require_completed()
            source = automatic.canonical(source)
            events = tuple(automatic.canonical(value) for value in events)
            reference.require_source_key(key)
            assert source not in seen_sources and len(events) == len(set(events)) == 2
            seen_sources.add(source)
            account = automatic.canonical(sql(f"SELECT LOWER(CONVERT(char(36),ConnectorAccountId)) FROM aioffice.GroupBindings WHERE {base} AND Id='{source}';"))
            assert account not in seen_accounts
            seen_accounts.add(account)
            scope = base+f" AND BindingId='{source}'"
            assert retained() == retained_before, "Attempt result preparation changed retained scopes"

            def originals():
                return tuple(hash_rows(table, order, scope) for table, order in automatic.SOURCE_TABLES[:6]
                    if table != "GroupSourceStates") + (
                    hash_rows("GroupSourceStates", "BindingId", scope, "TenantId,CompanyId,BindingId,CommittedSequence"),
                    hash_rows("GroupServiceGrants", "ServiceId,Capability", scope),
                    hash_rows("GroupBindings", "Id", base+f" AND Id='{source}'"),
                    hash_rows("GroupConnectorAccounts", "Id", base+f" AND Id='{account}'"),
                    hash_rows("GroupListenerLeases", "ConnectorAccountId", base+f" AND ConnectorAccountId='{account}'"))

            original_before = originals()
            environment = dict(os.environ, AIOFFICE_OWNED_GROUP_REFERENCE_PROOF="true",
                AIOFFICE_GROUP_REFERENCE_PROOF_CONNECTION="Server=sql;Database=AIOfficeLocal;User ID=aioffice_runtime;Password="
                    +password+";Encrypt=true;TrustServerCertificate=true", AIOFFICE_GROUP_REFERENCE_PROOF_SOURCE_KEY=key)
            configuration = json.dumps(dict(tenantId=tenant, companyId=company, serviceId=service, sourceId=source, eventId=events[0]))
            modes = ["attempt-result-prepare", "attempt-result-commit", "attempt-result-resume"] if fixture_index == 0 else [
                "attempt-result-prepare", "attempt-result-deadline"]
            allocation_graph = None
            original_plan = None
            original_result = None
            original_start = None
            for index, mode in enumerate(modes):
                result = subprocess.run([*command, mode], input=configuration, env=environment, capture_output=True, text=True, timeout=170)
                assert not result.stderr, "Owned attempt result executable emitted unexpected diagnostics"
                reference.require_reference_result(result, mode, PREPARE_LINES if index == 0 else [RUNTIME_LINES[mode]])
                assert retained() == retained_before, "Attempt result changed retained scopes"
                assert originals() == original_before, "Attempt result changed protected source or registry"
                graph = tuple(hash_rows(table, order, scope) for table, order in automatic.SOURCE_TABLES[6:9])
                if allocation_graph is None: allocation_graph = graph
                assert graph == allocation_graph, "Attempt result changed original allocation graph"
                assert sql(f"SELECT COUNT(*) FROM aioffice.GroupSourceStates WHERE {scope} AND CommittedSequence=2 AND ScheduledThroughSequence=2"
                    " AND FirstPendingAtUtc IS NULL AND LastPendingAtUtc IS NULL;") == "1"
                assert all(sql(f"SELECT COUNT(*) FROM aioffice.{table} WHERE {scope};") == "0" for table in
                    automatic.EFFECT_TABLES+("GroupWorkRawDispositions", "GroupCoverageGaps", "GroupModelAttemptFailures"))
                expected = ("0|0|0|0", "1|1|1|1", "1|1|1|2")[index] if fixture_index == 0 else ("0|0|0|0", "1|1|0|1")[index]
                assert sql(f"SELECT CONCAT((SELECT COUNT(*) FROM aioffice.GroupAutomaticInputPlans WHERE {scope}),N'|',"
                    f"(SELECT COUNT(*) FROM aioffice.GroupModelAttemptStarts WHERE {scope}),N'|',"
                    f"(SELECT COUNT(*) FROM aioffice.GroupModelAttemptResults WHERE {scope}),N'|',"
                    f"(SELECT COUNT(*) FROM aioffice.GroupBatchClaimReceipts WHERE {scope}));") == expected
                if mode in ("attempt-result-commit", "attempt-result-resume"):
                    assert sql(f"SELECT COUNT(*) FROM aioffice.GroupAutomaticInputPlans WHERE {scope} AND FormatVersion=2"
                        " AND OriginalClaimEpoch=1 AND DATALENGTH([Plan]) BETWEEN 256 AND 32000"
                        " AND PlanSha256=CONVERT(varchar(64),HASHBYTES('SHA2_256',[Plan]),2);") == "1"
                    plan = hash_rows("GroupAutomaticInputPlans", "BatchId", scope)
                    if original_plan is None: original_plan = plan
                    assert plan == original_plan, "Attempt result was overwritten on replacement"
                if mode in ("attempt-result-commit", "attempt-result-resume"):
                    assert sql(f"SELECT COUNT(*) FROM aioffice.GroupModelAttemptResults WHERE {scope} AND FormatVersion=1"
                        " AND DATALENGTH(ProtectedResult) BETWEEN 229 AND 66029 AND SUBSTRING(ProtectedResult,1,1)=0x01"
                        " AND ProtectedResultSha256=CONVERT(varchar(64),HASHBYTES('SHA2_256',ProtectedResult),2);") == "1"
                    accepted = hash_rows("GroupModelAttemptResults", "RequestId", scope)
                    started = hash_rows("GroupModelAttemptStarts", "RequestId", scope)
                    if original_result is None: original_result = accepted; original_start = started
                    assert accepted == original_result and started == original_start, "Original accepted output or start was overwritten"
                if index:
                    witness = "=ExpiresAtUtc" if mode == "attempt-result-deadline" else "IS NULL"
                    epoch = 1 if fixture_index else index
                    assert sql(f"SELECT COUNT(*) FROM aioffice.GroupBatchClaimStates WHERE {scope} AND Epoch={epoch} AND ExpiryObservedAtUtc {witness};") == "1"
                require_completed()
            if fixture_index == 0:
                completed_scope = scope
                completed_graph = full_graph(scope)
                completed_source = source; completed_account = account
                completed_registry = registry(source, account)
            else:
                deadline_scope = scope
                deadline_graph = full_graph(scope)
                deadline_source = source; deadline_account = account
                deadline_registry = registry(source, account)
    except BaseException as error:
        failure = error
    finally:
        try: automatic.close_owned_container(directory=directory, api=api, name=image, suffix=suffix, reference=reference)
        except BaseException as error:
            if failure is None: failure = error
        try: require_completed()
        except BaseException as error:
            if failure is None: failure = error
        try:
            removed = subprocess.run(["docker", "image", "rm", image], capture_output=True, text=True, timeout=30)
            if removed.returncode != 0 and failure is None: failure = AssertionError("Owned attempt result image cleanup failed")
        except BaseException as error:
            if failure is None: failure = error
        try: require_completed()
        except BaseException as error:
            if failure is None: failure = error
    if failure is not None: raise failure
    print(SUMMARY, flush=True)
