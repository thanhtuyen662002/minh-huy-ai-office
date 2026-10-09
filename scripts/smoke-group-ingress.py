"""Shipping HMAC HTTP -> runtime SQL proof, only on owned disposable GitHub CI."""
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timezone
import base64
import hashlib
import hmac
import json
import os
from pathlib import Path
import re
import secrets
import struct
import subprocess
import time
import urllib.error
import urllib.request
import uuid


def identity_index(provider, account, group):
    value = b"aioffice-group-identity-v1\0"
    for item in (provider, account, group):
        encoded = item.encode("utf-8", errors="strict")
        value += struct.pack("<I", len(encoded)) + encoded
    return hashlib.sha256(value).hexdigest().upper()


def signing_bytes(service, epoch, signed_at, nonce, body):
    return (f"aioffice-group-ingest-v1\n{uuid.UUID(service)}\n{epoch}\n{signed_at}\n{uuid.UUID(nonce)}\n"
        + hashlib.sha256(body).hexdigest().upper()).encode("ascii")


def require_contiguous_cursor(sequences, cursor):
    assert 0 < cursor <= 4096 and sequences == list(range(1, cursor + 1)), "Group source skipped a committed sequence or stored beyond its cursor"


def require_owned(directory, api):
    if not (os.environ.get("CI") == "true" and os.environ.get("GITHUB_ACTIONS") == "true"
            and os.environ.get("RUNNER_TEMP") and directory.resolve() == (Path(os.environ["RUNNER_TEMP"]) / "aioffice-local").resolve()
            and api == "http://127.0.0.1:8080"):
        raise RuntimeError("Group ingress proof requires the owned disposable GitHub CI fixture.")


def owned_sql(compose, environment, query):
    result = subprocess.run([*compose, "exec", "-T", "sql", "sh", "-c",
        'SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -I -b -m 1 -h -1 -W -w 65535 -i /dev/stdin'],
        input="SET NOCOUNT ON; USE AIOfficeLocal; " + query, capture_output=True, text=True, env=environment, timeout=30)
    if result.returncode:
        match = re.search(r"\bMsg (\d{1,5}),", result.stdout + result.stderr)
        raise RuntimeError("Owned group SQL fixture command failed" + (f" (SQL message {match[1]})" if match else "") + ".")
    return result.stdout.strip()


