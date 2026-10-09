"""Immutable submission proof, restricted to the disposable owned GitHub SQL stack."""
from concurrent.futures import ThreadPoolExecutor
import hashlib
import json
import os
from pathlib import Path
import re
import struct
import subprocess
import time
import urllib.parse
import urllib.request
import uuid


def input_fingerprint(source, question):
    encoded = question.encode("utf-8", errors="strict")
    return hashlib.sha256(b"aioffice-task-intent-v1\0" + uuid.UUID(source).hex.encode("ascii")
        + struct.pack("<II", 3, len(encoded)) + encoded).hexdigest().upper()


def task_identity(tenant, company, user, operation):
    parts = ["pilot-task-v1", *(uuid.UUID(value).hex for value in (tenant, company, user)), "web-intent-v1-" + uuid.UUID(operation).hex]
    return deterministic_identity(parts)


def deterministic_identity(parts):
    digest = bytearray(hashlib.sha256("\x1f".join(parts).encode("utf-8")).digest()[:16])
    digest[6] = (digest[6] & 15) | 0x50
    digest[8] = (digest[8] & 63) | 0x80
    return str(uuid.UUID(bytes_le=bytes(digest)))


def verify(*, directory, manifest, compose, environment, http, sql, runtime_statement, identity, api, auth):
    if not (os.environ.get("CI") == "true" and os.environ.get("GITHUB_ACTIONS") == "true"
            and os.environ.get("RUNNER_TEMP") and directory.resolve() == (Path(os.environ["RUNNER_TEMP"]) / "aioffice-local").resolve()
            and api == "http://127.0.0.1:8080" and identity == "http://127.0.0.1:8081"):
        raise RuntimeError("Submission proof requires the owned disposable GitHub CI fixture.")
    # Exact fixture bytes stay out of argv and diagnostic output.
    def owned_sql(query):
        result = subprocess.run([*compose, "exec", "-T", "sql", "sh", "-c",
            'SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -I -b -m 1 -h -1 -W -i /dev/stdin'],
            input="SET NOCOUNT ON; " + query, capture_output=True, text=True, env=environment, timeout=30)
        if result.returncode:
            match = re.search(r"\bMsg (\d{1,5}), Level\b", result.stdout + result.stderr)
            raise RuntimeError("Owned submission SQL command failed" + (" (SQL message " + match[1] + ")" if match else "") + ".")
        return result.stdout.strip()
    sql = owned_sql
    tenant, company, owner, source = (str(uuid.UUID(manifest[f"AIOFFICE_{key}_ID"])) for key in ("TENANT", "COMPANY", "USER", "DATA_SOURCE"))
    question = "Tồn kho 😀 �"
    fingerprint = input_fingerprint(source, question)
    assert input_fingerprint("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", question) == "6354CD8F9D07F489B9F1B213CE89B4FE5EF4016DFC44329AE740B0C39BDE0656"
    scope = f"TenantId='{tenant}' AND CompanyId='{company}'"
    owner_scope = scope + f" AND UserId='{owner}'"
    operation = str(uuid.uuid4())
    request = {"operationId": operation, "dataSourceId": source, "question": question}
    path = "/api/tasks/intents/" + operation

    def call(path, payload=None, authority=auth):
        status, headers, body = http(path, payload, base=api, headers=authority)
        assert "no-store" in headers.get("Cache-Control", ""), "Submission response was cacheable"
        assert "secretref://" not in json.dumps(body), "Submission exposed private source configuration"
        return status, headers, body

    tables = {"Tasks": "TenantId,CompanyId,Id", "TaskSteps": "TenantId,CompanyId,TaskId,Id",
        "TaskEvents": "TenantId,CompanyId,TaskId,Sequence", "TaskCheckpoints": "TenantId,CompanyId,TaskId,StepId,Version",
        "TaskStepExecutions": "TenantId,CompanyId,TaskId,StepId", "TaskDispatches": "TenantId,CompanyId,TaskId,StepId,MessageId",
        "CustomerAiCreditSettlements": "SettlementId"}

    def snapshot():
        values = []
        for table, order in tables.items():
            value = sql(f"""USE AIOfficeLocal; SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',
                CONVERT(varbinary(max),COALESCE((SELECT * FROM aioffice.{table} ORDER BY {order}
                FOR JSON PATH,INCLUDE_NULL_VALUES),N'[]'))),2);""")
            assert re.fullmatch(r"[0-9A-F]{64}", value), "Invalid submission effect fingerprint"
            values.append(value)
        return values

    def effect_counts():
        counts = [f"(SELECT COUNT_BIG(*) FROM aioffice.{table})" for table in tables]
        return [int(value) for value in sql("USE AIOfficeLocal; SELECT CONCAT(" + ",N',',".join(counts) + ");").split(",")]

    def require_one_graph(previous_counts, previous_snapshot, task):
        # Validate the first commit BEFORE it becomes the replay baseline.
        # This pilot performs one zero-credit read probe and adds request + two
        # terminal status events; no unrelated/orphan graph or charge is allowed.
        assert [after - before for before, after in zip(previous_counts, effect_counts(), strict=True)] == [1, 1, 3, 1, 1, 1, 0], "Admission created unexpected graph or credit rows"
        assert snapshot()[-1] == previous_snapshot[-1], "Pilot submission altered credit settlements"
        for table in tables:
            if table == "CustomerAiCreditSettlements": continue
            task_filter = f"Id='{task}'" if table == "Tasks" else f"TaskId='{task}'"
            expected = "3" if table == "TaskEvents" else "1"
            assert sql(f"USE AIOfficeLocal; SELECT COUNT(*) FROM aioffice.{table} WHERE {scope} AND {task_filter};") == expected, "Unexpected owned task graph cardinality"

    def await_completed(task):
        deadline = time.monotonic() + 60
        while time.monotonic() < deadline:
            if sql(f"USE AIOfficeLocal; SELECT COUNT(*) FROM aioffice.Tasks WHERE {scope} AND Id='{task}' AND Status=N'Completed';") == "1": return
            time.sleep(.5)
        raise AssertionError("Owned submission worker did not complete")

    before = snapshot()
    initial_counts = effect_counts()
    status, _, prepared = call("/api/tasks/intents", request)
    assert status == 200 and prepared["state"] == 0 and prepared["inputFingerprint"] == fingerprint
    assert prepared["question"] == question and prepared["dataSourceId"] == source and prepared["accepted"] is None
    assert call("/api/tasks/intents", request)[2] == prepared, "Preparation renewed or changed the immutable intent"
    assert call(path)[2] == prepared
    page = call("/api/tasks/intents?offset=0&limit=25")[2]
    assert any(item == prepared for item in page["items"])
    assert call("/api/tasks/intents", {**request, "question": "different"})[0] == 409
    assert call(path + "/submit", {"inputFingerprint": "0" * 64})[0] == 409
    assert snapshot() == before, "Preparation/recovery/conflict created execution effects"
    print("PASS native immutable prepare/replay/fixed lifetime/strict Unicode/GET-only recovery with zero execution effects")

    expected_task = task_identity(tenant, company, owner, operation)
    expected_step = deterministic_identity(["pilot-step-v1", uuid.UUID(expected_task).hex, "pilot.readonly-data-source-v1"])
    expected_message = deterministic_identity(["pilot-message-v1", uuid.UUID(expected_task).hex, uuid.UUID(expected_step).hex, "1"])
    with ThreadPoolExecutor(max_workers=2) as executor:
        accepted = list(executor.map(lambda _: call(path + "/submit", {"inputFingerprint": fingerprint}), range(2)))
    for status, headers, receipt in accepted:
        assert status == 202, f"Concurrent owned submit expected202, got HTTP{int(status)}"
        assert headers.get("Location") == f"/api/tasks/{expected_task}/history", "Concurrent owned submit Location identity mismatch"
        assert (receipt["companyId"], receipt["operationId"], receipt["dataSourceId"], receipt["inputFingerprint"],
            receipt["taskId"], receipt["stepId"], receipt["messageId"]) == (company, operation, source, fingerprint, expected_task, expected_step, expected_message)
    task_scope = scope + f" AND TaskId='{expected_task}'"
    await_completed(expected_task)
    assert sql(f"USE AIOfficeLocal; SELECT COUNT(*) FROM aioffice.TaskStepExecutions WHERE {task_scope} AND Attempt=1 AND LastFailureClass IS NULL;") == "1"
    assert sql(f"USE AIOfficeLocal; SELECT COUNT(*) FROM aioffice.TaskDispatches WHERE {task_scope} AND MessageId='{expected_message}' AND Attempt=1;") == "1"
    assert sql(f"USE AIOfficeLocal; SELECT COUNT(*) FROM aioffice.TaskCheckpoints WHERE {task_scope};") == "1"
    require_one_graph(initial_counts, before, expected_task)
    settled = snapshot()
    assert call(path)[2]["state"] == 1
    assert call(path + "/submit", {"inputFingerprint": fingerprint})[2]["taskId"] == expected_task
    assert call("/api/tasks/intents", request)[2]["accepted"]["messageId"] == expected_message
    assert snapshot() == settled, "Accepted recovery/replay created another execution or charge"
    print("PASS native concurrent same-operation submit preserves independent task/step/message identities and one completed worker attempt/dispatch/checkpoint")

    # The actual runtime cannot mutate any intent column, delete, truncate or take ownership.
    for column in ("TenantId", "CompanyId", "UserId", "OperationId", "DataSourceId", "InputVersion", "MaxAttempts", "Question", "InputFingerprint", "CreatedAtUtc", "ExpiresAtUtc"):
        runtime_statement(f"UPDATE aioffice.TaskSubmissionIntents SET [{column}]=[{column}] WHERE 1=0")
    for statement in ("DELETE FROM aioffice.TaskSubmissionIntents", "TRUNCATE TABLE aioffice.TaskSubmissionIntents",
            "ALTER TABLE aioffice.TaskSubmissionIntents ADD Forbidden int NULL",
            "ALTER AUTHORIZATION ON OBJECT::aioffice.TaskSubmissionIntents TO aioffice_runtime"):
        runtime_statement(statement)
    try:
        sql("""USE AIOfficeLocal; REVOKE UPDATE ON OBJECT::aioffice.TaskSubmissionIntents(Question) FROM aioffice_binding_runtime;
            GRANT UPDATE ON OBJECT::aioffice.TaskSubmissionIntents(Question) TO aioffice_runtime;""")
        runtime_statement("UPDATE aioffice.TaskSubmissionIntents SET Question=Question WHERE 1=0", expected="ALLOWED")
        assert call(path)[0] == 403 and call(path + "/submit", {"inputFingerprint": fingerprint})[0] == 403
    finally:
        sql("""USE AIOfficeLocal; REVOKE UPDATE ON OBJECT::aioffice.TaskSubmissionIntents(Question) FROM aioffice_runtime;
            DENY UPDATE ON OBJECT::aioffice.TaskSubmissionIntents(Question) TO aioffice_binding_runtime;""")
    assert call(path)[0] == 200 and snapshot() == settled
    print("PASS native intent every-column/append-only runtime rights and real unsafe column-grant refusal/restored positive")

    # Privileged corruption is restricted to owned disposable rows and always restored.
    row_scope = owner_scope + f" AND OperationId='{operation}'"
    original_question = "CONVERT(nvarchar(max),0x" + question.encode("utf-16-le").hex() + ")"
    for assignment in ("Question=CONVERT(nvarchar(4000),0x00d8)", "InputFingerprint=REPLICATE('0',64)"):
        try:
            sql(f"USE AIOfficeLocal; UPDATE aioffice.TaskSubmissionIntents SET {assignment} WHERE {row_scope};")
            corrupted = call(path)[2]
            assert corrupted["state"] == 3 and all(corrupted[key] is None for key in ("dataSourceId", "question", "inputFingerprint", "createdAtUtc", "expiresAtUtc", "accepted"))
            assert call(path + "/submit", {"inputFingerprint": fingerprint})[0] == 409
        finally:
            sql(f"USE AIOfficeLocal; UPDATE aioffice.TaskSubmissionIntents SET Question={original_question},InputFingerprint='{fingerprint}' WHERE {row_scope};")
    assert call(path)[2]["accepted"]["taskId"] == expected_task and snapshot() == settled
    print("PASS native raw malformedUTF16/hash corruption cannot execute or expose repaired private input, with restored committed acceptance")

    # Expiry limits uncommitted execution; an already committed operation remains authoritative.
    expired_op = str(uuid.uuid4())
    sql(f"""USE AIOfficeLocal; DECLARE @now datetimeoffset=DATEADD(hour,-25,SYSUTCDATETIME());
        INSERT aioffice.TaskSubmissionIntents(TenantId,CompanyId,UserId,OperationId,DataSourceId,InputVersion,MaxAttempts,Question,InputFingerprint,CreatedAtUtc,ExpiresAtUtc)
        VALUES('{tenant}','{company}','{owner}','{expired_op}','{source}',1,3,{original_question},'{fingerprint}',@now,DATEADD(hour,24,@now));""")
    expired_path = "/api/tasks/intents/" + expired_op
    assert call(expired_path)[2]["state"] == 2
    assert call(expired_path + "/submit", {"inputFingerprint": fingerprint})[2]["code"] == "intent-expired"
    assert snapshot() == settled
    try:
        sql(f"""USE AIOfficeLocal; DECLARE @expired datetimeoffset=DATEADD(hour,-25,SYSUTCDATETIME());
            UPDATE aioffice.TaskSubmissionIntents SET CreatedAtUtc=@expired,ExpiresAtUtc=DATEADD(hour,24,@expired) WHERE {row_scope};""")
        assert call(path)[2]["state"] == 1
        assert call(path + "/submit", {"inputFingerprint": fingerprint})[2]["taskId"] == expected_task
    finally:
        sql(f"""USE AIOfficeLocal; UPDATE aioffice.TaskSubmissionIntents SET CreatedAtUtc=CONVERT(datetimeoffset,'{prepared['createdAtUtc']}'),
            ExpiresAtUtc=CONVERT(datetimeoffset,'{prepared['expiresAtUtc']}') WHERE {row_scope};""")
    assert snapshot() == settled
    print("PASS native fixed24h expired-uncommitted denial and historical committed acceptance after expiry without new effects")

    # Operator-only constraint rejects the actual outbox INSERT after graph save
    # begins; the transaction must leave no partial task/event/step/execution.
    rollback_op = str(uuid.uuid4())
    rollback_path = "/api/tasks/intents/" + rollback_op
    assert call("/api/tasks/intents", {**request, "operationId": rollback_op})[0] == 200
    rollback_task = task_identity(tenant, company, owner, rollback_op)
    constraint = "CK_SubmissionProof_" + uuid.uuid4().hex
    try:
        sql(f"USE AIOfficeLocal; ALTER TABLE aioffice.TaskDispatches ADD CONSTRAINT [{constraint}] CHECK(TaskId<>'{rollback_task}');")
        assert call(rollback_path + "/submit", {"inputFingerprint": fingerprint})[0] == 503
        assert call(rollback_path)[2]["state"] == 0 and snapshot() == settled
    finally:
        sql(f"USE AIOfficeLocal; ALTER TABLE aioffice.TaskDispatches DROP CONSTRAINT [{constraint}];")
    rollback_counts = effect_counts()
    assert call(rollback_path + "/submit", {"inputFingerprint": fingerprint})[2]["taskId"] == rollback_task
    await_completed(rollback_task)
    require_one_graph(rollback_counts, settled, rollback_task)
    settled = snapshot()
    assert call(rollback_path + "/submit", {"inputFingerprint": fingerprint})[2]["taskId"] == rollback_task and snapshot() == settled
    print("PASS native actual outbox-write failure rolls back the whole graph and restored same-operation retry creates one completed task")

    # A real second provider identity has an isolated100-active quota. Seed99 valid
    # immutable rows as operator, then race actual HTTP admissions100 and101.
    fixture = json.loads((directory / "history-fixture.json").read_text(encoding="utf-8"))
    other_user = str(uuid.UUID(fixture["otherUserId"]))
    form = urllib.parse.urlencode({"client_id": "aioffice-local", "grant_type": "password", "username": fixture["otherUsername"], "password": manifest["AIOFFICE_OWNER_PASSWORD"]}).encode()
    with urllib.request.urlopen(urllib.request.Request(identity + "/realms/aioffice-local/protocol/openid-connect/token", data=form,
            headers={"Content-Type": "application/x-www-form-urlencoded"}), timeout=15) as response:
        token = json.loads(response.read(65537))["access_token"]
    other_auth = {"Authorization": "Bearer " + token, "X-AIOffice-Company-Id": company}
    assert call(path, authority=other_auth)[0] == 404
    assert call(path, authority={**auth, "X-AIOffice-Company-Id": fixture["companyId"]})[0] == 404
    assert call(path + "/submit", {"inputFingerprint": fingerprint}, other_auth)[0] == 404
    operations = [str(uuid.uuid4()) for _ in range(99)]
    values = ",".join(f"('{tenant}','{company}','{other_user}','{op}','{source}',1,3,{original_question},'{fingerprint}',@now,DATEADD(hour,24,@now))" for op in operations)
    sql("USE AIOfficeLocal; DECLARE @now datetimeoffset=SYSUTCDATETIME(); INSERT aioffice.TaskSubmissionIntents"
        "(TenantId,CompanyId,UserId,OperationId,DataSourceId,InputVersion,MaxAttempts,Question,InputFingerprint,CreatedAtUtc,ExpiresAtUtc) VALUES" + values + ";")
    other_scope = scope + f" AND UserId='{other_user}'"
    assert sql(f"USE AIOfficeLocal; SELECT COUNT(*) FROM aioffice.TaskSubmissionIntents WHERE {other_scope};") == "99"
    with ThreadPoolExecutor(max_workers=2) as executor:
        results = list(executor.map(lambda _: call("/api/tasks/intents", {**request, "operationId": str(uuid.uuid4())}, other_auth), range(2)))
    assert sorted(result[0] for result in results) == [200, 409], "Concurrent quota99/100/101 admission was not serialized"
    assert next(result[2] for result in results if result[0] == 409)["code"] == "intent-limit"
    assert sql(f"USE AIOfficeLocal; SELECT COUNT(*) FROM aioffice.TaskSubmissionIntents WHERE {other_scope};") == "100"
    assert call("/api/tasks/intents", {**request, "operationId": operations[0]}, other_auth)[0] == 200
    assert snapshot() == settled, "Quota preparation or foreign recovery created execution effects"
    print("PASS native two-provider owner/company404 isolation and concurrent99/100/101 quota with same-operation replay at capacity")

    committed_op = operations[0]
    committed_path = "/api/tasks/intents/" + committed_op
    committed_task = task_identity(tenant, company, other_user, committed_op)
    secondary_counts = effect_counts()
    assert call(committed_path + "/submit", {"inputFingerprint": fingerprint}, other_auth)[2]["taskId"] == committed_task
    await_completed(committed_task)
    require_one_graph(secondary_counts, settled, committed_task)
    assert call("/api/tasks/intents", {**request, "operationId": str(uuid.uuid4())}, other_auth)[0] == 200
    assert sql(f"USE AIOfficeLocal; SELECT COUNT(*) FROM aioffice.TaskSubmissionIntents WHERE {other_scope};") == "101"
    assert call("/api/tasks/intents", {**request, "operationId": committed_op}, other_auth)[2]["state"] == 1
    settled = snapshot()
    # Existence of a Task is insufficient: invalid original request consumes quota.
    event = sql(f"USE AIOfficeLocal; SELECT CONVERT(varchar(max),CONVERT(varbinary(max),PayloadJson),2) FROM aioffice.TaskEvents WHERE {scope} AND TaskId='{committed_task}' AND Sequence=1;")
    assert re.fullmatch(r"[0-9A-F]+", event)
    try:
        sql(f"USE AIOfficeLocal; UPDATE aioffice.TaskEvents SET PayloadJson=N'{{}}' WHERE {scope} AND TaskId='{committed_task}' AND Sequence=1;")
        assert call(committed_path, authority=other_auth)[2]["state"] == 3
        assert call("/api/tasks/intents", {**request, "operationId": str(uuid.uuid4())}, other_auth)[2]["code"] == "intent-limit"
    finally:
        sql(f"USE AIOfficeLocal; UPDATE aioffice.TaskEvents SET PayloadJson=CONVERT(nvarchar(max),0x{event}) WHERE {scope} AND TaskId='{committed_task}' AND Sequence=1;")
    assert call(committed_path, authority=other_auth)[2]["state"] == 1 and snapshot() == settled
    print("PASS native quota excludes only validated committed acceptance and refuses malformed original task evidence, with restored positive")
