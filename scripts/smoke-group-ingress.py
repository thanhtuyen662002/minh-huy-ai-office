"""Shipping HMAC HTTP -> runtime SQL proof, only on owned disposable GitHub CI."""
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timezone
import base64
import hashlib
import hmac
import importlib.util
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


def decode_bounded_history(text, byte_count):
    assert 0 < byte_count <= 8000 and len(text.encode("utf-16-le")) == byte_count, "Owned group history transport was truncated or malformed"
    return json.loads(text)


def browser_failure_stage(line):
    match = re.fullmatch(r"FAIL owned group Chromium inbox gate: ([a-z-]{1,80}|native-reader-grant-(?:private|catalog)-http-[1-5][0-9]{2})", line)
    return match.group(1) if match else None


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


def permission_diagnostic_query():
    registry = ["GroupConnectorAccounts", "GroupServices", "GroupBindings", "GroupServiceGrants", "GroupReaderGrants"]
    append_only = ["GroupMessages", "GroupMessageRevisions", "GroupIngressReceipts", "GroupAccountCoverageGaps", "GroupListenerCommandReceipts", "GroupIngressInbox"]
    mutable = {"GroupListenerLeases": ["OwnerId", "Epoch", "ExpiresAtUtc", "HeartbeatAtUtc"],
        "GroupSourceStates": ["CommittedSequence", "ScheduledThroughSequence", "FirstPendingAtUtc", "LastPendingAtUtc"],
        "GroupCoverageGaps": ["ReconnectedAtUtc"],
        "GroupIngressOutbox": ["AvailableAtUtc", "PublishAttempts", "PublishedAtUtc"]}
    predicates = []
    for table in [*registry, *append_only, *mutable]:
        name = "aioffice." + table
        predicates += [f"OBJECT_ID(N'{name}',N'U') IS NOT NULL",
            f"EXISTS(SELECT 1 FROM sys.objects WHERE object_id=OBJECT_ID(N'{name}') AND principal_id=DATABASE_PRINCIPAL_ID(N'aioffice_binding_operator_owner'))"]
        for permission, expected in [("SELECT", 1), ("INSERT", 0 if table in registry else 1), ("UPDATE", 0),
                ("DELETE", 0), ("ALTER", 0), ("CONTROL", 0), ("TAKE OWNERSHIP", 0)]:
            if permission == "UPDATE" and table in mutable:
                predicates.append(" AND ".join(f"HAS_PERMS_BY_NAME(N'{name}',N'OBJECT',N'UPDATE',N'{column}',N'COLUMN')=1" for column in mutable[table]))
            else: predicates.append(f"HAS_PERMS_BY_NAME(N'{name}',N'OBJECT',N'{permission}')={expected}")
        columns = " AND c.name NOT IN(" + ",".join("N'" + column + "'" for column in mutable[table]) + ")" if table in mutable else ""
        predicates.append(f"NOT EXISTS(SELECT 1 FROM sys.columns c WHERE c.object_id=OBJECT_ID(N'{name}'){columns}"
            f" AND ISNULL(HAS_PERMS_BY_NAME(N'{name}',N'OBJECT',N'UPDATE',c.name,N'COLUMN'),1)<>0)")
    checks = [f"SELECT N'group_permission_{index:03}' AS CheckId,CASE WHEN({predicate}) THEN N'PASS' ELSE N'FAIL_OR_UNKNOWN' END AS Result"
        for index, predicate in enumerate(predicates, start=1)]
    return "EXECUTE AS LOGIN=N'aioffice_runtime'; " + " UNION ALL ".join(checks) + "; REVERT;"