def verify(*, directory, manifest, compose, environment, api):
    require_owned(directory, api)  # Before private configuration, files or processes.
    tenant, company = (str(uuid.UUID(manifest[key])) for key in ("AIOFFICE_TENANT_ID", "AIOFFICE_COMPANY_ID"))
    source, account, service, listener = (str(uuid.uuid4()) for _ in range(4))
    external = {"provider": "synthetic", "accountId": "owned-account ", "groupId": "owned-group😀 \uFEFF"}
    group_key, content_key = secrets.token_bytes(32), secrets.token_bytes(32)
    override = directory / "group-ingress-owned.override.json"
    scope = f"TenantId='{tenant}' AND CompanyId='{company}' AND BindingId='{source}'"
    registry_scope = f"TenantId='{tenant}' AND CompanyId='{company}'"
    schema = "aioffice."
    path = "/internal/group-ingress/events"

    def sql(query):
        return owned_sql(compose, environment, query)

    def compose_run(*arguments, overridden=False):
        command = [*compose, *(["-f", str(override)] if overridden else []), *arguments]
        result = subprocess.run(command, capture_output=True, text=True, env=environment, timeout=150)
        if result.returncode: raise RuntimeError("Owned group API recreation failed.")

    def call(payload, *, epoch=1, signature_key=None, raw_body=None):
        body = raw_body if raw_body is not None else json.dumps(payload, ensure_ascii=True, separators=(",", ":")).encode("utf-8")
        signed_at, nonce = int(time.time()), str(uuid.uuid4())
        signature = hmac.new(group_key if signature_key is None else signature_key,
            signing_bytes(service, epoch, signed_at, nonce, body), hashlib.sha256).hexdigest().upper()
        request = urllib.request.Request(api + path, data=body, method="POST", headers={
            "Content-Type": "application/json", "X-AIOffice-Group-Service": service,
            "X-AIOffice-Group-Epoch": str(epoch), "X-AIOffice-Group-Signed-At": str(signed_at),
            "X-AIOffice-Group-Nonce": nonce, "X-AIOffice-Group-Signature": signature,
            "X-Tenant-Id": str(uuid.uuid4()), "X-User-Id": str(uuid.uuid4())})
        try: response = urllib.request.urlopen(request, timeout=60)
        except urllib.error.HTTPError as error: response = error
        with response:
            value = response.read()
            assert "no-store" in response.headers.get("Cache-Control", ""), "Group response was cacheable"
            return response.status, json.loads(value) if value else None

    def event(*, message=None, revision=None, text="Owned original 😀\uFFFD \uFEFF", kind=1, historical=False):
        return {"event": {"identity": external.copy(), "messageId": message or str(uuid.uuid4()),
            "revisionEventId": revision or str(uuid.uuid4()), "senderId": "owned-sender ", "replyToMessageId": None,
            "kind": kind, "occurredAtUtc": datetime.now(timezone.utc).isoformat(),
            "contentSha256": hashlib.sha256(text.encode("utf-8")).hexdigest().upper(), "isHistoricalBackfill": historical},
            "text": text, "isGroup": True, "isSelf": False, "isKnownReportEcho": False,
            "listenerOwnerId": listener, "listenerEpoch": 1}

    def snapshot():
        # Hash complete original durable bytes, not only cardinalities.
        tables = [("GroupMessages", "Id"), ("GroupMessageRevisions", "CommittedSequence"),
            ("GroupIngressReceipts", "EventIdentityHash"), ("GroupIngressOutbox", "Id"),
            ("GroupSourceStates", "BindingId"), ("GroupCoverageGaps", "Id")]
        values = [sql(f"SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),COALESCE("
            f"(SELECT * FROM {schema}{table} WHERE {scope} ORDER BY {order} FOR JSON PATH,INCLUDE_NULL_VALUES),N'[]'))),2);")
            for table, order in tables]
        assert all(re.fullmatch(r"[0-9A-F]{64}", value) for value in values), "Invalid group snapshot"
        return values

    def counts():
        return [int(sql(f"SELECT COUNT(*) FROM {schema}{table} WHERE {scope};")) for table in
            ("GroupMessages", "GroupMessageRevisions", "GroupIngressReceipts", "GroupIngressOutbox")]

    def require_receipt(status, value):
        assert status == 200, f"Group committed ACK expected200, gotHTTP{status}"
        assert value["source"] == {"tenantId": tenant, "companyId": company, "sourceBindingId": source}, "Request headers changed SQL scope"
        assert set(value) == {"source", "messageId", "revision", "committedSequence", "committedAtUtc", "wasAlreadyCommitted"}, "ACK exposed unbounded source fields"
        return value

    def no_effect(payload, expected=403, **kwargs):
        before = snapshot()
        status, value = call(payload, **kwargs)
        assert status == expected, f"Group denial expectedHTTP{expected}, gotHTTP{status}"
        assert set(value) == {"error"} and "secretref://" not in json.dumps(value), "Denial exposed private authority"
        if payload["text"]: assert payload["text"] not in json.dumps(value), "Denial exposed source text"
        assert snapshot() == before, "Denied group ingress changed committed bytes"

    def ready():
        deadline = time.monotonic() + 60
        while time.monotonic() < deadline:
            try:
                with urllib.request.urlopen(api + "/health", timeout=3) as response:
                    if response.status == 200: return
            except (OSError, urllib.error.URLError): pass
            time.sleep(.2)
        raise RuntimeError("Owned group API readiness deadline exceeded.")

    def queued_revoke(revoke, restore):
        payload = event()
        before = snapshot()
        gate = "dbo.GroupIngressGate_" + uuid.uuid4().hex
        resource = f"aioffice:group-ingest:{uuid.UUID(tenant).hex}/{uuid.UUID(company).hex}/{uuid.UUID(source).hex}"
        sql(f"CREATE TABLE {gate}(Released bit NOT NULL,OwnerSpid int NULL); INSERT {gate} VALUES(0,NULL);")
        query = f"""SET NOCOUNT ON; USE AIOfficeLocal; SET XACT_ABORT ON;
          UPDATE {gate} SET OwnerSpid=@@SPID; BEGIN TRANSACTION;
          DECLARE @result int; EXEC @result=sys.sp_getapplock @Resource=N'{resource}',@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=0;
          IF @result<0 THROW 51000,'Owned group gate unavailable.',1;
          DECLARE @deadline datetime2=DATEADD(second,25,SYSUTCDATETIME());
          WHILE (SELECT Released FROM {gate} WITH(READUNCOMMITTED))=0 AND SYSUTCDATETIME()<@deadline WAITFOR DELAY '00:00:00.050';
          COMMIT TRANSACTION;"""
        locker = subprocess.Popen([*compose, "exec", "-T", "sql", "sh", "-c",
            'SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -I -b -m 1 -h -1 -W -i /dev/stdin'],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, env=environment)
        locker.stdin.write(query); locker.stdin.close(); locker.stdin = None
        pending = None
        executor = ThreadPoolExecutor(max_workers=1)

        def wait_observed(predicate, seconds):
            deadline = time.monotonic() + seconds
            while time.monotonic() < deadline:
                if predicate(): return
                if locker.poll() is not None or pending is not None and pending.done():
                    raise AssertionError("Group admission ended before observed queued fence")
                time.sleep(.05)
            raise AssertionError("Group queued fence was not observed")

        try:
            wait_observed(lambda: int(sql(f"SELECT COUNT(*) FROM sys.dm_tran_locks WHERE request_session_id=(SELECT OwnerSpid FROM {gate})"
                " AND resource_type=N'APPLICATION' AND resource_database_id=DB_ID() AND request_status=N'GRANT' AND request_mode=N'X';")) > 0, 8)
            pending = executor.submit(call, payload)
            wait_observed(lambda: int(sql(f"""SELECT COUNT(*) FROM sys.dm_exec_requests r
              JOIN sys.dm_exec_sessions s ON s.session_id=r.session_id
              JOIN sys.dm_tran_locks l ON l.request_session_id=r.session_id
              JOIN sys.dm_tran_locks held ON held.request_session_id=(SELECT OwnerSpid FROM {gate})
                AND held.resource_type=l.resource_type AND held.resource_database_id=l.resource_database_id
                AND held.resource_description=l.resource_description AND held.request_status=N'GRANT' AND held.request_mode=N'X'
              WHERE s.login_name=N'aioffice_runtime' AND r.blocking_session_id=(SELECT OwnerSpid FROM {gate})
                AND r.wait_type=N'LCK_M_X' AND l.resource_type=N'APPLICATION' AND l.resource_database_id=DB_ID()
                AND l.request_mode=N'X' AND l.request_status IN(N'WAIT',N'CONVERT');""")) > 0, 3)
            sql(revoke + f"; UPDATE {gate} SET Released=1;")
            status, value = pending.result(timeout=15)
            assert status == 403 and set(value) == {"error"}, "Fresh group authority did not deny observed queued write"
        finally:
            sql(f"UPDATE {gate} SET Released=1;")
            try: locker.communicate(timeout=8)
            except subprocess.TimeoutExpired:
                locker.kill(); locker.communicate(timeout=5)
            executor.shutdown(wait=True, cancel_futures=True)
            sql(restore + f"; DROP TABLE {gate};")
        assert locker.returncode == 0 and snapshot() == before, "Queued denial changed group bytes"
        require_receipt(*call(payload))

    assert call(event())[0] == 404, "Shipping group ingress was not disabled by default"
    assert not override.exists(), "Owned group override already exists"
    private_environment = {"AIOffice__GroupIntake__Enabled": "true", "AIOffice__GroupIntake__OwnedSyntheticFixture": "true",
        "AIOffice__GroupIntake__OwnedDisposableFixture": "true", "OWNED_GROUP_KEY": base64.b64encode(group_key).decode("ascii"),
        "OWNED_GROUP_CONTENT_KEY": base64.b64encode(content_key).decode("ascii")}
    for name, value in {"TenantId": tenant, "CompanyId": company, "SourceBindingId": source,
        "KeyId": "owned-source-v1", "SecretRef": "secretref://env/OWNED_GROUP_CONTENT_KEY", "IsWriteKey": "true"}.items():
        private_environment["AIOffice__GroupIntake__SourceKeys__0__" + name] = value
    descriptor = os.open(override, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(descriptor, "w", encoding="utf-8") as handle:
        json.dump({"services": {"core-api": {"environment": private_environment}}}, handle)
    try:
        literal = lambda value: "N'" + value.replace("'", "''") + "'"
        qualification = {"environment": 1, "observations": [{"capability": capability, "support": 1,
            "evidenceId": str(uuid.uuid4()), "observedAtUtc": datetime.now(timezone.utc).isoformat()} for capability in (8, 9)]}
        # Operator enrollment is explicit only in this disposable fixture. No
        # shipping bootstrap auto-enrollment, production grant or provider call.
        sql(f"""INSERT {schema}GroupConnectorAccounts
          (TenantId,CompanyId,Id,Provider,ExternalAccountId,IdentityHash,PackageVersion,GitCommit,QualificationJson,Version,IsEnabled)
          VALUES ('{tenant}','{company}','{account}','synthetic',{literal(external['accountId'])},
          '{identity_index('synthetic',external['accountId'],'account-registry')}','owned-fixture','{'0'*40}',{literal(json.dumps(qualification))},1,1);
          INSERT {schema}GroupServices(TenantId,CompanyId,Id,CredentialEpoch,CredentialReference,IsEnabled)
          VALUES ('{tenant}','{company}','{service}',1,N'secretref://env/OWNED_GROUP_KEY',1);
          INSERT {schema}GroupBindings(TenantId,CompanyId,Id,ConnectorAccountId,Role,Provider,ExternalAccountId,ExternalGroupId,IdentityHash,PhysicalGroupHash,DisplayName,Version,DeletionGeneration,IsEnabled)
          VALUES ('{tenant}','{company}','{source}','{account}',1,'synthetic',{literal(external['accountId'])},{literal(external['groupId'])},
          '{identity_index(**dict(provider='synthetic',account=external['accountId'],group=external['groupId']))}',
          '{identity_index('synthetic','physical-group-registry',external['groupId'])}',N'Owned CI source',1,0,1);
          INSERT {schema}GroupServiceGrants(TenantId,CompanyId,ServiceId,BindingId,Capability,Version,IsEnabled)
          VALUES ('{tenant}','{company}','{service}','{source}',1,1,1);
          INSERT {schema}GroupListenerLeases(TenantId,CompanyId,ConnectorAccountId,OwnerId,Epoch,ExpiresAtUtc,HeartbeatAtUtc)
          VALUES ('{tenant}','{company}','{account}','{listener}',1,DATEADD(minute,20,TODATETIMEOFFSET(SYSUTCDATETIME(),'+00:00')),TODATETIMEOFFSET(SYSUTCDATETIME(),'+00:00'));
        """)
        compose_run("up", "-d", "--no-deps", "--force-recreate", "core-api", overridden=True)
        ready()
        empty_snapshot = snapshot()
        assert counts() == [0, 0, 0, 0], "Owned new source was not empty"
        empty_digest = hashlib.sha256("[]".encode("utf-16-le")).hexdigest().upper()
        assert empty_snapshot == [empty_digest] * 6, "Empty owned source did not have canonical complete-byte fingerprints"
        owner_graph = sql("SELECT CONCAT((SELECT COUNT(*) FROM aioffice.Users),N'|',(SELECT COUNT(*) FROM aioffice.Tasks),N'|',"
            "(SELECT COUNT(*) FROM aioffice.TaskDispatches),N'|',(SELECT COUNT(*) FROM aioffice.TaskCheckpoints));")
        payload = event()
        original = require_receipt(*call(payload))
        assert counts() == [1, 1, 1, 1] and original["committedSequence"] == original["revision"] == 1
        first_snapshot = snapshot()
        assert sql(f"SELECT COUNT(*) FROM {schema}GroupMessageRevisions WHERE {scope} AND ContentKeyId='owned-source-v1'"
            " AND DATALENGTH(ProtectedContent)>29 AND SUBSTRING(ProtectedContent,1,1)=0x01;") == "1", "Source protection envelope was not committed"
        # Concurrent calls are separately signed. They must reconcile one
        # logical event without renewing pending anchors or changing any bytes.
        with ThreadPoolExecutor(max_workers=8) as executor:
            repeated = list(executor.map(lambda _: call(payload), range(100)))
        for result in repeated:
            receipt = require_receipt(*result)
            assert receipt == {**original, "wasAlreadyCommitted": True}, "Duplicate event receipt changed"
        assert snapshot() == first_snapshot and counts() == [1, 1, 1, 1]
        print("PASS actual group HTTP HMAC/SQL scope/protected atomic graph and concurrent100 one-receipt replay with unchanged pending/outbox")

        changed = json.loads(json.dumps(payload)); changed["event"]["senderId"] += "changed"
        no_effect(changed, 409)
        for change in ("isGroup", "isSelf", "isKnownReportEcho", "foreign-group", "signature", "epoch"):
            denied = event()
            if change in ("isGroup", "isSelf", "isKnownReportEcho"): denied[change] = change != "isGroup"
            if change == "foreign-group": denied["event"]["identity"]["groupId"] += "foreign"
            no_effect(denied, **({"signature_key": b"0"*32} if change == "signature" else {"epoch": 2} if change == "epoch" else {}))
        no_effect(event(), raw_body=b'{"invalid":true}')
        print("PASS actual group unenrolled/DM/self/echo/bad signature/epoch/strict body and changed logical-event denial without effects")

        # A physical alias with an intentionally corrupt different index still
        # cannot create customer->IT role confusion. Full stored bytes govern.
        alias = str(uuid.uuid4())
        sql(f"INSERT {schema}GroupBindings(TenantId,CompanyId,Id,ConnectorAccountId,Role,Provider,ExternalAccountId,ExternalGroupId,IdentityHash,PhysicalGroupHash,DisplayName,Version,DeletionGeneration,IsEnabled)"
            f" SELECT TenantId,CompanyId,'{alias}',ConnectorAccountId,2,Provider,ExternalAccountId,ExternalGroupId,"
            f"REPLICATE('A',64),REPLICATE('B',64),N'Owned disabled alias',Version,DeletionGeneration,0 FROM {schema}GroupBindings WHERE {registry_scope} AND Id='{source}';")
        try: no_effect(event())
        finally: sql(f"DELETE {schema}GroupBindings WHERE {registry_scope} AND Id='{alias}';")
        try:
            sql(f"UPDATE {schema}GroupBindings SET ExternalGroupId=CONVERT(nvarchar(256),0x00D8) WHERE {registry_scope} AND Id='{source}';")
            no_effect(event())
        finally: sql(f"UPDATE {schema}GroupBindings SET ExternalGroupId={literal(external['groupId'])} WHERE {registry_scope} AND Id='{source}';")
        require_receipt(*call(event()))
        print("PASS actual group independent physical-role alias catalog and original malformed UTF16 denial/restored positive")

        # Real SQL failure targets the next outbox write after the protected
        # graph is staged. Whole graph and source cursor must roll back.
        before = snapshot()
        sequence = int(sql(f"SELECT CommittedSequence FROM {schema}GroupSourceStates WHERE {scope};")) + 1
        rollback = event()
        sql(f"ALTER TABLE {schema}GroupIngressOutbox ADD CONSTRAINT CK_CiGroupOutboxRollback CHECK(BindingId<>'{source}' OR CommittedSequence<>{sequence});")
        try:
            assert call(rollback)[0] == 503, "Failed SQL write acknowledged a group receipt"
            assert snapshot() == before, "Failed outbox write did not roll back all group bytes"
        finally: sql(f"ALTER TABLE {schema}GroupIngressOutbox DROP CONSTRAINT CK_CiGroupOutboxRollback;")
        assert require_receipt(*call(rollback))["committedSequence"] == sequence
        print("PASS actual group outbox SQL failure rolls back receipt/revision/message/pending/cursor and stable restored retry")

        # A column GRANT genuinely overrides table DENY. The shipping proof
        # refuses it before content-key/write effects; restore explicit rights.
        sql("REVOKE UPDATE ON OBJECT::aioffice.GroupIngressReceipts(EnvelopeSha256) FROM aioffice_binding_runtime;"
            " GRANT UPDATE ON OBJECT::aioffice.GroupIngressReceipts(EnvelopeSha256) TO aioffice_runtime;")
        try:
            assert sql("EXECUTE AS LOGIN=N'aioffice_runtime'; BEGIN TRANSACTION; UPDATE aioffice.GroupIngressReceipts SET EnvelopeSha256=REPLICATE('0',64);"
                " ROLLBACK TRANSACTION; REVERT; SELECT N'ALLOWED';") == "ALLOWED"
            no_effect(event())
        finally:
            sql("REVOKE UPDATE ON OBJECT::aioffice.GroupIngressReceipts(EnvelopeSha256) FROM aioffice_runtime;"
                " DENY UPDATE ON OBJECT::aioffice.GroupIngressReceipts(EnvelopeSha256) TO aioffice_binding_runtime;")
        require_receipt(*call(event()))
        print("PASS actual group unsafe effective append-only column permission refusal and restored positive")

        revocations = [
            ("source", "GroupBindings", f"{registry_scope} AND Id='{source}'", "IsEnabled=0", "IsEnabled=1"),
            ("grant", "GroupServiceGrants", f"{scope} AND ServiceId='{service}' AND Capability=1", "IsEnabled=0", "IsEnabled=1"),
            ("service-epoch", "GroupServices", f"{registry_scope} AND Id='{service}'", "CredentialEpoch=2", "CredentialEpoch=1"),
            ("account", "GroupConnectorAccounts", f"{registry_scope} AND Id='{account}'", "IsEnabled=0", "IsEnabled=1"),
            ("deletion", "GroupBindings", f"{registry_scope} AND Id='{source}'", "DeletionGeneration=1", "DeletionGeneration=0"),
            ("listener-epoch", "GroupListenerLeases", f"{registry_scope} AND ConnectorAccountId='{account}'", "Epoch=2", "Epoch=1")]
        for name, table, predicate, revoke, restore in revocations:
            queued_revoke(f"UPDATE {schema}{table} SET {revoke} WHERE {predicate}", f"UPDATE {schema}{table} SET {restore} WHERE {predicate}")
            print("PASS actual group observed queued source-lock " + name + " revoke denies unchanged graph/restored positive")

        message = str(uuid.uuid4())
        before_edit = int(sql(f"SELECT CommittedSequence FROM {schema}GroupSourceStates WHERE {scope};"))
        gap_after_edit = None
        for kind, text, historical in [(3, "Owned edit", False), (4, "", False), (1, "Late owned original", True)]:
            require_receipt(*call(event(message=message, kind=kind, text=text, historical=historical)))
            if kind == 3: gap_after_edit = snapshot()[5]
        assert gap_after_edit == snapshot()[5], "Recall or late original erased/changed the original-unseen coverage gap"
        history = json.loads(sql(f"SELECT r.Revision,r.Kind,r.ContentSha256,r.IsHistoricalBackfill FROM {schema}GroupMessageRevisions r"
            f" JOIN {schema}GroupMessages m ON m.TenantId=r.TenantId AND m.CompanyId=r.CompanyId AND m.BindingId=r.BindingId AND m.Id=r.MessageId"
            f" WHERE r.TenantId='{tenant}' AND r.CompanyId='{company}' AND r.BindingId='{source}' AND m.ExternalMessageId=N'{message}' ORDER BY r.Revision FOR JSON PATH;"))
        assert history == [{"Revision": index, "Kind": kind, "ContentSha256": hashlib.sha256(text.encode()).hexdigest().upper(), "IsHistoricalBackfill": historical}
            for index, (kind, text, historical) in enumerate([(3, "Owned edit", False), (4, "", False), (1, "Late owned original", True)], start=1)]
        assert sql(f"SELECT COUNT(*) FROM {schema}GroupCoverageGaps WHERE {scope} AND Reason='original-message-unseen' AND AfterCommittedSequence={before_edit};") == "1"
        assert sql(f"SELECT COUNT(*) FROM {schema}GroupMessageRevisions WHERE {scope} AND IsHistoricalBackfill=1;") == "1"
        sequence = int(sql(f"SELECT CommittedSequence FROM {schema}GroupSourceStates WHERE {scope};"))
        sequences = sql(f"SELECT STRING_AGG(CONVERT(varchar(max),CommittedSequence),',') WITHIN GROUP(ORDER BY CommittedSequence) FROM {schema}GroupMessageRevisions WHERE {scope};")
        require_contiguous_cursor([int(value) for value in sequences.split(',')], sequence)
        assert owner_graph == sql("SELECT CONCAT((SELECT COUNT(*) FROM aioffice.Users),N'|',(SELECT COUNT(*) FROM aioffice.Tasks),N'|',"
            "(SELECT COUNT(*) FROM aioffice.TaskDispatches),N'|',(SELECT COUNT(*) FROM aioffice.TaskCheckpoints));")
        retained = snapshot()
        compose_run("restart", "core-api", overridden=True)
        ready()
        assert require_receipt(*call(payload)) == {**original, "wasAlreadyCommitted": True} and snapshot() == retained
        print("PASS actual group append-only edit/recall/late-history gap, contiguous commit cursor, retained restart and no fake portal task/user")
    finally:
        # Remove configuration from this host before the existing browser gate.
        # Durable synthetic source evidence stays in the owned disposable SQL.
        try:
            compose_run("up", "-d", "--no-deps", "--force-recreate", "core-api")
            ready()
        finally:
            # This invocation exclusively created this exact file. Even a
            # terminal restore failure must not retain private random keys.
            override.unlink()
    assert call(event())[0] == 404
    print("PASS owned group API private configuration removed; shipping default-off restored; no live connector or send attempted")
