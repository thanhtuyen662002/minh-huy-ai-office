"""Owned real SQL journal gates. Synthetic context; never a model evaluation."""
import json
import os
import re
import subprocess
import uuid


RUNTIME_LINES = {
    "model-journal-prepare": "PASS owned automatic note actual inbox and allocation exact two media references empty effects and claims",
    "model-journal-lost-reply": "PASS owned model journal actual start committed reply interrupted fresh context replay no second permit one start zero failures no effects provider or model",
    "model-journal-resume": "PASS owned model journal separate process replacement exact input second ordinal original deadline bounded backoff immutable failure replay clock rewind denied no effects provider or model",
    "model-journal-cap": "PASS owned model journal four actual concurrent contexts one third ordinal two failures fourth unavailable original claim predecessor timing cap and mutation SQL refusals no effects provider or model",
    "model-journal-expiry": "PASS owned model journal flushed start source lock savepoint rollback clean detach witness only durable clock rewind denied original three starts two failures no effects provider or model"
}
SUMMARY = "PASS actual model attempt journal SQL committed reply interruption separate process replacement four concurrent contexts bounded three starts original claim predecessor timing cap immutable rights expiry flushed rollback witness only clock rewind no effects unchanged retained scopes no provider tokenizer DI or model"

EXTRACTION_FAILURE_LINE = "PASS owned extraction failure SQL exact exhausted input wrong input refused configured key expiry witness rewind denied replacement host only protected three notes four metadata evidence Attention quarantine atomic NotesCommitted replay new nonce refused unchanged journal no provider model or IT send"
EXTRACTION_FAILURE_SUMMARY = "PASS actual extraction failure SQL original exhausted journal exact input configured key expiry durable witness replacement host metadata protected notes full raw effects replay no duplicate unchanged retained scopes no provider model or IT send"


