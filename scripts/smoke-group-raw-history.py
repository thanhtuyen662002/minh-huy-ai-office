"""Separate owned actual-Core501/SQL500 history proof; no terminal/model claim."""
import json
import os
import re
import subprocess
import uuid


RUNTIME_LINES = {
    "raw-history-prepare": "PASS owned raw history actual501 inbox references allocation500 pending suffix501 two selected cutoff heads no claims effects model",
    "raw-history-expiry": "PASS owned raw history five-hundred raw plus three effects flushed same SQL savepoint rollback detach witness only pending501 retained clock rollback denied",
    "raw-history-commit": "PASS owned raw history atomic500 raw rows cutoff499 ChangedAfterCutoff and empty1 NoWork original replay new nonce refused immutable runtime rights pending501 unchanged"}


def verify(*, directory, api, manifest, tenant, company, service, sql, prepare_source, reference, automatic):
    reference.require_owned(directory, api)  # Before config/callbacks/SQL/process.
    assert callable(sql) and callable(prepare_source)
    tenant, company, service = (automatic.canonical(value) for value in (tenant, company, service))
    installation = automatic.canonical(manifest["AIOFFICE_INSTALLATION_ID"])
    password = manifest["AIOFFICE_RUNTIME_PASSWORD"]
    assert isinstance(password, str) and re.fullmatch(r"[A-Za-z0-9_-]{32,128}", password)
    base = f"TenantId='{tenant}' AND CompanyId='{company}'"
    sources = [automatic.canonical(value.strip()) for value in sql(f"SELECT LOWER(CONVERT(char(36),Id)) FROM aioffice.GroupBindings WHERE {base} ORDER BY Id;").splitlines()]
    accounts = [automatic.canonical(value.strip()) for value in sql(f"SELECT LOWER(CONVERT(char(36),Id)) FROM aioffice.GroupConnectorAccounts WHERE {base} ORDER BY Id;").splitlines()]
    retained = automatic.retained_snapshot(tenant=tenant, company=company, sources=sources, accounts=accounts, sql=sql)
    def unchanged():
        assert automatic.retained_snapshot(tenant=tenant, company=company, sources=sources, accounts=accounts, sql=sql) == retained, \
            "Owned raw history changed a previously verified complete graph"
    source, events, key = prepare_source()
    source = automatic.canonical(source); events = [automatic.canonical(value) for value in events]
    reference.require_source_key(key)
    assert source not in sources and len(events) == len(set(events)) == 2
    account = automatic.canonical(sql(f"SELECT LOWER(CONVERT(char(36),ConnectorAccountId)) FROM aioffice.GroupBindings WHERE {base} AND Id='{source}';"))
    assert account not in accounts
    scope = base + f" AND BindingId='{source}'"
    unchanged()
    def digest(table, order, columns="*"):
        value = sql("SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),COALESCE("
            f"(SELECT {columns} FROM aioffice.{table} WHERE {scope} ORDER BY {order} FOR JSON PATH,INCLUDE_NULL_VALUES),N'[]'))),2);")
        assert re.fullmatch(r"[0-9A-F]{64}", value)
        return value
    def immutable():
        values = tuple(digest(table, order) for table, order in automatic.SOURCE_TABLES[:7]
            if table not in ("GroupSourceStates", "GroupIngressInbox")) + (
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
    def raw_heads():
        # All11 fields join scoped allocation, original revision, selected
        # cutoff head and receipt. Current revision500 is NOT the cutoff head.
        assert sql(f"SELECT CONCAT((SELECT COUNT(*) FROM aioffice.GroupWorkRawDispositions WHERE {scope}),N'|',"
            "(SELECT COUNT(*) FROM aioffice.GroupWorkRawDispositions r "
            "JOIN aioffice.GroupBatchAllocatedRevisions a ON a.TenantId=r.TenantId AND a.CompanyId=r.CompanyId AND a.BindingId=r.BindingId "
            "AND a.BatchId=r.BatchId AND a.CommittedSequence=r.CommittedSequence AND a.MessageId=r.MessageId AND a.Revision=r.RawRevision "
            "JOIN aioffice.GroupMessageRevisions m ON m.TenantId=r.TenantId AND m.CompanyId=r.CompanyId AND m.BindingId=r.BindingId "
            "AND m.MessageId=r.MessageId AND m.Revision=r.RawRevision AND m.CommittedSequence=r.CommittedSequence "
            "JOIN aioffice.GroupWorkSourceDispositions d ON d.TenantId=r.TenantId AND d.CompanyId=r.CompanyId AND d.BindingId=r.BindingId "
            "AND d.BatchId=r.BatchId AND d.MessageId=r.MessageId AND d.OperationId=r.OperationId "
            "AND d.MessageRevision=r.SelectedMessageRevision AND d.Outcome=r.Outcome "
            "JOIN aioffice.GroupWorkCommitReceipts w ON w.TenantId=r.TenantId AND w.CompanyId=r.CompanyId AND w.BindingId=r.BindingId "
            "AND w.BatchId=r.BatchId AND w.OperationId=r.OperationId "
            "JOIN aioffice.GroupBatchAllocations b ON b.TenantId=r.TenantId AND b.CompanyId=r.CompanyId AND b.BindingId=r.BindingId AND b.Id=r.BatchId "
            f"WHERE r.TenantId='{tenant}' AND r.CompanyId='{company}' AND r.BindingId='{source}' "
            "AND w.SelectedMessageCount=2 AND w.NoteCount=0 AND w.Outcome=2 AND b.AfterSequence=0 "
            "AND b.AllocatedThroughSequence=500 AND b.ObservedCommittedThroughSequence=501 AND b.RawRevisionCount=500 "
            "AND r.Relation=CASE WHEN r.RawRevision=r.SelectedMessageRevision THEN 1 ELSE 2 END "
            f"AND ((r.MessageId=(SELECT MessageId FROM aioffice.GroupMessageRevisions WHERE {scope} AND CommittedSequence=1) "
            "AND r.SelectedMessageRevision=499 AND r.Outcome=6) OR "
            f"(r.MessageId=(SELECT MessageId FROM aioffice.GroupMessageRevisions WHERE {scope} AND CommittedSequence=2) "
            "AND r.SelectedMessageRevision=1 AND r.Outcome=2))),N'|',"
            f"(SELECT COUNT(*) FROM aioffice.GroupWorkRawDispositions WHERE {scope} AND Relation=1),N'|',"
            f"(SELECT COUNT(*) FROM aioffice.GroupWorkRawDispositions WHERE {scope} AND Relation=2));") == "500|500|2|498"
    immutable_before = immutable(); no_gaps()
    assert sql(f"SELECT COUNT(*) FROM aioffice.GroupSourceStates WHERE {scope} AND CommittedSequence=501 AND ScheduledThroughSequence=0 "
        "AND FirstPendingAtUtc IS NOT NULL AND LastPendingAtUtc IS NOT NULL AND FirstPendingAtUtc<=LastPendingAtUtc "
        "AND DATEPART(TZOFFSET,FirstPendingAtUtc)=0 AND DATEPART(TZOFFSET,LastPendingAtUtc)=0 "
        f"AND LastPendingAtUtc>=(SELECT CommittedAtUtc FROM aioffice.GroupMessageRevisions WHERE {scope} AND CommittedSequence=501);") == "1"
    columns = "TenantId,CompanyId,BindingId,CommittedSequence,FirstPendingAtUtc,LastPendingAtUtc,ScheduledThroughSequence"
    expected_state = digest("GroupSourceStates", "BindingId", "TenantId,CompanyId,BindingId,CommittedSequence,"
        f"(SELECT CommittedAtUtc FROM aioffice.GroupMessageRevisions WHERE {scope} AND CommittedSequence=501) AS FirstPendingAtUtc,"
        "LastPendingAtUtc,CAST(500 AS bigint) AS ScheduledThroughSequence")
    assert all(sql(f"SELECT COUNT(*) FROM aioffice.{table} WHERE {scope};") == "0" for table in
        automatic.EFFECT_TABLES + ("GroupWorkRawDispositions", "GroupIngressInbox", "GroupBatchAllocations", "GroupBatchAllocatedRevisions", "GroupBatchClaimStates", "GroupBatchClaimReceipts"))
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
        assert built.returncode == 0, "Owned raw history executable build failed"
        configuration = json.dumps({"tenantId": tenant, "companyId": company, "serviceId": service, "sourceId": source, "eventId": events[0]})
        prepared_graph = None
        for mode, expected in RUNTIME_LINES.items():
            result = subprocess.run([*command, mode], input=configuration, env=environment, capture_output=True, text=True, timeout=170)
            assert not result.stderr, "Owned raw history executable emitted unexpected diagnostics"
            expected_lines = [expected]
            if mode == "raw-history-commit": expected_lines.append(automatic.DEPENDENCY_RUNTIME_LINES[mode])
            if mode == "raw-history-commit": expected_lines.append(automatic.EFFECT_EXPECTATION_RUNTIME_LINES[mode])
            reference.require_reference_result(result, mode, expected_lines)
            unchanged(); no_gaps()
            assert immutable() == immutable_before, "Owned raw history changed protected source originals"
            assert digest("GroupSourceStates", "BindingId", columns) == expected_state, "Owned raw history lost its exact pending suffix"
            graph = tuple(digest(table, order) for table, order in automatic.SOURCE_TABLES[6:9])
            if prepared_graph is None:
                assert sql(f"SELECT CONCAT((SELECT COUNT(*) FROM aioffice.GroupIngressInbox WHERE {scope}),N'|',"
                    f"(SELECT COUNT(*) FROM aioffice.GroupBatchAllocations WHERE {scope} AND OperationId='{events[0]}' AND AfterSequence=0 "
                    "AND AllocatedThroughSequence=500 AND ObservedCommittedThroughSequence=501 AND RawRevisionCount=500),N'|',"
                    f"(SELECT COUNT(*) FROM aioffice.GroupBatchAllocatedRevisions WHERE {scope}));") == "501|1|500"
                prepared_graph = graph
            assert graph == prepared_graph
            epoch = list(RUNTIME_LINES).index(mode)
            assert sql(f"SELECT COUNT(*) FROM aioffice.GroupBatchClaimReceipts WHERE {scope};") == str(epoch)
            if epoch:
                witness = "IS NULL" if mode == "raw-history-commit" else "=ExpiresAtUtc"
                assert sql(f"SELECT COUNT(*) FROM aioffice.GroupBatchClaimStates WHERE {scope} AND Epoch={epoch} AND ExpiryObservedAtUtc {witness};") == "1"
            if mode != "raw-history-commit":
                assert all(sql(f"SELECT COUNT(*) FROM aioffice.{table} WHERE {scope};") == "0" for table in automatic.EFFECT_TABLES + ("GroupWorkRawDispositions",))
            else:
                raw_heads()
                assert [sql(f"SELECT COUNT(*) FROM aioffice.{table} WHERE {scope};") for table in automatic.EFFECT_TABLES] == ["1", "2", "0", "0", "0", "0", "0"]
    except BaseException as error:
        failure = error
    finally:
        try:
            automatic.close_owned_container(directory=directory, api=api, name=image, suffix=suffix, reference=reference)
        except BaseException as error:
            if failure is None: failure = error
        try:
            removed = subprocess.run(["docker", "image", "rm", image], capture_output=True, text=True, timeout=30)
            if removed.returncode != 0 and failure is None: failure = AssertionError("Owned raw history image cleanup failed")
        except BaseException as error:
            if failure is None: failure = error
    if failure is not None: raise failure
    print("PASS actual raw history SQL actual501 Core ingress allocation500 raw500 exact cutoff heads two selected498 superseded same transaction rollback replay immutable rights pending501 retained all prior scopes no model", flush=True)
