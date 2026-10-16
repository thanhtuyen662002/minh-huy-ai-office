"""Owned original-plan SQL proof; empty history and no provider/model evaluation."""
import json
import os
import re
import subprocess
import uuid

PREPARE_LINES = [
    "PASS owned automatic note actual inbox and allocation exact two media references empty effects and claims",
    "PASS owned original plan actual two source inbox allocation no claims plans effects or model"
]
RUNTIME_LINES = {
    "original-plan-commit": "PASS owned original plan actual SQL committed reply interrupted fresh context exact original allocation plan chunks replay one immutable row source locks no extra keys effects provider or model",
    "original-plan-resume": "PASS owned original plan separate process replacement original claim epoch one current epoch two exact persisted chunks concurrent replay changed profile refused SQL constraints immutable rights no effects provider or model",
    "original-plan-expiry": "PASS owned original plan SQL flushed insert source lock savepoint rollback clean detach witness only durable clock rewind refused zero plans effects provider or model"
}
SUMMARY = "PASS actual original plan empty history SQL commit interrupted reply fresh context separate process replacement exact stored allocation input chunks concurrent replay immutable physical boundaries expiry flushed rollback witness only unchanged retained scopes no provider tokenizer automatic DI or model"


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
        assert type(value) is str and re.fullmatch(r"[0-9A-F]{64}", value), "Owned original plan snapshot refused"
        return value

    def retained():
        return automatic.retained_snapshot(tenant=tenant, company=company, sources=sources, accounts=accounts, sql=sql) + tuple(
            hash_rows(table, order, base+f" AND BindingId IN ({source_list})") for table, order in (
                ("GroupAutomaticInputPlans", "BatchId"), ("GroupModelAttemptStarts", "RequestId"),
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
    seen_sources = set(sources)
    seen_accounts = set(accounts)
    def full_graph(predicate):
        return tuple(hash_rows(table, order, predicate) for table, order in automatic.SOURCE_TABLES) + tuple(
            hash_rows(table, order, predicate) for table, order in (
                ("GroupAutomaticInputPlans", "BatchId"), ("GroupModelAttemptStarts", "RequestId"),
                ("GroupModelAttemptFailures", "RequestId"), ("GroupWorkRawDispositions", "BatchId,CommittedSequence")))
    def registry(source, account):
        return (
            hash_rows("GroupBindings", "Id", base+f" AND Id='{source}'"),
            hash_rows("GroupConnectorAccounts", "Id", base+f" AND Id='{account}'"),
            hash_rows("GroupListenerLeases", "ConnectorAccountId", base+f" AND ConnectorAccountId='{account}'"))
    def require_completed():
        if completed_graph is not None:
            assert full_graph(completed_scope) == completed_graph, "Expiry fixture changed completed original plan scope"
            assert registry(completed_source, completed_account) == completed_registry, "Expiry fixture changed completed original registry"

    try:
        built = subprocess.run(["docker", "build", "-f", "tests/GroupReference.RuntimeProof/Dockerfile", "-t", image, "."],
            capture_output=True, text=True, timeout=300)
        assert built.returncode == 0, "Owned original plan executable build failed"
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
            assert retained() == retained_before, "Original plan preparation changed retained scopes"

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
            modes = ["original-plan-prepare", "original-plan-commit", "original-plan-resume"] if fixture_index == 0 else [
                "original-plan-prepare", "original-plan-expiry"]
            allocation_graph = None
            original_plan = None
            for index, mode in enumerate(modes):
                result = subprocess.run([*command, mode], input=configuration, env=environment, capture_output=True, text=True, timeout=170)
                assert not result.stderr, "Owned original plan executable emitted unexpected diagnostics"
                reference.require_reference_result(result, mode, PREPARE_LINES if index == 0 else [RUNTIME_LINES[mode]])
                assert retained() == retained_before, "Original plan changed retained scopes"
                assert originals() == original_before, "Original plan changed protected source or registry"
                graph = tuple(hash_rows(table, order, scope) for table, order in automatic.SOURCE_TABLES[6:9])
                if allocation_graph is None: allocation_graph = graph
                assert graph == allocation_graph, "Original plan changed original allocation graph"
                assert sql(f"SELECT COUNT(*) FROM aioffice.GroupSourceStates WHERE {scope} AND CommittedSequence=2 AND ScheduledThroughSequence=2"
                    " AND FirstPendingAtUtc IS NULL AND LastPendingAtUtc IS NULL;") == "1"
                assert all(sql(f"SELECT COUNT(*) FROM aioffice.{table} WHERE {scope};") == "0" for table in
                    automatic.EFFECT_TABLES+("GroupWorkRawDispositions", "GroupCoverageGaps", "GroupModelAttemptStarts", "GroupModelAttemptFailures"))
                expected = ("0|0", "1|1", "1|2")[index] if fixture_index == 0 else ("0|0", "0|1")[index]
                assert sql(f"SELECT CONCAT((SELECT COUNT(*) FROM aioffice.GroupAutomaticInputPlans WHERE {scope}),N'|',"
                    f"(SELECT COUNT(*) FROM aioffice.GroupBatchClaimReceipts WHERE {scope}));") == expected
                if mode in ("original-plan-commit", "original-plan-resume"):
                    assert sql(f"SELECT COUNT(*) FROM aioffice.GroupAutomaticInputPlans WHERE {scope} AND FormatVersion=2"
                        " AND OriginalClaimEpoch=1 AND DATALENGTH([Plan]) BETWEEN 256 AND 32000"
                        " AND PlanSha256=CONVERT(varchar(64),HASHBYTES('SHA2_256',[Plan]),2);") == "1"
                    plan = hash_rows("GroupAutomaticInputPlans", "BatchId", scope)
                    if original_plan is None: original_plan = plan
                    assert plan == original_plan, "Original plan was overwritten on replacement"
                if index:
                    witness = "=ExpiresAtUtc" if mode == "original-plan-expiry" else "IS NULL"
                    epoch = 1 if fixture_index else index
                    assert sql(f"SELECT COUNT(*) FROM aioffice.GroupBatchClaimStates WHERE {scope} AND Epoch={epoch} AND ExpiryObservedAtUtc {witness};") == "1"
                require_completed()
            if fixture_index == 0:
                completed_scope = scope
                completed_graph = full_graph(scope)
                completed_source = source; completed_account = account
                completed_registry = registry(source, account)
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
            if removed.returncode != 0 and failure is None: failure = AssertionError("Owned original plan image cleanup failed")
        except BaseException as error:
            if failure is None: failure = error
        try: require_completed()
        except BaseException as error:
            if failure is None: failure = error
    if failure is not None: raise failure
    print(SUMMARY, flush=True)
