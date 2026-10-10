"""Owned actual combined-writer effects. Synthetic interpretations, no model eval."""
import json
import os
import re
import subprocess
import uuid


SOURCE_TABLES = (("GroupMessages", "Id"), ("GroupMessageRevisions", "CommittedSequence"),
    ("GroupIngressReceipts", "EventIdentityHash"), ("GroupSourceStates", "BindingId"),
    ("GroupCoverageGaps", "Id"), ("GroupIngressOutbox", "CommittedSequence"), ("GroupIngressInbox", "CommittedSequence"),
    ("GroupBatchAllocations", "AfterSequence"), ("GroupBatchAllocatedRevisions", "CommittedSequence"),
    ("GroupBatchClaimStates", "BatchId"), ("GroupBatchClaimReceipts", "BatchId,Epoch"),
    ("GroupCustomerRequests", "Id"), ("GroupRequestRevisions", "RequestId,Revision"),
    ("GroupRequestEvidence", "RequestId,RequestRevision,Ordinal"), ("GroupWorkCommitReceipts", "BatchId,OperationId"),
    ("GroupWorkSourceDispositions", "BatchId,MessageId"), ("GroupNotesCommittedOutbox", "Id"),
    ("GroupNotesCommittedItems", "OutboxId,Ordinal"), ("GroupGlossaryEntries", "Id"), ("GroupGlossaryRevisions", "EntryId,Revision"),
    ("GroupReaderGrants", "UserId"), ("GroupEditorGrants", "UserId"), ("GroupServiceGrants", "ServiceId,Capability"))
EFFECT_TABLES = ("GroupWorkCommitReceipts", "GroupWorkSourceDispositions", "GroupCustomerRequests", "GroupRequestRevisions",
    "GroupRequestEvidence", "GroupNotesCommittedOutbox", "GroupNotesCommittedItems")
RUNTIME_LINES = {
    "automatic-note-prepare": "PASS owned automatic note actual inbox and allocation exact two media references empty effects and claims",
    "automatic-note-expiry": "PASS owned automatic note twenty-one flushed SQL effects rollback source lock retained clean detach only expiry witness clock rollback denied",
    "automatic-note-key-expiry": "PASS owned automatic note configured write key outside SQL expiry witness no effects clock rollback denied",
    "automatic-note-commit": "PASS owned automatic note protected mixed four notes five evidence two dispositions atomic NotesCommitted exact original replay changed plan new nonce denied no IT authority"}
NO_WORK_RUNTIME_LINES = {
    "automatic-no-work-prepare": "PASS owned automatic no-work actual inbox allocation two empty plain sources zero claims effects no model",
    "automatic-no-work-expiry": "PASS owned automatic no-work three flushed SQL effects source lock savepoint rollback clean detach witness only clock rollback denied",
    "automatic-no-work-commit": "PASS owned automatic no-work actual atomic receipt two exact NoWork dispositions original replay new nonce refusal no notes outbox or model"}
HOST_RUNTIME_LINES = {
    "automatic-host-prepare": "PASS owned automatic host actual inbox allocation two empty media sources no claims effects or model",
    "automatic-host-expiry": "PASS owned automatic host nine flushed SQL effects rollback source lock retained clean detach only expiry witness clock rollback denied",
    "automatic-host-key-expiry": "PASS owned automatic host configured write key outside SQL expiry witness no effects clock rollback denied",
    "automatic-host-commit": "PASS owned automatic host actual protected UnsupportedMedia note two metadata evidence two Attention dispositions atomic NotesCommitted original replay new nonce refusal no AI or IT authority"}
COVERAGE_RUNTIME_LINES = {
    "coverage-prepare": "PASS owned coverage actual inbox allocation two original text sources zero claims effects",
    "coverage-check": "PASS owned coverage SQL already incomplete reconnection source account additions key await refusal disposed material exact max256 overflow257 before keys seven reads zero writes unchanged effects"}


def canonical(value):
    result = str(uuid.UUID(value))
    assert result == value and uuid.UUID(result).int != 0
    return result


