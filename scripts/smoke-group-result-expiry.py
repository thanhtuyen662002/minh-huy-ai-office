"""Separate owned first Shared expiry witness; synthetic result, no model evaluation."""
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
    "attempt-result-shared-expiry": "PASS owned attempt result first expiry actual SQL original epoch replay NULL witness Shared claim observation disposed read session fresh Exclusive one witness original timestamp detached commit clock rewind denied no result key effects provider or model"
}
SUMMARY = "PASS actual first Shared result expiry SQL fresh accepted epoch1 NULL witness exact original replay actual Shared disposed session fresh Exclusive one original timestamp witness detached commit clock rewind denied unchanged keys complete old and new graphs both cleanup gates no provider or model"


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
    source_list = ','.join("'" + value + "'" for value in sources)
    result_tables = (("GroupModelAttemptResults", "RequestId"), ("GroupAutomaticInputPlans", "BatchId"),
        ("GroupModelAttemptStarts", "RequestId"), ("GroupModelAttemptFailures", "RequestId"),
        ("GroupWorkRawDispositions", "BatchId,CommittedSequence"))

    def digest(table, order, predicate, columns="*"):
        value = sql("SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),COALESCE("
            f"(SELECT {columns} FROM aioffice.{table} WHERE {predicate} ORDER BY {order} FOR JSON PATH,INCLUDE_NULL_VALUES),N'[]'))),2);")
        assert type(value) is str and re.fullmatch(r"[0-9A-F]{64}", value), "Owned expiry snapshot refused"
        return value

    def retained():
        return automatic.retained_snapshot(tenant=tenant, company=company, sources=sources, accounts=accounts, sql=sql) + tuple(
            digest(table, order, base + f" AND BindingId IN ({source_list})") for table, order in result_tables)

    # Freeze ALL prior scopes, including both old result fixtures and registries,
    # before creating the new scope. Never rebaseline them after a child.
    retained_before = retained()
    suffix = uuid.uuid4().hex
    image = "aioffice-automatic-note-proof-" + suffix
    command = ["docker", "run", "--init", "--rm", "--name", image, "--label", "aioffice.owned-proof=" + suffix,
        "--network", "aioffice-" + uuid.UUID(installation).hex + "_default", "--read-only", "--cap-drop", "ALL",
        "--security-opt", "no-new-privileges:true", "--tmpfs", "/tmp:rw,noexec,nosuid,size=16m", "-i",
        "-e", "CI", "-e", "GITHUB_ACTIONS", "-e", "AIOFFICE_OWNED_GROUP_REFERENCE_PROOF",
        "-e", "AIOFFICE_GROUP_REFERENCE_PROOF_CONNECTION", "-e", "AIOFFICE_GROUP_REFERENCE_PROOF_SOURCE_KEY", image]
    failure = None
    completed = None
    current_graph = None

    def require_retained():
        assert retained() == retained_before, "First expiry changed retained scopes"
        if completed is not None:
            assert current_graph() == completed, "Cleanup changed completed first expiry graph"

    try:
        built = subprocess.run(["docker", "build", "-f", "tests/GroupReference.RuntimeProof/Dockerfile", "-t", image, "."],
            capture_output=True, text=True, timeout=300)
        assert built.returncode == 0, "Owned expiry executable build failed"
        source, events, key = prepare_source()
        require_retained()
        source = automatic.canonical(source)
        events = tuple(automatic.canonical(value) for value in events)
        reference.require_source_key(key)
        assert source not in sources and len(events) == len(set(events)) == 2
        account = automatic.canonical(sql(f"SELECT LOWER(CONVERT(char(36),ConnectorAccountId)) FROM aioffice.GroupBindings WHERE {base} AND Id='{source}';"))
        assert account not in accounts
        scope = base + f" AND BindingId='{source}'"

        def registry():
            return tuple(digest(table, order, base + f" AND {column}='{identity}'") for table, order, column, identity in (
                ("GroupBindings", "Id", "Id", source), ("GroupConnectorAccounts", "Id", "Id", account),
                ("GroupListenerLeases", "ConnectorAccountId", "ConnectorAccountId", account),
                ("GroupAccountCoverageGaps", "ConnectorAccountId,ListenerEpoch,Reason", "ConnectorAccountId", account),
                ("GroupListenerCommandReceipts", "ConnectorAccountId,ServiceId,CredentialEpoch,Nonce", "ConnectorAccountId", account)))

        def graph(with_witness=True):
            columns = "*" if with_witness else "TenantId,CompanyId,BindingId,BatchId,Epoch,OwnerId,OperationId,IssuedAtUtc,ExpiresAtUtc"
            return tuple(digest(table, order, scope, columns if table == "GroupBatchClaimStates" else "*")
                for table, order in automatic.SOURCE_TABLES + result_tables) + registry()

        current_graph = graph
        originals = tuple(digest(table, order, scope) for table, order in automatic.SOURCE_TABLES[:6] if table != "GroupSourceStates") + (
            digest("GroupSourceStates", "BindingId", scope, "TenantId,CompanyId,BindingId,CommittedSequence"),
            digest("GroupServiceGrants", "ServiceId,Capability", scope)) + registry()

        def require_originals():
            assert tuple(digest(table, order, scope) for table, order in automatic.SOURCE_TABLES[:6] if table != "GroupSourceStates") + (
                digest("GroupSourceStates", "BindingId", scope, "TenantId,CompanyId,BindingId,CommittedSequence"),
                digest("GroupServiceGrants", "ServiceId,Capability", scope)) + registry() == originals, "First expiry changed protected source or registry"

        environment = dict(os.environ, AIOFFICE_OWNED_GROUP_REFERENCE_PROOF="true",
            AIOFFICE_GROUP_REFERENCE_PROOF_CONNECTION="Server=sql;Database=AIOfficeLocal;User ID=aioffice_runtime;Password="
                + password + ";Encrypt=true;TrustServerCertificate=true", AIOFFICE_GROUP_REFERENCE_PROOF_SOURCE_KEY=key)
        configuration = json.dumps(dict(tenantId=tenant, companyId=company, serviceId=service, sourceId=source, eventId=events[0]))
        allocation_graph = accepted_graph = None
        for index, mode in enumerate(("attempt-result-prepare", "attempt-result-commit", "attempt-result-shared-expiry")):
            child = subprocess.run([*command, mode], input=configuration, env=environment, capture_output=True, text=True, timeout=170)
            assert not child.stderr, "Owned expiry executable emitted unexpected diagnostics"
            reference.require_reference_result(child, mode, PREPARE_LINES if index == 0 else [RUNTIME_LINES[mode]])
            require_retained(); require_originals()
            allocated = tuple(digest(table, order, scope) for table, order in automatic.SOURCE_TABLES[6:9])
            if allocation_graph is None: allocation_graph = allocated
            assert allocated == allocation_graph, "First expiry changed original allocation graph"
            assert sql(f"SELECT COUNT(*) FROM aioffice.GroupSourceStates WHERE {scope} AND CommittedSequence=2 AND ScheduledThroughSequence=2"
                " AND FirstPendingAtUtc IS NULL AND LastPendingAtUtc IS NULL;") == "1"
            assert all(sql(f"SELECT COUNT(*) FROM aioffice.{table} WHERE {scope};") == "0" for table in
                automatic.EFFECT_TABLES + ("GroupWorkRawDispositions", "GroupCoverageGaps", "GroupModelAttemptFailures"))
            expected = "0|0|0|0" if index == 0 else "1|1|1|1"
            assert sql(f"SELECT CONCAT((SELECT COUNT(*) FROM aioffice.GroupAutomaticInputPlans WHERE {scope}),N'|',"
                f"(SELECT COUNT(*) FROM aioffice.GroupModelAttemptStarts WHERE {scope}),N'|',"
                f"(SELECT COUNT(*) FROM aioffice.GroupModelAttemptResults WHERE {scope}),N'|',"
                f"(SELECT COUNT(*) FROM aioffice.GroupBatchClaimReceipts WHERE {scope}));") == expected
            if index:
                witness = "IS NULL" if index == 1 else "=ExpiresAtUtc"
                assert sql(f"SELECT COUNT(*) FROM aioffice.GroupBatchClaimStates WHERE {scope} AND Epoch=1 AND ExpiryObservedAtUtc {witness};") == "1"
                assert sql(f"SELECT COUNT(*) FROM aioffice.GroupAutomaticInputPlans WHERE {scope} AND FormatVersion=2 AND OriginalClaimEpoch=1"
                    " AND PlanSha256=CONVERT(varchar(64),HASHBYTES('SHA2_256',[Plan]),2);") == "1"
                assert sql(f"SELECT COUNT(*) FROM aioffice.GroupModelAttemptResults WHERE {scope} AND FormatVersion=1"
                    " AND DATALENGTH(ProtectedResult) BETWEEN 229 AND 66029 AND SUBSTRING(ProtectedResult,1,1)=0x01"
                    " AND ProtectedResultSha256=CONVERT(varchar(64),HASHBYTES('SHA2_256',ProtectedResult),2);") == "1"
                # After commit freeze every field except the single authorized
                # witness. After expiry compare to that immutable baseline.
                unchanged = graph(with_witness=False)
                if accepted_graph is None: accepted_graph = unchanged
                assert unchanged == accepted_graph, "First expiry changed original accepted graph"
        completed = graph()
        require_retained()
    except BaseException as error:
        failure = error
    finally:
        try: automatic.close_owned_container(directory=directory, api=api, name=image, suffix=suffix, reference=reference)
        except BaseException as error:
            if failure is None: failure = error
        try: require_retained()
        except BaseException as error:
            if failure is None: failure = error
        try:
            removed = subprocess.run(["docker", "image", "rm", image], capture_output=True, text=True, timeout=30)
            if removed.returncode != 0 and failure is None: failure = AssertionError("Owned expiry image cleanup failed")
        except BaseException as error:
            if failure is None: failure = error
        try: require_retained()
        except BaseException as error:
            if failure is None: failure = error
    if failure is not None: raise failure
    print(SUMMARY, flush=True)