def verify(*, directory, manifest, compose, environment, api, auth=None):
    require_owned(directory, api)  # Before private configuration, files or processes.
    if not auth or set(auth) != {"Authorization", "X-AIOffice-Company-Id"}:
        raise RuntimeError("Group source proof requires the owned issued portal identity.")
    tenant, company = (str(uuid.UUID(manifest[key])) for key in ("AIOFFICE_TENANT_ID", "AIOFFICE_COMPANY_ID"))
    owner = str(uuid.UUID(manifest["AIOFFICE_USER_ID"]))
    assert auth["X-AIOffice-Company-Id"] == company
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

    def read_route(route, *, headers=None):
        request = urllib.request.Request(api + route, headers=auth if headers is None else headers)
        try: response = urllib.request.urlopen(request, timeout=20)
        except urllib.error.HTTPError as error: response = error
        with response:
            value = response.read(65537)
            assert len(value) <= 65536 and "no-store" in response.headers.get("Cache-Control", ""), "Invalid private group read response"
            return response.status, json.loads(value) if value else None

    def read_source(message, *, headers=None, binding=None):
        return read_route(f"/api/group-sources/{binding or source}/messages/{message}", headers=headers)

    def require_private_denial(message, expected=403, **kwargs):
        status, value = read_source(message, **kwargs)
        assert status == expected, f"Group private read denial expectedHTTP{expected}, gotHTTP{status}"
        if value is not None:
            assert set(value) == {"error"}, "Private denial exposed source fields"
            assert "secretref://" not in json.dumps(value) and "Owned original" not in json.dumps(value)

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

    def read_release_range(payload, original):
        # Hold only the final gap query. The real GET has already acquired its
        # post-key Serializable member/grant/message/winner locks at this point.
        gate = "dbo.GroupReadGate_" + uuid.uuid4().hex
        base_cursor = int(sql(f"SELECT CommittedSequence FROM aioffice.GroupSourceStates WHERE {scope};"))
        base_counts = counts()
        grant_query = f"SELECT * FROM aioffice.GroupReaderGrants WHERE {scope} AND UserId='{owner}'"

        def fingerprint(query):
            value = sql("SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),COALESCE(("
                + query + " FOR JSON PATH,INCLUDE_NULL_VALUES),N'[]'))),2);")
            assert re.fullmatch(r"[0-9A-F]{64}", value), "Invalid owned final read fingerprint"
            return value

        def prefix():
            result = [fingerprint(f"SELECT * FROM aioffice.{table} WHERE {scope} ORDER BY Id")
                for table in ("GroupMessages", "GroupCoverageGaps")]
            result += [fingerprint(f"SELECT * FROM aioffice.{table} WHERE {scope} AND CommittedSequence<={base_cursor} ORDER BY CommittedSequence")
                for table in ("GroupMessageRevisions", "GroupIngressOutbox")]
            qualified = scope.replace("TenantId", "r.TenantId").replace("CompanyId", "r.CompanyId").replace("BindingId", "r.BindingId")
            result.append(fingerprint(f"SELECT r.* FROM aioffice.GroupIngressReceipts r WHERE {qualified} AND EXISTS("
                "SELECT 1 FROM aioffice.GroupMessageRevisions v WHERE v.TenantId=r.TenantId AND v.CompanyId=r.CompanyId"
                f" AND v.BindingId=r.BindingId AND v.MessageId=r.MessageId AND v.Revision=r.Revision AND v.CommittedSequence<={base_cursor}) ORDER BY r.EventIdentityHash"))
            return result

        base_prefix, base_grant = prefix(), fingerprint(grant_query)
        recall_payload = event(message=payload["event"]["messageId"], kind=4, text="")
        query = f"""SET NOCOUNT ON; USE AIOfficeLocal; SET XACT_ABORT ON;
          UPDATE {gate} SET OwnerSpid=@@SPID; BEGIN TRANSACTION;
          SELECT COUNT(*) FROM aioffice.GroupCoverageGaps WITH(TABLOCKX,HOLDLOCK);
          DECLARE @deadline datetime2=DATEADD(second,25,SYSUTCDATETIME());
          WHILE (SELECT Released FROM {gate} WITH(READUNCOMMITTED))=0 AND SYSUTCDATETIME()<@deadline WAITFOR DELAY '00:00:00.050';
          COMMIT TRANSACTION;"""
        locker = executor = None
        gate_attempted = False
        failure = None
        pending_read = pending_recall = pending_revoke = None
        read_started = None

        def observed(predicate, seconds):
            deadline = time.monotonic() + seconds
            if read_started is not None: deadline = min(deadline, read_started + 6)
            while time.monotonic() < deadline:
                if predicate(): return
                if locker.poll() is not None or any(pending is not None and pending.done() for pending in (pending_read, pending_recall, pending_revoke)):
                    raise AssertionError("Owned final read ended before observed SQL fence")
                time.sleep(.05)
            raise AssertionError("Owned final read SQL fence was not observed")

        def on_table(table):
            return (f"l.resource_database_id=DB_ID() AND ((l.resource_type='OBJECT' AND l.resource_associated_entity_id=OBJECT_ID(N'aioffice.{table}')) OR"
                f" (l.resource_type IN('KEY','PAGE','RID','HOBT') AND l.resource_associated_entity_id IN(SELECT hobt_id FROM sys.partitions WHERE object_id=OBJECT_ID(N'aioffice.{table}'))))")

        def waiter(table, login, statement, modes, held_modes, key_only=False):
            # Match the exact waiting resource to a granted lock held by this
            # final reader, and identify the executing INSERT/UPDATE privately.
            return f"""SELECT COUNT(DISTINCT r.session_id) FROM sys.dm_exec_requests r
              JOIN sys.dm_exec_sessions s ON s.session_id=r.session_id
              JOIN sys.dm_tran_locks l ON l.request_session_id=r.session_id
              JOIN sys.dm_tran_locks held ON held.request_session_id=(SELECT ReaderSpid FROM {gate})
                AND held.resource_database_id=l.resource_database_id AND held.resource_type=l.resource_type
                AND held.resource_associated_entity_id=l.resource_associated_entity_id
                AND held.resource_description=l.resource_description AND held.request_status='GRANT'
              CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) text
              WHERE s.login_name=N'{login}' AND r.blocking_session_id=(SELECT ReaderSpid FROM {gate})
                AND r.wait_type LIKE N'LCK_M_%' AND l.request_status IN('WAIT','CONVERT') AND {on_table(table)}
                AND l.request_mode IN({modes}) AND held.request_mode IN({held_modes})
                {"AND l.resource_type='KEY'" if key_only else ""}
                AND SUBSTRING(text.text,r.statement_start_offset/2+1,
                  (CASE WHEN r.statement_end_offset=-1 THEN DATALENGTH(text.text) ELSE r.statement_end_offset END-r.statement_start_offset)/2+1)
                  LIKE N'%{statement}%{table}%';"""

        try:
            gate_attempted = True
            sql(f"CREATE TABLE {gate}(Released bit NOT NULL,OwnerSpid int NULL,ReaderSpid int NULL); INSERT {gate} VALUES(0,NULL,NULL);")
            locker = subprocess.Popen([*compose, "exec", "-T", "sql", "sh", "-c",
                'SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -I -b -m 1 -h -1 -W -i /dev/stdin'],
                stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, env=environment)
            locker.stdin.write(query); locker.stdin.close(); locker.stdin = None
            executor = ThreadPoolExecutor(max_workers=3)
            observed(lambda: sql(f"SELECT COUNT(*) FROM sys.dm_tran_locks WHERE request_session_id=(SELECT OwnerSpid FROM {gate})"
                " AND resource_database_id=DB_ID() AND resource_type='OBJECT' AND resource_associated_entity_id=OBJECT_ID(N'aioffice.GroupCoverageGaps') AND request_mode='X' AND request_status='GRANT';") == "1", 8)
            read_started = time.monotonic()
            pending_read = executor.submit(read_source, original["messageId"])
            reader_query = f"""SELECT r.session_id FROM sys.dm_exec_requests r JOIN sys.dm_exec_sessions s ON s.session_id=r.session_id
              JOIN sys.dm_tran_locks l ON l.request_session_id=r.session_id
              WHERE s.login_name=N'aioffice_runtime' AND s.transaction_isolation_level=4
                AND r.blocking_session_id=(SELECT OwnerSpid FROM {gate}) AND r.wait_type LIKE N'LCK_M_%'
                AND l.request_status IN('WAIT','CONVERT') AND {on_table('GroupCoverageGaps')}"""
            observed(lambda: sql(f"SELECT COUNT(*) FROM ({reader_query}) q;") == "1", 3)
            sql(f"UPDATE {gate} SET ReaderSpid=({reader_query});")
            pending_recall = executor.submit(call, recall_payload)
            pending_revoke = executor.submit(sql, f"UPDATE aioffice.GroupReaderGrants SET IsEnabled=0 WHERE {scope} AND UserId='{owner}';")
            observed(lambda: int(sql(waiter("GroupMessageRevisions", "aioffice_runtime", "INSERT", "'RangeI-N'", "'RangeS-S','RangeS-U'", True))) == 1, 3)
            observed(lambda: int(sql(waiter("GroupReaderGrants", "sa", "UPDATE", "'X','U','IX'", "'S','RangeS-S','RangeS-U'"))) == 1, 3)
            assert not pending_read.done() and not pending_recall.done() and not pending_revoke.done()
            sql(f"UPDATE {gate} SET Released=1;")
            status, view = pending_read.result(timeout=10)
            assert status == 200 and view["text"] == payload["text"] and view["revision"] == 1
            assert sql(f"SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE session_id=(SELECT ReaderSpid FROM {gate}) AND transaction_isolation_level=2 AND open_transaction_count=0;") == "1"
            receipt = require_receipt(*pending_recall.result(timeout=10)); pending_revoke.result(timeout=10)
            assert receipt["messageId"] == original["messageId"] and receipt["revision"] == 2 and receipt["committedSequence"] == base_cursor + 1
            require_private_denial(original["messageId"])
        except BaseException as error:
            failure = error
        finally:
            def attempt(action):
                nonlocal failure
                try: action()
                except BaseException as error:
                    if failure is None: failure = error

            def drain_locker():
                if locker is None: return
                if locker.stdin is not None:
                    attempt(lambda: locker.stdin.close())
                    locker.stdin = None
                try: locker.communicate(timeout=8)
                except BaseException as error:
                    attempt(locker.kill)
                    attempt(lambda: locker.communicate(timeout=5))
                    raise error

            # Every resource gets its own cleanup attempt. Preserve the first
            # failure while still draining and restoring exact owned state.
            if gate_attempted: attempt(lambda: sql(f"UPDATE {gate} SET Released=1;"))
            attempt(drain_locker)
            if executor is not None: attempt(lambda: executor.shutdown(wait=True, cancel_futures=True))
            attempt(lambda: sql(f"UPDATE aioffice.GroupReaderGrants SET IsEnabled=1 WHERE {scope} AND UserId='{owner}';"))
            if gate_attempted: attempt(lambda: sql(f"DROP TABLE IF EXISTS {gate};"))
        if failure is not None: raise failure
        assert locker.returncode == 0
        status, view = read_source(original["messageId"])
        assert status == 200 and view["kind"] == 4 and view["text"] is None and view["revision"] == 2
        assert counts() == [base_counts[0], base_counts[1] + 1, base_counts[2] + 1, base_counts[3] + 1]
        assert int(sql(f"SELECT CommittedSequence FROM aioffice.GroupSourceStates WHERE {scope};")) == base_cursor + 1
        qualified = scope.replace("TenantId", "v.TenantId").replace("CompanyId", "v.CompanyId").replace("BindingId", "v.BindingId")
        assert sql(f"""SELECT COUNT(*) FROM aioffice.GroupMessageRevisions v
          JOIN aioffice.GroupIngressReceipts r ON r.TenantId=v.TenantId AND r.CompanyId=v.CompanyId AND r.BindingId=v.BindingId
            AND r.MessageId=v.MessageId AND r.Revision=v.Revision
          JOIN aioffice.GroupIngressOutbox o ON o.TenantId=v.TenantId AND o.CompanyId=v.CompanyId AND o.BindingId=v.BindingId
            AND o.MessageId=v.MessageId AND o.Revision=v.Revision AND o.CommittedSequence=v.CommittedSequence
          WHERE {qualified} AND v.MessageId='{original['messageId']}' AND v.Revision=2 AND v.CommittedSequence={base_cursor + 1} AND v.Kind=4
            AND CONVERT(varbinary(max),v.ExternalRevisionEventId)=CONVERT(varbinary(max),N'{recall_payload['event']['revisionEventId']}')
            AND CONVERT(varbinary(max),r.ExternalRevisionEventId)=CONVERT(varbinary(max),v.ExternalRevisionEventId);""") == "1", "Final read race lost exact Recall receipt/revision/outbox link"
        assert prefix() == base_prefix and fingerprint(grant_query) == base_grant, "Final read race altered retained source/grant bytes"
        committed = snapshot()
        replay = require_receipt(*call(recall_payload))
        assert replay["wasAlreadyCommitted"] and replay == {**receipt, "wasAlreadyCommitted": True} and snapshot() == committed
        print("PASS actual group post-key Serializable read release holds winner insert range and current ReaderGrant through commit; observed recall/revoke wait, fresh denial/restored Recall-null")

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
          INSERT {schema}GroupReaderGrants(TenantId,CompanyId,UserId,BindingId,Version,IsEnabled)
          VALUES ('{tenant}','{company}','{owner}','{source}',1,1);
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
        first_status, first_value = call(payload)
        if first_status != 200:
            # Only fixed server phase labels escape the private container log.
            diagnostic = subprocess.run([*compose, "logs", "--no-color", "--tail", "80", "core-api"],
                capture_output=True, text=True, env=environment, timeout=20)
            for category, phase in re.findall(r"group-(auth|store)-refusal: ([a-z-]{1,32})", diagnostic.stdout + diagnostic.stderr):
                print("Owned group refusal phase:" + category + "/" + phase)
            for line in sql(permission_diagnostic_query()).splitlines():
                if re.fullmatch(r"group_permission_\d{3}\s+FAIL_OR_UNKNOWN", line.strip()): print(line.strip())
        original = require_receipt(first_status, first_value)
        assert counts() == [1, 1, 1, 1] and original["committedSequence"] == original["revision"] == 1
        first_snapshot = snapshot()
        assert sql(f"SELECT COUNT(*) FROM {schema}GroupMessageRevisions WHERE {scope} AND ContentKeyId='owned-source-v1'"
            " AND DATALENGTH(ProtectedContent)>29 AND SUBSTRING(ProtectedContent,1,1)=0x01;") == "1", "Source protection envelope was not committed"
        # Mixed table GRANT/immutable-column DENY must permit only the intended
        # runtime fields. Execute real rolled-back UPDATEs as the runtime login,
        # then test both loss of an allowed column and escalation of a key.
        assert sql("EXECUTE AS LOGIN=N'aioffice_runtime'; BEGIN TRANSACTION;"
            f" UPDATE {schema}GroupListenerLeases SET HeartbeatAtUtc=HeartbeatAtUtc WHERE {registry_scope} AND ConnectorAccountId='{account}';"
            f" UPDATE {schema}GroupSourceStates SET CommittedSequence=CommittedSequence WHERE {scope};"
            f" UPDATE {schema}GroupCoverageGaps SET ReconnectedAtUtc=ReconnectedAtUtc WHERE {scope};"
            f" UPDATE {schema}GroupIngressOutbox SET PublishAttempts=PublishAttempts WHERE {scope};"
            " ROLLBACK TRANSACTION; REVERT; SELECT N'ALLOWED';") == "ALLOWED"
        assert snapshot() == first_snapshot
        sql("DENY UPDATE ON OBJECT::aioffice.GroupIngressOutbox(PublishedAtUtc) TO aioffice_runtime;")
        try: no_effect(event())
        finally: sql("REVOKE UPDATE ON OBJECT::aioffice.GroupIngressOutbox(PublishedAtUtc) FROM aioffice_runtime;")
        sql("REVOKE UPDATE ON OBJECT::aioffice.GroupSourceStates(BindingId) FROM aioffice_binding_runtime;"
            " GRANT UPDATE ON OBJECT::aioffice.GroupSourceStates(BindingId) TO aioffice_runtime;")
        try:
            assert sql("EXECUTE AS LOGIN=N'aioffice_runtime'; BEGIN TRANSACTION;"
                f" UPDATE {schema}GroupSourceStates SET BindingId=BindingId WHERE {scope};"
                " ROLLBACK TRANSACTION; REVERT; SELECT N'ALLOWED';") == "ALLOWED"
            no_effect(event())
        finally:
            sql("REVOKE UPDATE ON OBJECT::aioffice.GroupSourceStates(BindingId) FROM aioffice_runtime;"
                " DENY UPDATE ON OBJECT::aioffice.GroupSourceStates(BindingId) TO aioffice_binding_runtime;")
        assert require_receipt(*call(payload)) == {**original, "wasAlreadyCommitted": True} and snapshot() == first_snapshot
        print("PASS actual group precise mutable-column writes, missing writable permission and immutable-key escalation refusal/restored positive")
        # Concurrent calls are separately signed. They must reconcile one
        # logical event without renewing pending anchors or changing any bytes.
        with ThreadPoolExecutor(max_workers=8) as executor:
            repeated = list(executor.map(lambda _: call(payload), range(100)))
        for result in repeated:
            receipt = require_receipt(*result)
            assert receipt == {**original, "wasAlreadyCommitted": True}, "Duplicate event receipt changed"
        assert snapshot() == first_snapshot and counts() == [1, 1, 1, 1]
        print("PASS actual group HTTP HMAC/SQL scope/protected atomic graph and concurrent100 one-receipt replay with unchanged pending/outbox")

        read_status, private = read_source(original["messageId"], headers={**auth, "X-AIOffice-Tenant-Id": str(uuid.uuid4()), "X-AIOffice-User-Id": str(uuid.uuid4())})
        assert read_status == 200, f"Current explicit group reader expectedHTTP200, gotHTTP{read_status}"
        assert private["source"] == original["source"] and private["messageId"] == original["messageId"]
        assert private["externalMessageId"] == payload["event"]["messageId"] and private["text"] == payload["text"] and private["kind"] == 1
        assert set(private) == {"source", "messageId", "externalMessageId", "revision", "committedSequence", "kind", "senderId", "replyToMessageId", "occurredAtUtc", "text", "isHistoricalBackfill", "hasCoverageGap"}
        require_private_denial(original["messageId"], 401, headers={"X-AIOffice-Company-Id": company, "X-AIOffice-Group-Service": service, "X-AIOffice-Group-Signature": "A"*64})
        require_private_denial(original["messageId"], headers={**auth, "X-AIOffice-Company-Id": str(uuid.uuid4())})
        require_private_denial(original["messageId"], binding=str(uuid.uuid4()))
        assert read_source(str(uuid.uuid4()))[0] == 404
        sql(f"UPDATE {schema}GroupReaderGrants SET IsEnabled=0 WHERE {scope} AND UserId='{owner}';")
        try: require_private_denial(original["messageId"])
        finally: sql(f"UPDATE {schema}GroupReaderGrants SET IsEnabled=1 WHERE {scope} AND UserId='{owner}';")
        assert read_source(original["messageId"])[0] == 200 and snapshot() == first_snapshot
        sources_status, source_page = read_route("/api/group-sources?limit=1")
        assert sources_status == 200 and source_page == {"companyId": company, "items": [{"source": original["source"],
            "displayName": "Owned CI source", "provider": "synthetic", "version": 1}], "hasMore": False}
        messages_status, message_page = read_route(f"/api/group-sources/{source}/messages?limit=1")
        assert messages_status == 200 and message_page["source"] == original["source"] and message_page["nextBeforeSequence"] is None
        assert len(message_page["items"]) == 1 and message_page["items"][0]["messageId"] == original["messageId"] and message_page["items"][0]["lastChangedSequence"] == 1
        assert all(set(item) == {"messageId", "revision", "lastChangedSequence", "kind", "occurredAtUtc", "isHistoricalBackfill"} for item in message_page["items"])
        assert payload["text"] not in json.dumps(message_page) and snapshot() == first_snapshot
        print("PASS actual issued portal current explicit group read, exact protected Unicode, HMAC-only/foreign scope/grant denial and restored private positive without writes")

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
        history_lines = sql(f"DECLARE @history nvarchar(max)=(SELECT r.Revision,r.Kind,r.ContentSha256,r.IsHistoricalBackfill FROM {schema}GroupMessageRevisions r"
            f" JOIN {schema}GroupMessages m ON m.TenantId=r.TenantId AND m.CompanyId=r.CompanyId AND m.BindingId=r.BindingId AND m.Id=r.MessageId"
            f" WHERE r.TenantId='{tenant}' AND r.CompanyId='{company}' AND r.BindingId='{source}' AND m.ExternalMessageId=N'{message}' ORDER BY r.Revision FOR JSON PATH);"
            " SELECT CONVERT(nvarchar(4000),CASE WHEN DATALENGTH(@history)<=8000 THEN @history END); SELECT DATALENGTH(@history);").splitlines()
        assert len(history_lines) == 2, "Invalid owned group history transport"
        history = decode_bounded_history(history_lines[0].strip(), int(history_lines[1]))
        assert history == [{"Revision": index, "Kind": kind, "ContentSha256": hashlib.sha256(text.encode()).hexdigest().upper(), "IsHistoricalBackfill": historical}
            for index, (kind, text, historical) in enumerate([(3, "Owned edit", False), (4, "", False), (1, "Late owned original", True)], start=1)]
        assert sql(f"SELECT COUNT(*) FROM {schema}GroupCoverageGaps WHERE {scope} AND Reason='original-message-unseen' AND AfterCommittedSequence={before_edit};") == "1"
        assert sql(f"SELECT COUNT(*) FROM {schema}GroupMessageRevisions WHERE {scope} AND IsHistoricalBackfill=1;") == "1"
        recalled_message = str(uuid.UUID(sql(f"SELECT Id FROM {schema}GroupMessages WHERE {scope} AND ExternalMessageId=N'{message}';")))
        recalled_status, recalled = read_source(recalled_message)
        assert recalled_status == 200 and recalled["kind"] == 4 and recalled["text"] is None and recalled["revision"] == 2 and recalled["hasCoverageGap"]
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
        # Execute shipping inbox while this invocation's exact private Core key
        # override is live. The child owns a separate real Code/S256 browser
        # session and returns only fixed diagnostics; no key is passed to Node.
        browser_before = snapshot()
        result = subprocess.run(["node", "scripts/smoke-browser-group-inbox.mjs", str(directory)],
            input=json.dumps({"sourceId": source, "messageId": original["messageId"],
                "sequence": original["committedSequence"], "text": payload["text"]}),
            capture_output=True, text=True, env=environment, timeout=240)
        for line in (result.stdout + result.stderr).splitlines():
            if line.startswith("PASS owned group Chromium ") or browser_failure_stage(line):
                print(line)
        assert result.returncode == 0, "Owned shipping group inbox browser gate failed"
        assert snapshot() == browser_before, "Read-only shipping inbox changed durable group bytes"
        assert owner_graph == sql("SELECT CONCAT((SELECT COUNT(*) FROM aioffice.Users),N'|',(SELECT COUNT(*) FROM aioffice.Tasks),N'|',"
            "(SELECT COUNT(*) FROM aioffice.TaskDispatches),N'|',(SELECT COUNT(*) FROM aioffice.TaskCheckpoints));")
        read_release_range(payload, original)
        listener_before = snapshot()
        listener_spec = importlib.util.spec_from_file_location("group_listener_proof", Path(__file__).with_name("smoke-group-listener.py"))
        listener_proof = importlib.util.module_from_spec(listener_spec)
        listener_spec.loader.exec_module(listener_proof)
        listener_proof.verify(directory=directory, api=api, tenant=tenant, company=company, user=owner,
            service=service, key=group_key, sql=sql, compose=compose, environment=environment,
            restart=lambda: compose_run("restart", "core-api", overridden=True), ready=ready,
            identity_index=identity_index, read_route=read_route)
        assert snapshot() == listener_before, "Separate listener proof changed retained source bytes"
        spool_spec = importlib.util.spec_from_file_location("group_spool_proof", Path(__file__).with_name("smoke-group-spool.py"))
        spool_proof = importlib.util.module_from_spec(spool_spec)
        spool_spec.loader.exec_module(spool_proof)
        def enroll_owned_source(source_id, slot):
            # Separate owned synthetic source; preserve the original key row.
            # No production registry/configuration or credentials are touched.
            source_id = str(uuid.UUID(source_id))
            assert type(slot) is int and slot in (1, 2, 3, 4, 5, 6, 7, 8)
            prefix = f"AIOffice__GroupIntake__SourceKeys__{slot}__"
            assert not any(name.startswith(prefix) for name in private_environment)
            secret_name = {1: "OWNED_NATIVE_GROUP_CONTENT_KEY", 2: "OWNED_MANAGED_GROUP_CONTENT_KEY", 3: "OWNED_EFFECT_GROUP_CONTENT_KEY", 4: "OWNED_AUTOMATIC_GROUP_CONTENT_KEY", 5: "OWNED_NO_WORK_GROUP_CONTENT_KEY", 6: "OWNED_HOST_GROUP_CONTENT_KEY", 7: "OWNED_COVERAGE_GROUP_CONTENT_KEY", 8: "OWNED_RAW_HISTORY_GROUP_CONTENT_KEY"}[slot]
            private_environment[secret_name] = base64.b64encode(secrets.token_bytes(32)).decode("ascii")
            for name, value in {"TenantId": tenant, "CompanyId": company, "SourceBindingId": source_id,
                    "KeyId": "owned-managed-source-v1" if slot == 2 else "owned-native-source-v1", "SecretRef": "secretref://env/" + secret_name, "IsWriteKey": "true"}.items():
                private_environment[prefix + name] = value
            override.write_text(json.dumps({"services": {"core-api": {"environment": private_environment}}}), encoding="utf-8")
            override.chmod(0o600)
            compose_run("up", "-d", "--no-deps", "--force-recreate", "core-api", overridden=True)
            ready()
        spool_source = spool_proof.verify(directory=directory, api=api, tenant=tenant, company=company, service=service,
            key=group_key, sql=sql, restart=lambda: compose_run("restart", "core-api", overridden=True),
            ready=ready, identity_index=identity_index, enroll_source=lambda source_id: enroll_owned_source(source_id, 1))
        assert snapshot() == listener_before, "Separate native spool proof changed retained source bytes"
        assert owner_graph == sql("SELECT CONCAT((SELECT COUNT(*) FROM aioffice.Users),N'|',(SELECT COUNT(*) FROM aioffice.Tasks),N'|',"
            "(SELECT COUNT(*) FROM aioffice.TaskDispatches),N'|',(SELECT COUNT(*) FROM aioffice.TaskCheckpoints));")

        recovery_spec = importlib.util.spec_from_file_location("group_recovery_host_proof", Path(__file__).with_name("smoke-group-recovery-host.py"))
        recovery_proof = importlib.util.module_from_spec(recovery_spec)
        recovery_spec.loader.exec_module(recovery_proof)
        recovery_proof.verify(directory=directory, api=api, tenant=tenant, company=company, service=service,
            key=group_key, runtime_password=manifest["AIOFFICE_RUNTIME_PASSWORD"], sql=sql,
            restart=lambda: compose_run("restart", "core-api", overridden=True), ready=ready,
            identity_index=identity_index, enroll_source=lambda source_id: enroll_owned_source(source_id, 2))
        assert snapshot() == listener_before, "Separate managed recovery proof changed retained source bytes"
        assert owner_graph == sql("SELECT CONCAT((SELECT COUNT(*) FROM aioffice.Users),N'|',(SELECT COUNT(*) FROM aioffice.Tasks),N'|',"
            "(SELECT COUNT(*) FROM aioffice.TaskDispatches),N'|',(SELECT COUNT(*) FROM aioffice.TaskCheckpoints));")

        reference_spec = importlib.util.spec_from_file_location("group_reference_proof", Path(__file__).with_name("smoke-group-reference.py"))
        reference_proof = importlib.util.module_from_spec(reference_spec)
        reference_spec.loader.exec_module(reference_proof)

        def reference_pipeline(operation):
            worker_environment = {"AIOffice__GroupIntake__Enabled": "true", "AIOffice__GroupIntake__PipelineEnabled": "true"}
            for name, value in {"TenantId": tenant, "CompanyId": company, "ServiceId": service, "CredentialEpoch": "1"}.items():
                worker_environment["AIOffice__GroupIntake__Worker__" + name] = value
            if operation == "enable":
                override.write_text(json.dumps({"services": {
                    "core-api": {"environment": {**private_environment, **worker_environment}},
                    "agent-worker": {"environment": worker_environment}}}), encoding="utf-8")
                override.chmod(0o600)
                compose_run("up", "-d", "--no-deps", "--force-recreate", "core-api", "agent-worker", overridden=True)
                ready()
            elif operation == "restart":
                compose_run("restart", "core-api", "agent-worker", overridden=True)
                ready()
            elif operation == "disable":
                # Baseline Worker has no group switch. Keep the private Core
                # enrollment until the parent's existing final restoration.
                failure = None
                try:
                    override.write_text(json.dumps({"services": {"core-api": {"environment": private_environment}}}), encoding="utf-8")
                    override.chmod(0o600)
                    compose_run("up", "-d", "--no-deps", "--force-recreate", "core-api", overridden=True)
                except BaseException as error:
                    failure = error
                finally:
                    try: compose_run("up", "-d", "--no-deps", "--force-recreate", "agent-worker")
                    except BaseException as error:
                        if failure is None: failure = error
                if failure is not None: raise failure
                ready()
            else:
                raise RuntimeError("Owned reference pipeline operation refused.")

        def prepare_effect_source():
            spec = importlib.util.spec_from_file_location("owned_group_effect_fixture", Path(__file__).with_name("owned-group-effect-fixture.py"))
            effect_fixture = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(effect_fixture)
            effect_source, effect_events = effect_fixture.prepare(directory=directory, api=api,
                tenant=tenant, company=company, service=service, sql=sql, identity_index=identity_index,
                enroll_source=lambda source_id: enroll_owned_source(source_id, 3), post_event=call)
            return effect_source, effect_events, private_environment["OWNED_EFFECT_GROUP_CONTENT_KEY"]

        reference_proof.verify(directory=directory, api=api, manifest=manifest, tenant=tenant, company=company,
            service=service, source=spool_source, sql=sql, compose=compose, environment=environment, pipeline=reference_pipeline,
            source_key=private_environment["OWNED_NATIVE_GROUP_CONTENT_KEY"], prepare_effect_source=prepare_effect_source)
        automatic_spec = importlib.util.spec_from_file_location("owned_automatic_notes", Path(__file__).with_name("smoke-group-automatic-notes.py"))
        automatic_proof = importlib.util.module_from_spec(automatic_spec)
        automatic_spec.loader.exec_module(automatic_proof)
        def prepare_automatic_source():
            spec = importlib.util.spec_from_file_location("owned_automatic_effect_fixture", Path(__file__).with_name("owned-group-effect-fixture.py"))
            fixture = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(fixture)
            new_source, new_events = fixture.prepare_automatic(directory=directory, api=api,
                tenant=tenant, company=company, service=service, sql=sql, identity_index=identity_index,
                enroll_source=lambda source_id: enroll_owned_source(source_id, 4), post_event=call)
            return new_source, new_events, private_environment["OWNED_AUTOMATIC_GROUP_CONTENT_KEY"]
        automatic_proof.verify(directory=directory, api=api, manifest=manifest, tenant=tenant, company=company,
            service=service, sql=sql, prepare_source=prepare_automatic_source, reference=reference_proof)
        def prepare_no_work_source():
            spec = importlib.util.spec_from_file_location("owned_no_work_effect_fixture", Path(__file__).with_name("owned-group-effect-fixture.py"))
            fixture = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(fixture)
            new_source, new_events = fixture.prepare_no_work(directory=directory, api=api,
                tenant=tenant, company=company, service=service, sql=sql, identity_index=identity_index,
                enroll_source=lambda source_id: enroll_owned_source(source_id, 5), post_event=call)
            return new_source, new_events, private_environment["OWNED_NO_WORK_GROUP_CONTENT_KEY"]
        automatic_proof.verify(directory=directory, api=api, manifest=manifest, tenant=tenant, company=company,
            service=service, sql=sql, prepare_source=prepare_no_work_source, reference=reference_proof, proof="no_work")
        def prepare_host_only_source():
            spec = importlib.util.spec_from_file_location("owned_host_only_effect_fixture", Path(__file__).with_name("owned-group-effect-fixture.py"))
            fixture = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(fixture)
            new_source, new_events = fixture.prepare_host_only(directory=directory, api=api,
                tenant=tenant, company=company, service=service, sql=sql, identity_index=identity_index,
                enroll_source=lambda source_id: enroll_owned_source(source_id, 6), post_event=call)
            return new_source, new_events, private_environment["OWNED_HOST_GROUP_CONTENT_KEY"]
        automatic_proof.verify(directory=directory, api=api, manifest=manifest, tenant=tenant, company=company,
            service=service, sql=sql, prepare_source=prepare_host_only_source, reference=reference_proof, proof="host_only")
        def prepare_coverage_source():
            spec = importlib.util.spec_from_file_location("owned_coverage_fixture", Path(__file__).with_name("owned-group-effect-fixture.py"))
            fixture = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(fixture)
            new_source, new_events = fixture.prepare(directory=directory, api=api,
                tenant=tenant, company=company, service=service, sql=sql, identity_index=identity_index,
                enroll_source=lambda source_id: enroll_owned_source(source_id, 7), post_event=call)
            return new_source, new_events, private_environment["OWNED_COVERAGE_GROUP_CONTENT_KEY"]
        automatic_proof.verify(directory=directory, api=api, manifest=manifest, tenant=tenant, company=company,
            service=service, sql=sql, prepare_source=prepare_coverage_source, reference=reference_proof, proof="coverage")
        raw_history_spec = importlib.util.spec_from_file_location("owned_raw_history", Path(__file__).with_name("smoke-group-raw-history.py"))
        raw_history_proof = importlib.util.module_from_spec(raw_history_spec)
        raw_history_spec.loader.exec_module(raw_history_proof)
        def prepare_raw_history_source():
            spec = importlib.util.spec_from_file_location("owned_raw_history_fixture", Path(__file__).with_name("owned-group-effect-fixture.py"))
            fixture = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(fixture)
            new_source, new_events = fixture.prepare_raw_history(directory=directory, api=api,
                tenant=tenant, company=company, service=service, sql=sql, identity_index=identity_index,
                enroll_source=lambda source_id: enroll_owned_source(source_id, 8), post_event=call)
            return new_source, new_events, private_environment["OWNED_RAW_HISTORY_GROUP_CONTENT_KEY"]
        raw_history_proof.verify(directory=directory, api=api, manifest=manifest, tenant=tenant, company=company,
            service=service, sql=sql, prepare_source=prepare_raw_history_source, reference=reference_proof, automatic=automatic_proof)
        assert sql(f"SELECT CONCAT(COUNT(*),N'|',COUNT(DISTINCT BatchId),N'|',COUNT(DISTINCT OperationId)) "
            f"FROM aioffice.GroupWorkCommitReceipts WHERE TenantId='{tenant}' AND CompanyId='{company}' "
            "AND DependencyManifestVersion=1 AND DATALENGTH(DependencyManifest)=274 AND SelectedMessageCount=2 "
            "AND SUBSTRING(DependencyManifest,1,8)=0x41494F4744455031 "
            "AND SUBSTRING(DependencyManifest,161,2)=0x0200;") == "4|4|4"
        print("PASS actual automatic manifest SQL four original version1 receipts exact scoped source identities cutoff metadata same savepoint rollback original replay immutable both columns no terminal or model claim", flush=True)
        print("PASS actual automatic whole dependency SQL four original scopes each three current manifest reconstructions serializable source lock unchanged graphs claim key counts pending501 no completion or model", flush=True)
        assert sql(f"SELECT CONCAT(COUNT(*),N'|',COUNT(DISTINCT BatchId),N'|',COUNT(DISTINCT OperationId)) "
            f"FROM aioffice.GroupWorkCommitReceipts WHERE TenantId='{tenant}' AND CompanyId='{company}' "
            "AND DependencyManifestVersion=1 AND DATALENGTH(DependencyManifest)=274 AND SelectedMessageCount=2 "
            "AND EffectLedgerVersion=1 AND DATALENGTH(ExpectedEffectSha256)=32 AND ExpectedEffectSha256<>CONVERT(varbinary(32),REPLICATE(CHAR(0),32));") == "4|4|4"
        print("PASS actual automatic original effect expectations four original scopes version1 digest32 atomic rollback replay two immutable columns denied complete original graphs unchanged no completion or model", flush=True)
        print("PASS actual automatic original effect graph SQL four original scopes each three current digest comparisons original acquisition six effect tables ciphertext preflight original graphs claim keys pending501 unchanged no completion or model", flush=True)
        assert snapshot() == listener_before, "Separate reference proof changed retained original full6 source bytes"
        assert owner_graph == sql("SELECT CONCAT((SELECT COUNT(*) FROM aioffice.Users),N'|',(SELECT COUNT(*) FROM aioffice.Tasks),N'|',"
            "(SELECT COUNT(*) FROM aioffice.TaskDispatches),N'|',(SELECT COUNT(*) FROM aioffice.TaskCheckpoints));")

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