def close_owned_container(*, directory, api, name, suffix, reference):
    reference.require_owned(directory, api)
    assert isinstance(suffix, str) and re.fullmatch(r"[0-9a-f]{32}", suffix)
    assert name == "aioffice-automatic-note-proof-" + suffix
    failure = None
    # Docker caller timeout/exit does not prove the daemon-side child stopped.
    # Re-inspect before each independent bounded removal, including a partial
    # startup/lost reply. Only the exact owned container ID may be removed.
    for _ in range(2):
        try:
            inspected = subprocess.run(["docker", "inspect", "--type", "container", "--format",
                '{{.Id}}|{{index .Config.Labels "aioffice.owned-proof"}}', name], capture_output=True, text=True, timeout=10)
            if inspected.returncode:
                assert inspected.returncode == 1 and inspected.stdout in ("", "\n", "\r\n") and inspected.stderr.strip() in (
                    "Error: No such object: " + name, "Error: No such container: " + name,
                    "Error response from daemon: No such container: " + name), "Owned automatic container absence is unverified"
                continue
            identity, label = inspected.stdout.strip().split("|", 1)
            assert re.fullmatch(r"[0-9a-f]{64}", identity) and label == suffix and not inspected.stderr, \
                "Owned automatic container identity mismatch"
            removed = subprocess.run(["docker", "rm", "--force", identity], capture_output=True, text=True, timeout=15)
            assert removed.returncode == 0 or (removed.returncode == 1 and removed.stdout in ("", "\n", "\r\n") and removed.stderr.strip() in (
                "Error: No such container: " + identity, "Error response from daemon: No such container: " + identity)), \
                "Owned automatic container removal failed"
        except BaseException as error:
            if failure is None: failure = error
    if failure is not None: raise failure


def retained_snapshot(*, tenant, company, sources, accounts, sql):
    # Closed table/column names and canonical nonzero IDs, private full hashes.
    tenant, company = canonical(tenant), canonical(company)
    sources = tuple(sorted(canonical(value) for value in sources))
    accounts = tuple(sorted(canonical(value) for value in accounts))
    assert sources and accounts and len(set(sources)) == len(sources) and len(set(accounts)) == len(accounts)
    source_list, account_list = (','.join("'" + value + "'" for value in values) for values in (sources, accounts))
    base = f"TenantId='{tenant}' AND CompanyId='{company}'"
    queries = [(table, ','.join(dict.fromkeys(("BindingId", *order.split(',')))), base + f" AND BindingId IN ({source_list})")
        for table, order in SOURCE_TABLES]
    queries += [("GroupBindings", "Id", base + f" AND Id IN ({source_list})"),
        ("GroupConnectorAccounts", "Id", base + f" AND Id IN ({account_list})"),
        ("GroupListenerLeases", "ConnectorAccountId", base + f" AND ConnectorAccountId IN ({account_list})"),
        ("GroupAccountCoverageGaps", "ConnectorAccountId,ListenerEpoch,Reason", base + f" AND ConnectorAccountId IN ({account_list})"),
        ("GroupListenerCommandReceipts", "ConnectorAccountId,ServiceId,CredentialEpoch,Nonce", base + f" AND ConnectorAccountId IN ({account_list})"),
        ("GroupServices", "Id", base), ("Users", "TenantId,Id", "1=1"),
        ("Tasks", "TenantId,CompanyId,Id", "1=1"), ("TaskDispatches", "TenantId,CompanyId,TaskId,StepId,MessageId", "1=1"),
        ("TaskCheckpoints", "TenantId,CompanyId,TaskId,StepId,Version", "1=1")]
    # Retain all 33 complete snapshots, using one bounded SQL invocation rather
    # than starting 33 Docker/sqlcmd processes per fence. Closed index tags make
    # lost, duplicate, extra or reordered result lines fail closed.
    statements = ["SET NOCOUNT ON;"]
    for index, (table, order, predicate) in enumerate(queries):
        statements.append(f"SELECT N'{index:02d}|' + CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),COALESCE("
            f"(SELECT * FROM aioffice.{table} WHERE {predicate} ORDER BY {order} FOR JSON PATH,INCLUDE_NULL_VALUES),N'[]'))),2);")
    result = sql(''.join(statements))
    assert isinstance(result, str)
    lines = result.splitlines()
    assert len(lines) == len(queries)
    values = []
    for index, line in enumerate(lines):
        assert re.fullmatch(f"{index:02d}\\|[0-9A-F]{{64}}", line)
        values.append(line[3:])
    return tuple(values)