def verify(*, directory, api, manifest, tenant, company, service, sql, prepare_source, reference, automatic):
    reference.require_owned(directory, api)  # Before callbacks, config, SQL or processes.
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

    def hash_rows(table, order, predicate, columns="*"):
        value = sql("SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),COALESCE("
            f"(SELECT {columns} FROM aioffice.{table} WHERE {predicate} ORDER BY {order} FOR JSON PATH,INCLUDE_NULL_VALUES),N'[]'))),2);")
        assert type(value) is str and re.fullmatch(r"[0-9A-F]{64}", value), "Owned model journal snapshot refused"
        return value

    def retained():
        original = automatic.retained_snapshot(tenant=tenant, company=company, sources=sources, accounts=accounts, sql=sql)
        journal = tuple(hash_rows(table, "BindingId,RequestId", base + f" AND BindingId IN ({source_list})")
            for table in ("GroupModelAttemptStarts", "GroupModelAttemptFailures"))
        return original + journal

    retained_before = retained()
    source, events, key = prepare_source()
    source = automatic.canonical(source); events = tuple(automatic.canonical(value) for value in events)
    reference.require_source_key(key)
    assert source not in sources and len(events) == len(set(events)) == 2
    assert retained() == retained_before, "Owned model journal preparation changed retained scopes"
    scope = base + f" AND BindingId='{source}'"
    account = automatic.canonical(sql(f"SELECT LOWER(CONVERT(char(36),ConnectorAccountId)) FROM aioffice.GroupBindings WHERE {base} AND Id='{source}';"))
    assert account not in accounts

    def originals():
        values = tuple(hash_rows(table, order, scope) for table, order in automatic.SOURCE_TABLES[:7]
            if table not in ("GroupSourceStates", "GroupIngressInbox"))
        values += (hash_rows("GroupSourceStates", "BindingId", scope, "TenantId,CompanyId,BindingId,CommittedSequence"),
            hash_rows("GroupServiceGrants", "ServiceId,Capability", scope),
            hash_rows("GroupBindings", "Id", base + f" AND Id='{source}'"),
            hash_rows("GroupConnectorAccounts", "Id", base + f" AND Id='{account}'"),
            hash_rows("GroupListenerLeases", "ConnectorAccountId", base + f" AND ConnectorAccountId='{account}'"))
        return values

    original_before = originals()
    assert sql(f"SELECT COUNT(*) FROM aioffice.GroupSourceStates WHERE {scope} AND CommittedSequence=2 AND ScheduledThroughSequence=0"
        " AND FirstPendingAtUtc IS NOT NULL AND LastPendingAtUtc IS NOT NULL AND FirstPendingAtUtc<=LastPendingAtUtc;") == "1"
    suffix = uuid.uuid4().hex; image = "aioffice-automatic-note-proof-" + suffix
    environment = dict(os.environ, AIOFFICE_OWNED_GROUP_REFERENCE_PROOF="true",
        AIOFFICE_GROUP_REFERENCE_PROOF_CONNECTION="Server=sql;Database=AIOfficeLocal;User ID=aioffice_runtime;Password="
            + password + ";Encrypt=true;TrustServerCertificate=true", AIOFFICE_GROUP_REFERENCE_PROOF_SOURCE_KEY=key)
    command = ["docker", "run", "--init", "--rm", "--name", image, "--label", "aioffice.owned-proof=" + suffix,
        "--network", "aioffice-" + uuid.UUID(installation).hex + "_default", "--read-only", "--cap-drop", "ALL",
        "--security-opt", "no-new-privileges:true", "--tmpfs", "/tmp:rw,noexec,nosuid,size=16m", "-i",
        "-e", "CI", "-e", "GITHUB_ACTIONS", "-e", "AIOFFICE_OWNED_GROUP_REFERENCE_PROOF",
        "-e", "AIOFFICE_GROUP_REFERENCE_PROOF_CONNECTION", "-e", "AIOFFICE_GROUP_REFERENCE_PROOF_SOURCE_KEY", image]
    failure = None
    try:
        built = subprocess.run(["docker", "build", "-f", "tests/GroupReference.RuntimeProof/Dockerfile", "-t", image, "."],
            capture_output=True, text=True, timeout=300)
        assert built.returncode == 0, "Owned model journal executable build failed"
        configuration = json.dumps(dict(tenantId=tenant, companyId=company, serviceId=service, sourceId=source, eventId=events[0]))
        allocation_graph = None
        expected_counts = ("0|0|0", "1|0|1", "2|1|2", "3|2|3", "3|2|3")
        for index, (mode, expected) in enumerate(RUNTIME_LINES.items()):
            result = subprocess.run([*command, mode], input=configuration, env=environment, capture_output=True, text=True, timeout=170)
            assert not result.stderr, "Owned model journal executable emitted unexpected diagnostics"
            reference.require_reference_result(result, mode, [expected])
            assert retained() == retained_before, "Owned model journal changed retained scopes"
            assert originals() == original_before, "Owned model journal changed original source registry or protected bytes"
            assert sql(f"SELECT CONCAT((SELECT COUNT(*) FROM aioffice.GroupModelAttemptStarts WHERE {scope}),N'|',"
                f"(SELECT COUNT(*) FROM aioffice.GroupModelAttemptFailures WHERE {scope}),N'|',"
                f"(SELECT COUNT(*) FROM aioffice.GroupBatchClaimReceipts WHERE {scope}));") == expected_counts[index]
            graph = tuple(hash_rows(table, order, scope) for table, order in automatic.SOURCE_TABLES[6:9])
            if allocation_graph is None: allocation_graph = graph
            assert graph == allocation_graph
            assert sql(f"SELECT COUNT(*) FROM aioffice.GroupSourceStates WHERE {scope} AND CommittedSequence=2 AND ScheduledThroughSequence=2"
                " AND FirstPendingAtUtc IS NULL AND LastPendingAtUtc IS NULL;") == "1"
            assert sql(f"SELECT CONCAT((SELECT COUNT(*) FROM aioffice.GroupIngressInbox WHERE {scope}),N'|',"
                f"(SELECT COUNT(*) FROM aioffice.GroupBatchAllocations WHERE {scope} AND AfterSequence=0 AND AllocatedThroughSequence=2),N'|',"
                f"(SELECT COUNT(*) FROM aioffice.GroupBatchAllocatedRevisions WHERE {scope}));") == "2|1|2"
            assert all(sql(f"SELECT COUNT(*) FROM aioffice.{table} WHERE {scope};") == "0"
                for table in automatic.EFFECT_TABLES + ("GroupWorkRawDispositions", "GroupCoverageGaps"))
            if index:
                epoch = min(index, 3)
                witness = "=ExpiresAtUtc" if mode == "model-journal-expiry" else "IS NULL"
                assert sql(f"SELECT COUNT(*) FROM aioffice.GroupBatchClaimStates WHERE {scope} AND Epoch={epoch} AND ExpiryObservedAtUtc {witness};") == "1"
            if index == 4:
                assert sql(f"SELECT CONCAT(COUNT(DISTINCT InputSha256),N'|',MIN(Ordinal),N'|',MAX(Ordinal),N'|',"
                    f"MIN(ClaimEpoch),N'|',MAX(ClaimEpoch)) FROM aioffice.GroupModelAttemptStarts WHERE {scope};") == "1|1|3|1|3"
        journal_before_failure = tuple(hash_rows(table, "RequestId", scope)
            for table in ("GroupModelAttemptStarts", "GroupModelAttemptFailures"))
        mode = "model-journal-extraction-failure"
        result = subprocess.run([*command, mode], input=configuration, env=environment, capture_output=True, text=True, timeout=170)
        assert not result.stderr, "Owned extraction failure executable emitted unexpected diagnostics"
        reference.require_reference_result(result, mode, [EXTRACTION_FAILURE_LINE])
        assert retained() == retained_before, "Extraction failure changed retained scopes"
        assert originals() == original_before, "Extraction failure changed protected source or registry"
        assert tuple(hash_rows(table, "RequestId", scope) for table in ("GroupModelAttemptStarts", "GroupModelAttemptFailures")) == journal_before_failure
        assert tuple(hash_rows(table, order, scope) for table, order in automatic.SOURCE_TABLES[6:9]) == allocation_graph
        assert sql(f"SELECT COUNT(*) FROM aioffice.GroupBatchClaimStates WHERE {scope} AND Epoch=5 AND ExpiryObservedAtUtc IS NULL;") == "1"
        assert sql(f"SELECT COUNT(*) FROM aioffice.GroupBatchClaimReceipts WHERE {scope};") == "5"
        expected_effects = {"GroupCustomerRequests": 3, "GroupRequestRevisions": 3, "GroupRequestEvidence": 4,
            "GroupWorkCommitReceipts": 1, "GroupWorkSourceDispositions": 2, "GroupNotesCommittedOutbox": 1, "GroupNotesCommittedItems": 3,
            "GroupWorkRawDispositions": 2, "GroupCoverageGaps": 0}
        for table, expected_count in expected_effects.items():
            assert sql(f"SELECT COUNT(*) FROM aioffice.{table} WHERE {scope};") == str(expected_count)
        assert sql(f"SELECT COUNT(*) FROM aioffice.GroupRequestRevisions WHERE {scope} AND Origin=3 AND VerificationLevel=3 AND AuthorUserId IS NULL;") == "3"
        assert sql(f"SELECT COUNT(*) FROM aioffice.GroupNotesCommittedOutbox WHERE {scope} AND NoteCount=3 AND PublishedAtUtc IS NULL AND PublishAttempts=0;") == "1"
    except BaseException as error:
        failure = error
    finally:
        try:
            automatic.close_owned_container(directory=directory, api=api, name=image, suffix=suffix, reference=reference)
        except BaseException as error:
            if failure is None: failure = error
        try:
            removed = subprocess.run(["docker", "image", "rm", image], capture_output=True, text=True, timeout=30)
            if removed.returncode != 0 and failure is None: failure = AssertionError("Owned model journal image cleanup failed")
        except BaseException as error:
            if failure is None: failure = error
    if failure is not None: raise failure
    print(SUMMARY, flush=True)
    print(EXTRACTION_FAILURE_SUMMARY, flush=True)
