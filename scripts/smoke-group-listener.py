"""Mandatory listener HTTP/SQL proof on an owned disposable CI account only."""
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timezone
import hashlib
import hmac
import json
import os
from pathlib import Path
import re
import subprocess
import time
import urllib.error
import urllib.request
import uuid


def require_owned(directory, api):
    if not (os.environ.get("CI") == "true" and os.environ.get("GITHUB_ACTIONS") == "true"
            and os.environ.get("RUNNER_TEMP") and directory.resolve() == (Path(os.environ["RUNNER_TEMP"]) / "aioffice-local").resolve()
            and api == "http://127.0.0.1:8080"):
        raise RuntimeError("Listener proof requires the owned disposable GitHub CI fixture.")


def signing_bytes(service, epoch, signed_at, nonce, body):
    return (f"aioffice-group-listener-v1\n{uuid.UUID(service)}\n{epoch}\n{signed_at}\n{uuid.UUID(nonce)}\n"
        + hashlib.sha256(body).hexdigest().upper()).encode("ascii")


def verify(*, directory, api, tenant, company, user, service, key, sql, compose, environment,
        restart, ready, identity_index, read_route):
    require_owned(directory, api)  # Before any SQL, processes or HTTP.
    tenant, company, user, service = (str(uuid.UUID(value)) for value in (tenant, company, user, service))
    source, account, owner = (str(uuid.uuid4()) for _ in range(3))
    external = {"provider": "synthetic", "accountId": "listener-account-" + account,
        "groupId": "listener-group-" + source}
    registry = f"TenantId='{tenant}' AND CompanyId='{company}'"
    account_scope = registry + f" AND ConnectorAccountId='{account}'"
    source_scope = registry + f" AND BindingId='{source}'"
    literal = lambda value: "N'" + value.replace("'", "''") + "'"

    def command(operation=1, expected=0, process=None):
        return json.dumps({"identity": external, "command": {"ownerId": process or owner,
            "operation": operation, "expectedEpoch": expected}}, separators=(",", ":")).encode("utf-8")

    def call(body, *, nonce=None, signed_at=None, domain=None, epoch=1):
        nonce, signed_at = nonce or str(uuid.uuid4()), int(time.time()) if signed_at is None else signed_at
        signing = signing_bytes(service, epoch, signed_at, nonce, body)
        if domain is not None: signing = signing.replace(b"aioffice-group-listener-v1", domain)
        signature = hmac.new(key, signing, hashlib.sha256).hexdigest().upper()
        request = urllib.request.Request(api + "/internal/group-ingress/listener", data=body, method="POST", headers={
            "Content-Type": "application/json", "X-AIOffice-Group-Service": service,
            "X-AIOffice-Group-Epoch": str(epoch), "X-AIOffice-Group-Signed-At": str(signed_at),
            "X-AIOffice-Group-Nonce": nonce, "X-AIOffice-Group-Signature": signature,
            "X-Tenant-Id": str(uuid.uuid4()), "X-User-Id": str(uuid.uuid4())})
        try: response = urllib.request.urlopen(request, timeout=20)
        except urllib.error.HTTPError as error: response = error
        with response:
            captured = response.read(8193)
            assert len(captured) <= 8192 and "no-store" in response.headers.get("Cache-Control", ""), "Invalid listener transport"
            return response.status, json.loads(captured) if captured else None

    def timestamp(value):
        result = datetime.fromisoformat(value.replace("Z", "+00:00"))
        assert result.utcoffset().total_seconds() == 0, "Listener clock was not UTC"
        return result

    def receipt(status, value, *, process=owner, epoch=1, already=False, changed=True, coverage=True):
        assert status == 200, f"Listener committed ACK expected200, gotHTTP{status}"
        assert set(value) == {"lease", "changed", "coverageRecorded", "committedAtUtc", "wasAlreadyCommitted"}
        assert (value["changed"], value["coverageRecorded"], value["wasAlreadyCommitted"]) == (changed, coverage, already)
        lease = value["lease"]
        assert set(lease) == {"account", "ownerId", "epoch", "heartbeatAtUtc", "expiresAtUtc"}
        assert lease["account"] == {"tenantId": tenant, "companyId": company, "connectorAccountId": account}
        assert lease["ownerId"] == process and lease["epoch"] == epoch
        heartbeat, expiry, committed = (timestamp(value) for value in (lease["heartbeatAtUtc"], lease["expiresAtUtc"], value["committedAtUtc"]))
        assert 0 <= (expiry - heartbeat).total_seconds() <= 30 and committed >= heartbeat
        return value

    def snapshot():
        values = [sql(f"SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),COALESCE("
            f"(SELECT * FROM aioffice.{table} WHERE {account_scope} ORDER BY {order} FOR JSON PATH,INCLUDE_NULL_VALUES),N'[]'))),2);")
            for table, order in (("GroupListenerLeases", "Epoch"), ("GroupAccountCoverageGaps", "ListenerEpoch,Reason"),
                ("GroupListenerCommandReceipts", "CredentialEpoch,Nonce"))]
        assert all(re.fullmatch(r"[0-9A-F]{64}", value) for value in values)
        return values

    def counts():
        return [int(sql(f"SELECT COUNT(*) FROM aioffice.{table} WHERE {account_scope};")) for table in
            ("GroupListenerLeases", "GroupAccountCoverageGaps", "GroupListenerCommandReceipts")]

    def no_effect(body, expected=403, **kwargs):
        before = snapshot()
        status, value = call(body, **kwargs)
        assert status == expected and set(value) == {"error"}, f"Listener refusal expectedHTTP{expected}, gotHTTP{status}"
        assert "secretref://" not in value["error"] and snapshot() == before, "Listener refusal leaked authority or changed SQL bytes"

    def temporary_sql(setup, restore, action):
        failure = None
        try:
            sql(setup)
            action()
        except BaseException as error: failure = error
        finally:
            try: sql(restore)
            except BaseException as error:
                if failure is None: failure = error
        if failure is not None: raise failure

    renewal_count = 0

    def immutable_history(excluded_nonce):
        history_query = f"""SELECT CONCAT(
          CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),COALESCE(
            (SELECT * FROM aioffice.GroupAccountCoverageGaps WHERE {account_scope} ORDER BY ListenerEpoch,Reason FOR JSON PATH,INCLUDE_NULL_VALUES),N'[]'))),2),N'|',
          CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),COALESCE(
            (SELECT * FROM aioffice.GroupListenerCommandReceipts WHERE {account_scope} AND Nonce<>'{excluded_nonce}' ORDER BY CredentialEpoch,Nonce FOR JSON PATH,INCLUDE_NULL_VALUES),N'[]'))),2),N'|',
          (SELECT COUNT(*) FROM aioffice.GroupListenerCommandReceipts WHERE {account_scope}));"""
        value = sql(history_query)
        assert re.fullmatch(r"[0-9A-F]{64}\|[0-9A-F]{64}\|[0-9]+", value), "Invalid owned renewal history metadata"
        return value.split('|')

    def renew_owned(process=owner, epoch=1):
        # Deliberate foreground command, outside every unchanged-graph window.
        # Excluding ONLY this new nonce proves all prior receipt/gap bytes stay
        # identical; the total receipt count must increase by exactly one.
        nonlocal renewal_count
        renewal_body, renewal_nonce, renewal_signed_at = command(2, epoch, process), str(uuid.uuid4()), int(time.time())
        before = immutable_history(renewal_nonce)
        response = call(renewal_body, nonce=renewal_nonce, signed_at=renewal_signed_at)
        value = receipt(*response, process=process, epoch=epoch, coverage=False)
        assert (timestamp(value['lease']['expiresAtUtc']) - timestamp(value['lease']['heartbeatAtUtc'])).total_seconds() == 30
        after = immutable_history(renewal_nonce)
        assert after[:2] == before[:2] and int(after[2]) == int(before[2]) + 1, "Renew changed historical receipt/gap bytes or appended other effects"
        renewal_count += 1
        return renewal_body, renewal_nonce, renewal_signed_at, value

    def renew_and_replay():
        fresh_body, fresh_nonce, fresh_signed_at, fresh = renew_owned()
        before = snapshot()
        assert receipt(*call(fresh_body, nonce=fresh_nonce, signed_at=fresh_signed_at), coverage=False, already=True) == {**fresh, 'wasAlreadyCommitted': True}
        assert snapshot() == before, "Restored live renewal replay changed SQL bytes"

    def timing(captured_nonce, captured_signed_at):
        # One server-clock snapshot, fixed boolean metadata only. Never log
        # fixture identities, timestamps, request bodies or credentials.
        flags = sql(f"""DECLARE @now datetimeoffset=TODATETIMEOFFSET(SYSUTCDATETIME(),'+00:00');
          SELECT CONCAT(CASE WHEN r.ExpiresAtUtc<=@now THEN 1 ELSE 0 END,N'|',
            CASE WHEN l.ExpiresAtUtc>@now AND l.OwnerId=r.OwnerId AND l.Epoch=r.ListenerEpoch THEN 1 ELSE 0 END,N'|',
            CASE WHEN ABS(DATEDIFF_BIG(millisecond,DATEADD(second,{captured_signed_at},CONVERT(datetime2,'19700101')),@now))<90000 THEN 1 ELSE 0 END)
          FROM aioffice.GroupListenerCommandReceipts r JOIN aioffice.GroupListenerLeases l
            ON l.TenantId=r.TenantId AND l.CompanyId=r.CompanyId AND l.ConnectorAccountId=r.ConnectorAccountId
          WHERE r.TenantId='{tenant}' AND r.CompanyId='{company}' AND r.ConnectorAccountId='{account}'
            AND r.ServiceId='{service}' AND r.CredentialEpoch=1 AND r.Nonce='{captured_nonce}';""")
        assert re.fullmatch(r"[01]\|[01]\|[01]", flags), "Invalid owned listener timing metadata"
        print("INFO owned listener timing expired-receipt/live-lease/fresh-signature=" + flags)
        return flags

    qualification = {"environment": 1, "observations": [{"capability": capability, "support": 1,
        "evidenceId": str(uuid.uuid4()), "observedAtUtc": datetime.now(timezone.utc).isoformat()} for capability in (8, 9)]}
    # Enroll a separate disposable account. Retained ingress's manual fixture
    # lease is deliberately not rewritten/adopted as listener authority.
    sql(f"""INSERT aioffice.GroupConnectorAccounts
      (TenantId,CompanyId,Id,Provider,ExternalAccountId,IdentityHash,PackageVersion,GitCommit,QualificationJson,Version,IsEnabled)
      VALUES ('{tenant}','{company}','{account}','synthetic',{literal(external['accountId'])},
      '{identity_index('synthetic',external['accountId'],'account-registry')}','owned-fixture','{'0'*40}',{literal(json.dumps(qualification))},1,1);
      INSERT aioffice.GroupBindings(TenantId,CompanyId,Id,ConnectorAccountId,Role,Provider,ExternalAccountId,ExternalGroupId,IdentityHash,PhysicalGroupHash,DisplayName,Version,DeletionGeneration,IsEnabled)
      VALUES ('{tenant}','{company}','{source}','{account}',1,'synthetic',{literal(external['accountId'])},{literal(external['groupId'])},
      '{identity_index('synthetic',external['accountId'],external['groupId'])}',
      '{identity_index('synthetic','physical-group-registry',external['groupId'])}',N'Owned listener source',1,0,1);
      INSERT aioffice.GroupServiceGrants(TenantId,CompanyId,ServiceId,BindingId,Capability,Version,IsEnabled)
      VALUES ('{tenant}','{company}','{service}','{source}',1,1,1);
      INSERT aioffice.GroupReaderGrants(TenantId,CompanyId,UserId,BindingId,Version,IsEnabled)
      VALUES ('{tenant}','{company}','{user}','{source}',1,1);""")
    assert counts() == [0, 0, 0]
    body, nonce, signed_at = command(), str(uuid.uuid4()), int(time.time())
    original = receipt(*call(body, nonce=nonce, signed_at=signed_at))
    assert counts() == [1, 1, 1]
    assert sql(f"SELECT COUNT(*) FROM aioffice.GroupListenerCommandReceipts WHERE {account_scope} AND ServiceId='{service}'"
        f" AND CredentialEpoch=1 AND Nonce='{nonce}' AND CommandSha256='{hashlib.sha256(body).hexdigest().upper()}'"
        " AND Operation=1 AND ListenerEpoch=1 AND Changed=1 AND CoverageRecorded=1;") == "1"
    first = snapshot()
    status, heads = read_route(f"/api/group-sources/{source}/messages")
    assert status == 200 and heads == {"source": {"tenantId": tenant, "companyId": company, "sourceBindingId": source},
        "items": [], "nextBeforeSequence": None, "hasCoverageGap": True}, "Account startup uncertainty was not visible to the issued source reader"
    assert snapshot() == first
    with ThreadPoolExecutor(max_workers=20) as executor:
        responses = list(executor.map(lambda _: call(body, nonce=nonce, signed_at=signed_at), range(100)))
    assert all(receipt(*result, already=True) == {**original, "wasAlreadyCommitted": True} for result in responses)
    assert snapshot() == first and counts() == [1, 1, 1], "Concurrent listener replay extended or duplicated SQL effects"
    print("PASS actual listener separate-domain scoped commit-only ACK and concurrent100 exact nonce replay preserve original lease/gap/receipt bytes")

    renew_owned()
    no_effect(command(2, 1), 409, nonce=nonce)
    renew_owned()
    no_effect(command(process=str(uuid.uuid4())))
    renew_owned()
    no_effect(command(2, 2))
    renew_owned()
    no_effect(command(), domain=b"aioffice-group-ingest-v1")
    renew_owned()
    no_effect(command(), epoch=2)
    renew_owned()  # Before the slow permission phase; never during escalation.
    column_permission = "SELECT state FROM sys.database_permissions WHERE class=1 AND major_id=OBJECT_ID(N'aioffice.GroupListenerCommandReceipts')"
    column_permission += " AND minor_id=COLUMNPROPERTY(OBJECT_ID(N'aioffice.GroupListenerCommandReceipts'),N'CommandSha256',N'ColumnId')"
    column_permission += " AND grantee_principal_id=DATABASE_PRINCIPAL_ID(N'aioffice_binding_runtime') AND permission_name=N'UPDATE';"
    # SQL catalog omits a redundant column DENY equal to its object DENY.
    # Capture the exception row separately; effective permission remains the
    # causal oracle, never inferred merely from a catalog row's presence.
    original_column_permission = sql(column_permission)
    table_permission = column_permission.replace("minor_id=COLUMNPROPERTY(OBJECT_ID(N'aioffice.GroupListenerCommandReceipts'),N'CommandSha256',N'ColumnId')", "minor_id=0")
    assert sql(table_permission) == "D" and original_column_permission in ("", "D"), "Owned receipt permission baseline was unsafe or unexpected"
    direct_permission = column_permission.replace("SELECT state", "SELECT COUNT(*)").replace("N'aioffice_binding_runtime'", "N'aioffice_runtime'")
    assert sql(direct_permission) == "0", "Owned receipt column had an unexpected direct runtime override"
    effective_column_permission = "EXECUTE AS LOGIN=N'aioffice_runtime'; SELECT HAS_PERMS_BY_NAME(N'aioffice.GroupListenerCommandReceipts',N'OBJECT',N'UPDATE',N'CommandSha256',N'COLUMN'); REVERT;"
    assert sql(effective_column_permission) == "0"
    def refuse_unsafe_column():
        assert sql(effective_column_permission) == "1", "Owned column escalation did not actually change effective runtime rights"
        no_effect(command())
    temporary_sql("GRANT UPDATE ON OBJECT::aioffice.GroupListenerCommandReceipts(CommandSha256) TO aioffice_binding_runtime;",
        "DENY UPDATE ON OBJECT::aioffice.GroupListenerCommandReceipts(CommandSha256) TO aioffice_binding_runtime;", refuse_unsafe_column)
    assert sql(table_permission) == "D" and sql(column_permission) == original_column_permission and sql(direct_permission) == "0"
    assert sql(effective_column_permission) == "0"
    timing(nonce, signed_at)
    renew_and_replay()  # Fresh original Renew receipt after rights restoration.

    # The second Save is forced to fail after a real Stop lease mutation and
    # coverage staging. Only SQL transaction rollback can preserve all3 tables.
    renew_owned()  # Keep the real Stop mutation live throughout rollback proof.
    assert renewal_count == 8
    stop_body, stop_nonce = command(3, 1), str(uuid.uuid4())
    constraint = "CK_CiListenerRollback_" + uuid.uuid4().hex
    temporary_sql(f"ALTER TABLE aioffice.GroupListenerCommandReceipts ADD CONSTRAINT {constraint} CHECK(Nonce<>'{stop_nonce}');",
        f"IF EXISTS(SELECT 1 FROM sys.check_constraints WHERE name=N'{constraint}' AND parent_object_id=OBJECT_ID(N'aioffice.GroupListenerCommandReceipts'))"
        f" ALTER TABLE aioffice.GroupListenerCommandReceipts DROP CONSTRAINT {constraint};", lambda: no_effect(stop_body, 503, nonce=stop_nonce))
    stopped = receipt(*call(stop_body, nonce=stop_nonce))
    assert counts() == [1, 2, 2 + renewal_count]
    no_effect(body, nonce=nonce)
    stopped_snapshot = snapshot()
    restart(); ready()
    assert receipt(*call(stop_body, nonce=stop_nonce), already=True) == {**stopped, "wasAlreadyCommitted": True}
    assert snapshot() == stopped_snapshot
    second_owner, second_nonce, second_signed_at = str(uuid.uuid4()), str(uuid.uuid4()), int(time.time())
    second_body = command(process=second_owner)
    second = receipt(*call(second_body, nonce=second_nonce, signed_at=second_signed_at), process=second_owner, epoch=2)
    assert counts() == [1, 3, 3 + renewal_count]
    no_effect(command(2, 1))
    print("PASS actual listener SQL second-save failure atomically rolls back lease/gap/receipt; stable restored stop/restart ACK and monotonic owner epoch")

    def queued_revoke(revoke, restore):
        gate = "dbo.ListenerGate_" + uuid.uuid4().hex
        resource = f"aioffice:group-listener:{uuid.UUID(tenant).hex}/{uuid.UUID(company).hex}/{uuid.UUID(account).hex}"
        locker = executor = pending = None
        before = snapshot()
        failure = None
        try:
            sql(f"CREATE TABLE {gate}(Released bit NOT NULL,OwnerSpid int NULL); INSERT {gate} VALUES(0,NULL);")
            query = f"""SET NOCOUNT ON; USE AIOfficeLocal; SET XACT_ABORT ON;
              UPDATE {gate} SET OwnerSpid=@@SPID; BEGIN TRANSACTION;
              DECLARE @result int; EXEC @result=sys.sp_getapplock @Resource=N'{resource}',@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=0;
              IF @result<0 THROW 51000,'Owned listener gate unavailable.',1;
              DECLARE @deadline datetime2=DATEADD(second,25,SYSUTCDATETIME());
              WHILE (SELECT Released FROM {gate} WITH(READUNCOMMITTED))=0 AND SYSUTCDATETIME()<@deadline WAITFOR DELAY '00:00:00.050';
              COMMIT TRANSACTION;"""
            locker = subprocess.Popen([*compose, "exec", "-T", "sql", "sh", "-c",
                'SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -I -b -m 1 -h -1 -W -i /dev/stdin'],
                stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, env=environment)
            locker.stdin.write(query); locker.stdin.close(); locker.stdin = None
            executor = ThreadPoolExecutor(max_workers=1)

            def wait(predicate, seconds):
                deadline = time.monotonic() + seconds
                while time.monotonic() < deadline:
                    if predicate(): return
                    if locker.poll() is not None or pending is not None and pending.done(): raise AssertionError("Listener fence ended before observation")
                    time.sleep(.05)
                raise AssertionError("Listener fence observation deadline")

            wait(lambda: int(sql(f"SELECT COUNT(*) FROM sys.dm_tran_locks WHERE request_session_id=(SELECT OwnerSpid FROM {gate})"
                " AND resource_type=N'APPLICATION' AND resource_database_id=DB_ID() AND request_status=N'GRANT' AND request_mode=N'X';")) > 0, 8)
            pending = executor.submit(call, command(2, 2, second_owner))
            wait(lambda: int(sql(f"""SELECT COUNT(*) FROM sys.dm_exec_requests r
              JOIN sys.dm_exec_sessions s ON s.session_id=r.session_id
              JOIN sys.dm_tran_locks l ON l.request_session_id=r.session_id
              JOIN sys.dm_tran_locks held ON held.request_session_id=(SELECT OwnerSpid FROM {gate})
                AND held.resource_type=l.resource_type AND held.resource_database_id=l.resource_database_id
                AND held.resource_description=l.resource_description AND held.request_status=N'GRANT' AND held.request_mode=N'X'
              WHERE s.login_name=N'aioffice_runtime' AND r.blocking_session_id=(SELECT OwnerSpid FROM {gate})
                AND s.transaction_isolation_level=4 AND r.wait_type=N'LCK_M_X' AND l.resource_type=N'APPLICATION'
                AND l.resource_database_id=DB_ID() AND l.request_mode=N'X' AND l.request_status IN(N'WAIT',N'CONVERT');""")) > 0, 3)
            sql(revoke + f"; UPDATE {gate} SET Released=1;")
            status, value = pending.result(timeout=15)
            assert status == 403 and set(value) == {"error"} and snapshot() == before, "Queued listener consumed stale authority or changed SQL bytes"
        except BaseException as error: failure = error
        finally:
            def attempt(action):
                nonlocal failure
                try: action()
                except BaseException as error:
                    if failure is None: failure = error
            attempt(lambda: sql(f"IF OBJECT_ID(N'{gate}',N'U') IS NOT NULL UPDATE {gate} SET Released=1;"))
            if locker is not None:
                if locker.stdin is not None:
                    attempt(locker.stdin.close); locker.stdin = None
                try: locker.communicate(timeout=8)
                except BaseException as error:
                    if failure is None: failure = error
                    attempt(locker.kill); attempt(lambda: locker.communicate(timeout=8))
            if executor is not None: attempt(lambda: executor.shutdown(wait=True, cancel_futures=True))
            attempt(lambda: sql(restore)); attempt(lambda: sql(f"DROP TABLE IF EXISTS {gate};"))
        if failure is not None: raise failure
        assert snapshot() == before
        return receipt(*call(command(2, 2, second_owner)), process=second_owner, epoch=2, coverage=False)

    for name, table, where, revoke, restore in (
            ("source", "GroupBindings", registry + f" AND Id='{source}'", "IsEnabled=0", "IsEnabled=1"),
            ("grant", "GroupServiceGrants", source_scope + f" AND ServiceId='{service}' AND Capability=1", "IsEnabled=0", "IsEnabled=1"),
            ("credential", "GroupServices", registry + f" AND Id='{service}'", "CredentialEpoch=2", "CredentialEpoch=1"),
            ("account", "GroupConnectorAccounts", registry + f" AND Id='{account}'", "IsEnabled=0", "IsEnabled=1"),
            ("deletion", "GroupBindings", registry + f" AND Id='{source}'", "DeletionGeneration=1", "DeletionGeneration=0")):
        renew_owned(second_owner, 2)  # Outside the queued refusal graph window.
        fingerprint_query = f"SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),(SELECT * FROM aioffice.{table} WHERE {where} FOR JSON PATH,INCLUDE_NULL_VALUES))),2);"
        current_registry = sql(fingerprint_query)
        assert re.fullmatch(r"[0-9A-F]{64}", current_registry)
        queued_revoke(f"UPDATE aioffice.{table} SET {revoke} WHERE {where};", f"UPDATE aioffice.{table} SET {restore} WHERE {where};")
        assert sql(fingerprint_query) == current_registry, "Listener fence did not restore exact registry bytes"
        print("PASS actual listener observed Serializable account-lock " + name + " revoke denies unchanged graph/restored renewal")

    # A separately captured same-owner Acquire keeps the current lease, with
    # an original receipt deadline and freshly signed nonce. The earlier
    # Acquire may now be beyond signing skew after all queued-revocation phases.
    renew_owned(second_owner, 2)
    deadline_body, deadline_nonce, deadline_signed_at = command(process=second_owner), str(uuid.uuid4()), int(time.time())
    before_deadline_acquire = snapshot()
    before_deadline_history = immutable_history(deadline_nonce)
    deadline_acquire = receipt(*call(deadline_body, nonce=deadline_nonce, signed_at=deadline_signed_at), process=second_owner, epoch=2, changed=False, coverage=False)
    assert snapshot()[:2] == before_deadline_acquire[:2], "Same-owner Acquire changed lease/gap bytes"
    after_deadline_history = immutable_history(deadline_nonce)
    assert after_deadline_history[:2] == before_deadline_history[:2] and int(after_deadline_history[2]) == int(before_deadline_history[2]) + 1
    # Renew well before its immutable original deadline, then wait past that
    # deadline while the renewed lease is still live. No expiry SQL mutation.
    remaining = (timestamp(deadline_acquire["lease"]["expiresAtUtc"]) - datetime.now(timezone.utc)).total_seconds()
    if remaining > 10: time.sleep(remaining - 10)
    renew_owned(second_owner, 2)
    remaining = (timestamp(deadline_acquire["lease"]["expiresAtUtc"]) - datetime.now(timezone.utc)).total_seconds()
    if remaining > 0: time.sleep(remaining + .2)
    assert timing(deadline_nonce, deadline_signed_at) == '1|1|1', "Receipt expiry denial lacks expired/live/signed timing preconditions"
    no_effect(deadline_body, nonce=deadline_nonce, signed_at=deadline_signed_at)
    no_effect(second_body, nonce=second_nonce, signed_at=second_signed_at)
    renewed_stop = receipt(*call(command(3, 2, second_owner)), process=second_owner, epoch=2)
    assert renewed_stop["lease"]["expiresAtUtc"] == renewed_stop["lease"]["heartbeatAtUtc"]
    third_owner, third_nonce, third_signed_at = str(uuid.uuid4()), str(uuid.uuid4()), int(time.time())
    third_body = command(process=third_owner)
    third = receipt(*call(third_body, nonce=third_nonce, signed_at=third_signed_at), process=third_owner, epoch=3)
    before_expiry = snapshot()
    remaining = (timestamp(third["lease"]["expiresAtUtc"]) - datetime.now(timezone.utc)).total_seconds()
    if remaining > 0: time.sleep(remaining + .2)
    no_effect(third_body, nonce=third_nonce, signed_at=third_signed_at)
    assert snapshot() == before_expiry
    fourth_owner = str(uuid.uuid4())
    receipt(*call(command(process=fourth_owner)), process=fourth_owner, epoch=4)
    assert sql(f"SELECT COUNT(*) FROM aioffice.GroupAccountCoverageGaps WHERE {account_scope} AND ListenerEpoch=4 AND Reason='listener-expired';") == "1"
    assert sql("SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE login_name=N'aioffice_runtime' AND status=N'sleeping'"
        " AND (transaction_isolation_level<>2 OR open_transaction_count<>0);") == "0", "Listener left pooled isolation/transaction dirty"
    assert [int(sql(f"SELECT COUNT(*) FROM aioffice.{table} WHERE {source_scope};")) for table in
        ("GroupMessages", "GroupMessageRevisions", "GroupIngressReceipts", "GroupIngressOutbox", "GroupSourceStates", "GroupCoverageGaps")] == [0] * 6
    print("PASS actual listener original ACK deadline survives renewal; expired signed nonce cannot extend/reacquire; fresh takeover records gap/retained epoch and clean pooled isolation")