def verify(*, directory, api, manifest, tenant, company, service, sql, prepare_source, reference, proof="notes"):
    reference.require_owned(directory, api)  # Before config/callback/SQL/process.
    assert type(proof) is str and proof in ("notes", "no_work", "host_only", "coverage")
    assert callable(sql) and callable(prepare_source)
    runtime_lines = {"notes": RUNTIME_LINES, "no_work": NO_WORK_RUNTIME_LINES, "host_only": HOST_RUNTIME_LINES, "coverage": COVERAGE_RUNTIME_LINES}[proof]
    commit_mode = list(runtime_lines)[-1]
    tenant, company, service = (canonical(value) for value in (tenant, company, service))
    installation = canonical(manifest["AIOFFICE_INSTALLATION_ID"])
    password = manifest["AIOFFICE_RUNTIME_PASSWORD"]
    assert isinstance(password, str) and re.fullmatch(r"[A-Za-z0-9_-]{32,128}", password)
    base = f"TenantId='{tenant}' AND CompanyId='{company}'"
    # SQL uniqueidentifier display is not the canonical wire representation.
    # Project only these internal typed GUIDs; keep the input guard strict and
    # never normalize opaque provider/account/group/message identities.
    sources = [canonical(value.strip()) for value in sql(f"SELECT LOWER(CONVERT(char(36),Id)) FROM aioffice.GroupBindings WHERE {base} ORDER BY Id;").splitlines()]
    accounts = [canonical(value.strip()) for value in sql(f"SELECT LOWER(CONVERT(char(36),Id)) FROM aioffice.GroupConnectorAccounts WHERE {base} ORDER BY Id;").splitlines()]
    retained = retained_snapshot(tenant=tenant, company=company, sources=sources, accounts=accounts, sql=sql)
    def unchanged():
        assert retained_snapshot(tenant=tenant, company=company, sources=sources, accounts=accounts, sql=sql) == retained, \
            "Automatic fixture changed a previously verified source account claim brain or portal graph"
    source, events, key = prepare_source()
    source = canonical(source); events = [canonical(value) for value in events]
    reference.require_source_key(key)
    assert source not in sources and len(events) == len(set(events)) == 2
    unchanged()
    scope = base + f" AND BindingId='{source}'"
    account = canonical(sql(f"SELECT LOWER(CONVERT(char(36),ConnectorAccountId)) FROM aioffice.GroupBindings WHERE {base} AND Id='{source}';"))
    assert account not in accounts
    def digest(table, order, columns="*"):
        value = sql("SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),COALESCE("
            f"(SELECT {columns} FROM aioffice.{table} WHERE {scope} ORDER BY {order} FOR JSON PATH,INCLUDE_NULL_VALUES),N'[]'))),2);")
        assert re.fullmatch(r"[0-9A-F]{64}", value)
        return value
    def immutable():
        values = tuple(digest(table, order) for table, order in SOURCE_TABLES[:7] if table not in ("GroupSourceStates", "GroupIngressInbox")
            and not (proof == "coverage" and table == "GroupCoverageGaps")) + (
            digest("GroupSourceStates", "BindingId", "TenantId,CompanyId,BindingId,CommittedSequence"),
            digest("GroupServiceGrants", "ServiceId,Capability"))
        for table, predicate in (("GroupBindings", base + f" AND Id='{source}'"),
                ("GroupConnectorAccounts", base + f" AND Id='{account}'"),
                ("GroupListenerLeases", base + f" AND ConnectorAccountId='{account}'")):
            value = sql("SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),COALESCE("
                f"(SELECT * FROM aioffice.{table} WHERE {predicate} FOR JSON PATH,INCLUDE_NULL_VALUES),N'[]'))),2);")
            assert re.fullmatch(r"[0-9A-F]{64}", value)
            values += (value,)
        return values
    def no_gaps():
        assert sql(f"SELECT CONCAT((SELECT COUNT(*) FROM aioffice.GroupCoverageGaps WHERE {scope}),N'|',"
            f"(SELECT COUNT(*) FROM aioffice.GroupAccountCoverageGaps WHERE {base} AND ConnectorAccountId='{account}'),N'|',"
            f"(SELECT COUNT(*) FROM aioffice.GroupListenerCommandReceipts WHERE {base} AND ConnectorAccountId='{account}'));") == "0|0|0"
    immutable_before = immutable()
    no_gaps()
    # Freeze the expected pending/cursor transition BEFORE executing allocation.
    columns = "TenantId,CompanyId,BindingId,CommittedSequence,FirstPendingAtUtc,LastPendingAtUtc,ScheduledThroughSequence"
    assert sql(f"SELECT COUNT(*) FROM aioffice.GroupSourceStates WHERE {scope} AND CommittedSequence=2 AND ScheduledThroughSequence=0"
        " AND FirstPendingAtUtc IS NOT NULL AND LastPendingAtUtc IS NOT NULL AND FirstPendingAtUtc<=LastPendingAtUtc"
        " AND DATEPART(TZOFFSET,FirstPendingAtUtc)=0 AND DATEPART(TZOFFSET,LastPendingAtUtc)=0"
        f" AND FirstPendingAtUtc>=(SELECT CommittedAtUtc FROM aioffice.GroupMessageRevisions WHERE {scope} AND CommittedSequence=1)"
        f" AND LastPendingAtUtc>=(SELECT CommittedAtUtc FROM aioffice.GroupMessageRevisions WHERE {scope} AND CommittedSequence=2);") == "1"
    expected_state = digest("GroupSourceStates", "BindingId", "TenantId,CompanyId,BindingId,CommittedSequence,"
        "CAST(NULL AS datetimeoffset(7)) AS FirstPendingAtUtc,CAST(NULL AS datetimeoffset(7)) AS LastPendingAtUtc,CAST(2 AS bigint) AS ScheduledThroughSequence")
    assert all(sql(f"SELECT COUNT(*) FROM aioffice.{table} WHERE {scope};") == "0" for table in
        EFFECT_TABLES + ("GroupIngressInbox", "GroupBatchAllocations", "GroupBatchAllocatedRevisions", "GroupBatchClaimStates", "GroupBatchClaimReceipts"))
    suffix = uuid.uuid4().hex; image = "aioffice-automatic-note-proof-" + suffix
    environment = dict(os.environ)
    environment.update({"AIOFFICE_OWNED_GROUP_REFERENCE_PROOF": "true", "AIOFFICE_GROUP_REFERENCE_PROOF_CONNECTION":
        "Server=sql;Database=AIOfficeLocal;User ID=aioffice_runtime;Password=" + password + ";Encrypt=true;TrustServerCertificate=true",
        "AIOFFICE_GROUP_REFERENCE_PROOF_SOURCE_KEY": key})
    command = ["docker", "run", "--init", "--rm", "--name", image, "--label", "aioffice.owned-proof=" + suffix,
        "--network", "aioffice-" + uuid.UUID(installation).hex + "_default", "--read-only", "--cap-drop", "ALL",
        "--security-opt", "no-new-privileges:true", "--tmpfs", "/tmp:rw,noexec,nosuid,size=16m", "-i",
        "-e", "CI", "-e", "GITHUB_ACTIONS", "-e", "AIOFFICE_OWNED_GROUP_REFERENCE_PROOF",
        "-e", "AIOFFICE_GROUP_REFERENCE_PROOF_CONNECTION", "-e", "AIOFFICE_GROUP_REFERENCE_PROOF_SOURCE_KEY", image]
    failure = None
    try:
        built = subprocess.run(["docker", "build", "-f", "tests/GroupReference.RuntimeProof/Dockerfile", "-t", image, "."],
            capture_output=True, text=True, timeout=300)
        assert built.returncode == 0, "Owned automatic note executable build failed"
        configuration = json.dumps({"tenantId": tenant, "companyId": company, "serviceId": service, "sourceId": source, "eventId": events[0]})
        prepared_graph = None
        for mode, expected in runtime_lines.items():
            result = subprocess.run([*command, mode], input=configuration, env=environment, capture_output=True, text=True, timeout=170)
            assert not result.stderr, "Owned automatic note executable emitted unexpected diagnostics"
            reference.require_reference_result(result, mode, [expected])
            unchanged()
            if proof != "coverage" or mode != commit_mode: no_gaps()
            else:
                assert sql(f"SELECT CONCAT((SELECT COUNT(*) FROM aioffice.GroupCoverageGaps WHERE {scope}),N'|',"
                    f"(SELECT COUNT(*) FROM aioffice.GroupAccountCoverageGaps WHERE {base} AND ConnectorAccountId='{account}'),N'|',"
                    f"(SELECT COUNT(*) FROM aioffice.GroupListenerCommandReceipts WHERE {base} AND ConnectorAccountId='{account}'));") == "257|2|0"
            assert immutable() == immutable_before, "Automatic note effects changed protected source originals"
            assert digest("GroupSourceStates", "BindingId", columns) == expected_state
            graph = tuple(digest(table, order) for table, order in SOURCE_TABLES[6:9])
            if prepared_graph is None:
                assert sql(f"SELECT CONCAT((SELECT COUNT(*) FROM aioffice.GroupIngressInbox WHERE {scope}),N'|',"
                    f"(SELECT COUNT(*) FROM aioffice.GroupBatchAllocations WHERE {scope} AND OperationId='{events[0]}' AND AfterSequence=0"
                    " AND AllocatedThroughSequence=2 AND RawRevisionCount=2),N'|',"
                    f"(SELECT COUNT(*) FROM aioffice.GroupBatchAllocatedRevisions WHERE {scope}));") == "2|1|2"
                prepared_graph = graph
            assert graph == prepared_graph
            epoch = list(runtime_lines).index(mode)
            assert sql(f"SELECT COUNT(*) FROM aioffice.GroupBatchClaimReceipts WHERE {scope};") == str(epoch)
            if epoch:
                witness = "IS NULL" if mode == commit_mode else "=ExpiresAtUtc"
                assert sql(f"SELECT COUNT(*) FROM aioffice.GroupBatchClaimStates WHERE {scope} AND Epoch={epoch} AND ExpiryObservedAtUtc {witness};") == "1"
            if mode != commit_mode:
                assert all(sql(f"SELECT COUNT(*) FROM aioffice.{table} WHERE {scope};") == "0" for table in EFFECT_TABLES)
            elif proof == "notes":
                assert [sql(f"SELECT COUNT(*) FROM aioffice.{table} WHERE {scope};") for table in EFFECT_TABLES] == ["1", "2", "4", "4", "5", "1", "4"]
                assert sql(f"SELECT COUNT(*) FROM aioffice.GroupRequestRevisions WHERE {scope} AND Origin=1 AND VerificationLevel=1;") == "2"
                assert sql(f"SELECT COUNT(*) FROM aioffice.GroupRequestRevisions WHERE {scope} AND Origin=3 AND VerificationLevel=3;") == "2"
                assert sql(f"SELECT COUNT(*) FROM aioffice.GroupWorkCommitReceipts WHERE {scope} AND Outcome=1 AND NoteCount=4 AND SelectedMessageCount=2;") == "1"
            elif proof == "no_work":
                assert [sql(f"SELECT COUNT(*) FROM aioffice.{table} WHERE {scope};") for table in EFFECT_TABLES] == ["1", "2", "0", "0", "0", "0", "0"]
                assert sql(f"SELECT COUNT(*) FROM aioffice.GroupWorkCommitReceipts WHERE {scope} AND Outcome=2 AND NoteCount=0 AND SelectedMessageCount=2;") == "1"
                assert sql(f"SELECT COUNT(*) FROM aioffice.GroupWorkSourceDispositions WHERE {scope} AND Outcome=2 AND MessageRevision=1;") == "2"
            elif proof == "host_only":
                assert [sql(f"SELECT COUNT(*) FROM aioffice.{table} WHERE {scope};") for table in EFFECT_TABLES] == ["1", "2", "1", "1", "2", "1", "1"]
                assert sql(f"SELECT COUNT(*) FROM aioffice.GroupWorkCommitReceipts WHERE {scope} AND Outcome=3 AND NoteCount=1 AND SelectedMessageCount=2;") == "1"
                assert sql(f"SELECT COUNT(*) FROM aioffice.GroupWorkSourceDispositions WHERE {scope} AND Outcome=7 AND MessageRevision=1;") == "2"
                assert sql(f"SELECT COUNT(*) FROM aioffice.GroupRequestRevisions WHERE {scope} AND Origin=3 AND VerificationLevel=3;") == "1"
                assert sql(f"SELECT COUNT(*) FROM aioffice.GroupRequestEvidence WHERE {scope} AND Kind=2;") == "2"
            else:
                assert all(sql(f"SELECT COUNT(*) FROM aioffice.{table} WHERE {scope};") == "0" for table in EFFECT_TABLES)
                assert sql(f"SELECT COUNT(*) FROM aioffice.GroupCoverageGaps WHERE {scope} AND AfterCommittedSequence=2"
                    " AND Reason='owned-coverage-probe' AND DATEPART(TZOFFSET,OpenedAtUtc)=0;") == "257"
                assert sql(f"SELECT COUNT(*) FROM aioffice.GroupCoverageGaps WHERE {scope} AND ReconnectedAtUtc IS NOT NULL"
                    " AND ReconnectedAtUtc>=OpenedAtUtc AND DATEPART(TZOFFSET,ReconnectedAtUtc)=0;") == "1"
                assert sql(f"SELECT COUNT(*) FROM aioffice.GroupAccountCoverageGaps WHERE {base} AND ConnectorAccountId='{account}'"
                    " AND ListenerEpoch=1 AND Reason IN ('listener-started','listener-expired') AND RecordedAtUtc>=OpenedAtUtc"
                    " AND DATEPART(TZOFFSET,OpenedAtUtc)=0 AND DATEPART(TZOFFSET,RecordedAtUtc)=0;") == "2"
    except BaseException as error:
        failure = error
    finally:
        try:
            close_owned_container(directory=directory, api=api, name=image, suffix=suffix, reference=reference)
        except BaseException as error:
            if failure is None: failure = error
        try:
            removed = subprocess.run(["docker", "image", "rm", image], capture_output=True, text=True, timeout=30)
            if removed.returncode != 0 and failure is None: failure = AssertionError("Owned automatic note image cleanup failed")
        except BaseException as error:
            if failure is None: failure = error
    if failure is not None: raise failure
    if proof == "notes":
        print("PASS actual automatic note combined SQL four protected AI host notes five evidence atomic twenty-one effects both expiry rollback original replay no duplicate unchanged all retained scopes", flush=True)
    elif proof == "no_work":
        print("PASS actual automatic no-work SQL receipt two dispositions expiry rollback exact replay no duplicate no notes outbox model unchanged all retained scopes", flush=True)
    elif proof == "host_only":
        print("PASS actual automatic host SQL protected UnsupportedMedia note two metadata evidence Attention receipt atomic nine effects both expiry rollback original replay no AI IT authority unchanged all retained scopes", flush=True)
    else:
        print("PASS actual coverage SQL exact source account additions reconnection key await context refusal disposed material max256 overflow257 before keys no effects unchanged all retained scopes", flush=True)
