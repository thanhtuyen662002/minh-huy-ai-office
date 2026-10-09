"""Required archive SQL proof. Mutations exist only in the owned disposable CI fixture."""
from concurrent.futures import ThreadPoolExecutor
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import time
import urllib.parse
import urllib.request
import uuid


def _owned_sql_stdin(compose, environment, query):
    # Hex-encoded malformed/oversize nvarchar can exceed Linux's single-argv
    # limit. Keep the exact fixture bytes and feed SQL through stdin instead.
    result = subprocess.run([*compose, "exec", "-T", "sql", "sh", "-c",
        'SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -I -b -m 1 -h -1 -W -i /dev/stdin'],
        input="SET NOCOUNT ON; " + query, capture_output=True, text=True, env=environment, timeout=30)
    if result.returncode:
        match = re.search(r"\bMsg (\d{1,5}), Level\b", result.stdout + result.stderr)
        raise RuntimeError("Owned archive SQL fixture command failed" + (" (SQL message " + match[1] + ")" if match else "") + ".")
    return result.stdout.strip()


def verify(*, directory, manifest, compose, environment, http, sql, identity_admin, identity, api, auth, retained_task):
    if not (os.environ.get("CI") == "true" and os.environ.get("GITHUB_ACTIONS") == "true"
            and os.environ.get("RUNNER_TEMP") and directory.resolve() == (Path(os.environ["RUNNER_TEMP"]) / "aioffice-local").resolve()
            and api == "http://127.0.0.1:8080"):
        raise RuntimeError("Archive proof requires the owned disposable GitHub CI fixture.")
    sql = lambda query: _owned_sql_stdin(compose, environment, query)
    tenant, company, owner, source = (str(uuid.UUID(manifest[f"AIOFFICE_{key}_ID"])) for key in ("TENANT", "COMPANY", "USER", "DATA_SOURCE"))
    retained_task = str(uuid.UUID(retained_task))
    archive_company, other_user = str(uuid.uuid4()), str(uuid.uuid4())
    username = "archive-proof-" + uuid.uuid4().hex
    identity_admin("users", {"username": username, "enabled": True, "emailVerified": True,
        "firstName": "Disposable", "lastName": "Archive", "email": username + "@example.invalid",
        "credentials": [{"type": "password", "value": manifest["AIOFFICE_OWNER_PASSWORD"], "temporary": False}]}, method="POST")
    users = identity_admin("users?username=" + username + "&exact=true")
    assert len(users) == 1 and users[0]["username"] == username
    subject = str(uuid.UUID(users[0]["id"]))
    form = urllib.parse.urlencode({"client_id": "aioffice-local", "grant_type": "password",
        "username": username, "password": manifest["AIOFFICE_OWNER_PASSWORD"]}).encode()
    with urllib.request.urlopen(urllib.request.Request(identity + "/realms/aioffice-local/protocol/openid-connect/token",
        data=form, headers={"Content-Type": "application/x-www-form-urlencoded"}), timeout=15) as response:
        other_token = json.loads(response.read(65537))["access_token"]
    sql(f"""USE AIOfficeLocal; SET XACT_ABORT ON; BEGIN TRANSACTION;
        INSERT aioffice.Companies(TenantId,Id,Code,Name) VALUES('{tenant}','{archive_company}',N'{archive_company}',N'Disposable archive SQL proof');
        INSERT aioffice.Users(TenantId,Id,IdentityProvider,Subject,DisplayName)
          VALUES('{tenant}','{other_user}',N'local-keycloak',N'{subject}',N'Disposable archive owner');
        INSERT aioffice.CompanyMemberships(TenantId,CompanyId,UserId) VALUES
          ('{tenant}','{archive_company}','{owner}'),('{tenant}','{archive_company}','{other_user}');
        INSERT aioffice.RoleAssignments(TenantId,CompanyId,UserId,RoleKey) VALUES('{tenant}','{archive_company}','{other_user}',N'admin');
        COMMIT TRANSACTION;""")
    headers = {**auth, "X-AIOffice-Company-Id": archive_company}
    other_headers = {"Authorization": "Bearer " + other_token, "X-AIOffice-Company-Id": archive_company}
    task_ids = [str(uuid.uuid4()) for _ in range(4)]
    question = "Tồn kho 😀 �"
    request = {"idempotencyKey": "owned-history", "dataSourceId": source, "question": question, "maxAttempts": 3}
    checkpoint = {"status": "completed", "dataSourceId": source, "logicalName": "Disposable fixture",
        "questionLength": len(question.encode("utf-16-le")) // 2, "answer": "Kết quả đã lưu 😀 �", "provider": "fixture", "model": "fixture-v1",
        "usage": {"inputTokens": 2, "outputTokens": 3, "totalTokens": 5}, "billingStatus": "usage-observed-not-settled",
        "erpEvidence": {"databaseName": "OwnedFixture", "tableCount": 0, "sampledTableCount": 0, "topTables": []},
        "evidence": "ai-provider-reasoning-after-bounded-read-only-erp-catalog"}
    def stored(value):
        text = value if isinstance(value, str) else json.dumps(value, ensure_ascii=False, separators=(",", ":"))
        return "CONVERT(nvarchar(max),0x" + text.encode("utf-16-le", errors="surrogatepass").hex() + ")"
    def step(task):
        digest = bytearray(hashlib.sha256(("pilot-step-v1\x1f" + uuid.UUID(task).hex + "\x1fpilot.readonly-data-source-v1").encode()).digest()[:16])
        digest[6] = (digest[6] & 15) | 0x50; digest[8] = (digest[8] & 63) | 0x80
        return str(uuid.UUID(bytes_le=bytes(digest)))
    scope = f"TenantId='{tenant}' AND CompanyId='{archive_company}'"
    for index, task in enumerate(task_ids):
        status = ["Completed", "Pending", "Failed", "Completed"][index]
        task_owner = owner if index < 3 else other_user
        row_request = request if index < 3 else {**request, "question": "Other owner task"}
        row_checkpoint = checkpoint if index < 3 else {**checkpoint, "questionLength": len(row_request["question"]), "answer": "Other owner saved answer"}
        sql(f"""USE AIOfficeLocal; SET XACT_ABORT ON; BEGIN TRANSACTION;
            INSERT aioffice.Tasks(TenantId,CompanyId,Id,CreatedByUserId,Status,WaitReason,CreatedAtUtc,UpdatedAtUtc)
              VALUES('{tenant}','{archive_company}','{task}','{task_owner}',N'{status}',N'PRIVATE_DIAGNOSTIC','2000-01-01','2000-01-01');
            INSERT aioffice.TaskSteps(TenantId,CompanyId,TaskId,Id,StepKey,Status,Attempt,CreatedAtUtc,UpdatedAtUtc)
              VALUES('{tenant}','{archive_company}','{task}','{step(task)}',N'pilot.readonly-data-source-v1',N'{status}',1,'2000-01-01','2000-01-01');
            INSERT aioffice.TaskEvents(TenantId,CompanyId,TaskId,Sequence,EventType,PayloadJson)
              VALUES('{tenant}','{archive_company}','{task}',1,N'pilot.task.requested',{stored(row_request)});
            INSERT aioffice.TaskCheckpoints(TenantId,CompanyId,TaskId,StepId,Version,PayloadJson)
              VALUES('{tenant}','{archive_company}','{task}','{step(task)}',1,{stored(row_checkpoint)});
            COMMIT TRANSACTION;""")
    def get(path, authority=headers):
        status, cache, body = http(path, base=api, headers=authority)
        assert "no-store" in cache.get("Cache-Control", ""), "Archive read was cacheable"
        assert "PRIVATE_" not in json.dumps(body), "Archive exposed private diagnostics or unvalidated metadata"
        return status, body
    def detail(task=task_ids[0], authority=headers):
        return get(f"/api/tasks/{task}/history", authority)
    original_snapshot_tables = {
        "Tasks": "TenantId,CompanyId,Id", "TaskSteps": "TenantId,CompanyId,TaskId,Id",
        "TaskEvents": "TenantId,CompanyId,TaskId,Sequence", "TaskCheckpoints": "TenantId,CompanyId,TaskId,StepId,Version",
        "TaskStepExecutions": "TenantId,CompanyId,TaskId,StepId", "TaskDispatches": "TenantId,CompanyId,TaskId,StepId,MessageId",
        "Users": "TenantId,Id", "RoleAssignments": "TenantId,CompanyId,UserId,RoleKey", "CompanyMemberships": "TenantId,CompanyId,UserId",
        "CompanyMembershipAccessAudits": "TenantId,CompanyId,Id", "CompanyAdministratorAudits": "TenantId,CompanyId,Id",
        "DataSourceSecretBindings": "TenantId,CompanyId,Id", "CustomerAiCreditSettlements": "SettlementId"}
    def snapshot():
        values = []
        for table, order in original_snapshot_tables.items():
            # Hash in SQL; neither durable JSON nor identity/secret metadata is printed.
            values.append(sql(f"""USE AIOfficeLocal; SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',
              CONVERT(varbinary(max),(SELECT * FROM aioffice.{table} ORDER BY {order} FOR JSON PATH))),2);"""))
        return values
    before = snapshot()
    pages = [get(f"/api/tasks?offset={offset}&limit=1")[1] for offset in range(3)]
    expected = sql(f"USE AIOfficeLocal; SELECT Id FROM aioffice.Tasks WHERE {scope} AND CreatedByUserId='{owner}' ORDER BY CreatedAtUtc DESC,Id DESC;").split()
    assert [page["items"][0]["taskId"] for page in pages] == [value.lower() for value in expected]
    assert [page["hasMore"] for page in pages] == [True, True, False]
    assert {page["items"][0]["status"] for page in pages} == {0, 5, 6}
    status, saved = detail(); assert status == 200 and saved["result"]["answer"] == checkpoint["answer"]
    assert saved["task"]["summary"] == question and not saved["resultUnavailable"]
    assert detail(task_ids[3])[0] == 404 and detail(task_ids[0], other_headers)[0] == 404
    assert get("/api/tasks", other_headers)[1]["items"][0]["taskId"] == task_ids[3]
    assert detail(task_ids[0], auth)[0] == 404  # original selected company
    assert get("/api/tasks?ownerId=" + other_user)[0] == 400
    assert get("/api/tasks?offset=10001")[0] == 400
    assert snapshot() == before
    print("PASS real SQL archive owner/admin/company isolation, equal-time pagination, known Unicode result and no durable effects")

    task_scope = scope + f" AND TaskId='{task_ids[0]}'"
    corrupt_requests = ["{}", "PRIVATE_INVALID_JSON", {**request, "secretRef": "PRIVATE_SECRET"},
        {**request, "question": "PRIVATE\ud800"}, {**request, "question": "x" * 33000},
        json.dumps(request).replace('"maxAttempts": 3', '"maxAttempts": 3,"question":"PRIVATE_DUPLICATE"')]
    corrupt_checkpoints = ["{}", {**checkpoint, "answer": "PRIVATE\ud800"}, {**checkpoint, "secretRef": "PRIVATE_SECRET"},
        {**checkpoint, "usage": {"inputTokens": 2, "outputTokens": 3, "totalTokens": 4}}, "x" * 140000]
    try:
        for invalid in corrupt_requests:
            sql(f"USE AIOfficeLocal; UPDATE aioffice.TaskEvents SET PayloadJson={stored(invalid)} WHERE {task_scope} AND Sequence=1;")
            assert get("/api/tasks")[0] == 200
            status, body = detail(); assert status == 200 and body["task"]["metadataUnavailable"] and body["resultUnavailable"] and body["result"] is None
        sql(f"USE AIOfficeLocal; UPDATE aioffice.TaskEvents SET PayloadJson={stored(request)} WHERE {task_scope} AND Sequence=1;")
        for invalid in corrupt_checkpoints:
            sql(f"USE AIOfficeLocal; UPDATE aioffice.TaskCheckpoints SET PayloadJson={stored(invalid)} WHERE {task_scope} AND Version=1;")
            status, body = detail(); assert status == 200 and body["task"]["status"] == 6 and body["resultUnavailable"] and body["result"] is None
        sql(f"USE AIOfficeLocal; UPDATE aioffice.Tasks SET Status=N'PRIVATE_UNKNOWN' WHERE {scope} AND Id='{task_ids[0]}';")
        status, body = detail(); assert status == 200 and body["task"]["status"] is None and body["task"]["metadataUnavailable"]
    finally:
        sql(f"""USE AIOfficeLocal;
          UPDATE aioffice.TaskEvents SET PayloadJson={stored(request)} WHERE {task_scope} AND Sequence=1;
          UPDATE aioffice.TaskCheckpoints SET PayloadJson={stored(checkpoint)} WHERE {task_scope} AND Version=1;
          UPDATE aioffice.Tasks SET Status=N'Completed' WHERE {scope} AND Id='{task_ids[0]}';""")
    assert detail()[1]["result"]["answer"] == checkpoint["answer"] and snapshot() == before
    print("PASS actual stored UTF16 surrogate/oversize/duplicate/unknown archive metadata refusal with unchanged completed status and restored valid supplementary/U+FFFD result")

    binding = f"TenantId='{tenant}' AND CompanyId='{company}' AND CanonicalReference=N'secretref://env/PILOT_ERP_CONNECTION' COLLATE Latin1_General_100_BIN2"
    enabled = sql(f"USE AIOfficeLocal; SELECT CONVERT(int,IsEnabled) FROM aioffice.DataSourceSecretBindings WHERE {binding};")
    assert enabled in ("0", "1")
    try:
        sql(f"USE AIOfficeLocal; UPDATE aioffice.DataSourceSecretBindings SET IsEnabled=0 WHERE {binding};")
        assert get(f"/api/tasks/{retained_task}", auth)[0] == 200
        status, body = get(f"/api/tasks/{retained_task}/history", auth)
        assert status == 200 and body["task"]["status"] == 6 and body["result"] is not None
    finally:
        sql(f"USE AIOfficeLocal; UPDATE aioffice.DataSourceSecretBindings SET IsEnabled={enabled} WHERE {binding};")
    assert snapshot() == before
    print("PASS actual revoked-source-grant historical-owner result/archive reads without ERP rerun, dispatch or credit settlement")

    # A native SQL row lock proves each real request passed its initial
    # directory read and is waiting on private data, before external revocation.
    def final_fence(path, table, row_scope, selected_company, authority):
        gate = "dbo.ArchiveGate_" + uuid.uuid4().hex
        member = f"TenantId='{tenant}' AND CompanyId='{selected_company}' AND UserId='{owner}'"
        sql(f"USE AIOfficeLocal; CREATE TABLE {gate}(Released bit NOT NULL,OwnerSpid int NULL); INSERT {gate} VALUES(0,NULL);")
        query = f"""SET NOCOUNT ON; USE AIOfficeLocal; SET XACT_ABORT ON;
          UPDATE {gate} SET OwnerSpid=@@SPID; BEGIN TRANSACTION;
          UPDATE aioffice.{table} WITH(ROWLOCK) SET PayloadJson=PayloadJson WHERE {row_scope};
          DECLARE @deadline datetime2=DATEADD(second,30,SYSUTCDATETIME());
          WHILE (SELECT Released FROM {gate} WITH(READUNCOMMITTED))=0 AND SYSUTCDATETIME()<@deadline WAITFOR DELAY '00:00:00.100';
          ROLLBACK TRANSACTION;"""
        locker = subprocess.Popen([*compose, "exec", "-T", "sql", "sh", "-c",
            'SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -I -b -m 1 -h -1 -W -Q "$1"',
            "sql", query], stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, env=environment)
        def await_condition(predicate, seconds=6):
            deadline = time.monotonic() + seconds
            while time.monotonic() < deadline:
                if predicate(): return
                time.sleep(.1)
            raise AssertionError("Native archive read barrier was not reached")
        executor = None
        try:
            await_condition(lambda: sql(f"SELECT COUNT(*) FROM sys.dm_tran_locks l WHERE l.request_session_id=(SELECT OwnerSpid FROM AIOfficeLocal.{gate}) AND l.request_mode=N'X' AND l.resource_database_id=DB_ID(N'AIOfficeLocal');") != "0")
            executor = ThreadPoolExecutor(max_workers=1); pending = executor.submit(get, path, authority)
            await_condition(lambda: sql(f"""SELECT COUNT(*) FROM sys.dm_exec_requests r JOIN sys.dm_exec_sessions s ON s.session_id=r.session_id
              CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) t WHERE s.login_name=N'aioffice_runtime'
                AND r.blocking_session_id=(SELECT OwnerSpid FROM AIOfficeLocal.{gate}) AND t.text LIKE N'%{table}%';""") == "1")
            sql(f"USE AIOfficeLocal; UPDATE aioffice.CompanyMemberships SET IsActive=0 WHERE {member}; UPDATE {gate} SET Released=1;")
            status, body = pending.result(timeout=15); assert status == 403 and body in ("", None)
        finally:
            sql(f"USE AIOfficeLocal; UPDATE {gate} SET Released=1;")
            try: locker.communicate(timeout=8)
            except subprocess.TimeoutExpired:
                locker.kill(); locker.communicate(timeout=5)
            if executor is not None: executor.shutdown(wait=True, cancel_futures=True)
            sql(f"USE AIOfficeLocal; UPDATE aioffice.CompanyMemberships SET IsActive=1 WHERE {member}; DROP TABLE {gate};")
        assert locker.returncode == 0 and get(path, authority)[0] == 200, "Archive barrier/restored positive failed"
    final_fence("/api/tasks", "TaskEvents", task_scope + " AND Sequence=1", archive_company, headers)
    final_fence(f"/api/tasks/{task_ids[0]}/history", "TaskCheckpoints", task_scope + " AND Version=1", archive_company, headers)
    retained_scope = f"TenantId='{tenant}' AND CompanyId='{company}' AND TaskId='{retained_task}'"
    final_fence(f"/api/tasks/{retained_task}", "TaskCheckpoints", retained_scope, company, auth)
    assert snapshot() == before
    print("PASS native queued SQL list/archive/legacy-result final membership revocation, empty403 and restored positive with unchanged task/identity/role/audit graph")
    # Only disposable fixture identifiers/usernames cross into the subsequent
    # Code/S256 browser proof. No token, password or customer metadata is saved.
    fixture_path = directory / "history-fixture.json"
    fixture_path.write_text(json.dumps({"companyId": archive_company, "otherUserId": other_user,
        "otherUsername": username, "taskIds": task_ids}), encoding="utf-8")
    fixture_path.chmod(0o600)
