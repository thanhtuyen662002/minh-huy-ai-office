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


def require_single_execution_delta(before, after, before_credit, after_credit):
    assert len(before) == len(after) == 7, "Invalid execution effect cardinalities"
    assert [end - start for start, end in zip(before, after, strict=True)] == [1, 1, 3, 1, 1, 1, 0], "Admission created unexpected graph or credit rows"
    assert before_credit == after_credit, "Pilot submission altered credit settlements"


def require_stored_event_hex(value, byte_count):
    assert 0 < byte_count <= 4000 and len(value) == byte_count * 2 and re.fullmatch(r"[0-9A-F]+", value), "Owned original event transport was truncated or malformed"
    return value


def verify(*, directory, manifest, compose, environment, http, sql, runtime_statement, identity, api, auth):
    if not (os.environ.get("CI") == "true" and os.environ.get("GITHUB_ACTIONS") == "true"
            and os.environ.get("RUNNER_TEMP") and directory.resolve() == (Path(os.environ["RUNNER_TEMP"]) / "aioffice-local").resolve()
            and api == "http://127.0.0.1:8080" and identity == "http://127.0.0.1:8081"):
        raise RuntimeError("Submission proof requires the owned disposable GitHub CI fixture.")
    # Exact fixture bytes stay out of argv and diagnostic output.
    def owned_sql(query):
        result = subprocess.run([*compose, "exec", "-T", "sql", "sh", "-c",
            'SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -I -b -m 1 -h -1 -W -w 65535 -i /dev/stdin'],
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
        require_single_execution_delta(previous_counts, effect_counts(), previous_snapshot[-1], snapshot()[-1])
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

    def original_event_hex(task):
        predicate = scope + f" AND TaskId='{task}' AND Sequence=1"
        size = int(sql(f"USE AIOfficeLocal; SELECT DATALENGTH(PayloadJson) FROM aioffice.TaskEvents WHERE {predicate};"))
        # sqlcmd truncates varchar(max) output to256 by default. This owned
        # short-question fixture fits a bounded fixed varchar; independently
        # compare SQL byte length before ever restoring it. Shipping limits stay.
        value = sql(f"USE AIOfficeLocal; SELECT CONVERT(varchar(8000),CONVERT(varbinary(max),PayloadJson),2) FROM aioffice.TaskEvents WHERE {predicate};")
        return require_stored_event_hex(value, size)

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

    maximum = "😀" * 1999 + "�x"
    assert len(maximum.encode("utf-16-le")) == 8000
    unicode_rows = []
    for text in (maximum, "ồ", "o\u0302\u0300"):
        unicode_op = str(uuid.uuid4())
        status, _, detail = call("/api/tasks/intents", {**request, "operationId": unicode_op, "question": text})
        assert status == 200 and detail["question"] == text and detail["inputFingerprint"] == input_fingerprint(source, text)
        assert call("/api/tasks/intents/" + unicode_op)[2] == detail
        unicode_rows.append(detail)
    assert unicode_rows[1]["inputFingerprint"] != unicode_rows[2]["inputFingerprint"], "Preparation normalized distinct canonical Unicode inputs"
    rejected_op = str(uuid.uuid4())
    assert call("/api/tasks/intents", {**request, "operationId": rejected_op, "question": maximum + "x"})[0] == 400
    assert call("/api/tasks/intents/" + rejected_op)[0] == 404 and snapshot() == before
    print("PASS native exact4000UTF16 supplementary/U+FFFD roundtrip, adjacent overflow refusal and composed/decomposed fingerprints without normalization or execution")

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

    def intent_snapshot():
        value = sql("""USE AIOfficeLocal; SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),
            COALESCE((SELECT * FROM aioffice.TaskSubmissionIntents ORDER BY TenantId,CompanyId,UserId,OperationId
            FOR JSON PATH,INCLUDE_NULL_VALUES),N'[]'))),2);""")
        assert re.fullmatch(r"[0-9A-F]{64}", value)
        return value

    def unsafe_store_denied():
        original_intents = intent_snapshot()
        assert call(path)[0] == 403 and call("/api/tasks/intents?offset=0&limit=25")[0] == 403
        assert call(path + "/submit", {"inputFingerprint": fingerprint})[0] == 403
        assert call("/api/tasks/intents", {**request, "operationId": str(uuid.uuid4())})[0] == 403
        assert snapshot() == settled and intent_snapshot() == original_intents

    # Prove the indirect permission is real as the actual runtime, then prove
    # all intent boundaries close before reading, preparing or replaying.
    module = "aioffice.SubmissionModule_" + uuid.uuid4().hex
    trigger = "aioffice.SubmissionTrigger_" + uuid.uuid4().hex
    try:
        sql("USE AIOfficeLocal; GRANT IMPERSONATE ON USER::aioffice_binding_operator_owner TO aioffice_runtime;")
        runtime_statement("EXECUTE AS USER=N'aioffice_binding_operator_owner'; UPDATE aioffice.TaskSubmissionIntents SET Question=Question WHERE 1=0; REVERT;", expected="ALLOWED")
        unsafe_store_denied()
    finally:
        sql("USE AIOfficeLocal; REVOKE IMPERSONATE ON USER::aioffice_binding_operator_owner FROM aioffice_runtime;")
    assert call(path)[0] == 200 and snapshot() == settled
    try:
        definition = f"CREATE PROCEDURE {module} AS UPDATE aioffice.TaskSubmissionIntents SET Question=Question WHERE 1=0;"
        sql("USE AIOfficeLocal; EXEC(N'" + definition.replace("'", "''") + f"'); GRANT EXECUTE ON OBJECT::{module} TO aioffice_runtime;")
        runtime_statement("EXEC " + module)
        unsafe_store_denied()
        definition = f"ALTER PROCEDURE {module} WITH EXECUTE AS OWNER AS UPDATE aioffice.TaskSubmissionIntents SET Question=Question WHERE 1=0;"
        sql("USE AIOfficeLocal; EXEC(N'" + definition.replace("'", "''") + "');")
        runtime_statement("EXEC " + module, expected="ALLOWED")
        unsafe_store_denied()
    finally:
        sql(f"USE AIOfficeLocal; DROP PROCEDURE IF EXISTS {module};")
    assert call(path)[0] == 200 and snapshot() == settled
    try:
        definition = f"CREATE TRIGGER {trigger} ON aioffice.DataSources WITH EXECUTE AS OWNER AFTER UPDATE AS BEGIN SET NOCOUNT ON; UPDATE aioffice.TaskSubmissionIntents SET Question=Question WHERE 1=0; END;"
        sql("USE AIOfficeLocal; EXEC(N'" + definition.replace("'", "''") + "');")
        runtime_statement("UPDATE aioffice.DataSources SET Purpose=Purpose WHERE 1=0", expected="ALLOWED")
        unsafe_store_denied()
    finally:
        sql(f"USE AIOfficeLocal; DROP TRIGGER IF EXISTS {trigger};")
    assert call(path)[0] == 200 and snapshot() == settled
    print("PASS native real impersonation/ordinary owner-chain/EXECUTE AS OWNER/trigger escalation closes all intent boundaries with unchanged bytes and restored positives")

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

    original_event = original_event_hex(expected_task)
    original_request = {"idempotencyKey": "web-intent-v1-" + uuid.UUID(operation).hex, "dataSourceId": source, "question": question, "maxAttempts": 3}
    invalid_events = [json.dumps({**original_request, **delta}) for delta in (
        {"idempotencyKey": "web-intent-v1-" + uuid.uuid4().hex}, {"dataSourceId": str(uuid.uuid4())},
        {"question": "different"}, {"maxAttempts": 2}, {"unexpected": True})]
    invalid_events += ["{" + json.dumps(original_request)[1:-1] + ',"question":"different"}', "\ud800"]
    for payload in invalid_events:
        try:
            raw = payload.encode("utf-16-le", errors="surrogatepass").hex()
            sql(f"USE AIOfficeLocal; UPDATE aioffice.TaskEvents SET PayloadJson=CONVERT(nvarchar(max),0x{raw}) WHERE {task_scope} AND Sequence=1;")
            status, _, invalid = call(path)
            assert status == 200 and invalid["state"] == 3 and all(invalid[key] is None for key in ("dataSourceId", "question", "inputFingerprint", "createdAtUtc", "expiresAtUtc", "accepted"))
            assert call(path + "/submit", {"inputFingerprint": fingerprint})[2]["code"] == "intent-unavailable"
        finally:
            sql(f"USE AIOfficeLocal; UPDATE aioffice.TaskEvents SET PayloadJson=CONVERT(nvarchar(max),0x{original_event}) WHERE {task_scope} AND Sequence=1;")
        assert call(path)[2]["state"] == 1 and snapshot() == settled
    print("PASS native original request key/source/question/maxAttempts/unknown/duplicate/rawUTF16 conflicts cannot become acceptance, with restored exact original bytes")

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
    event = original_event_hex(committed_task)
    try:
        sql(f"USE AIOfficeLocal; UPDATE aioffice.TaskEvents SET PayloadJson=N'{{}}' WHERE {scope} AND TaskId='{committed_task}' AND Sequence=1;")
        assert call(committed_path, authority=other_auth)[2]["state"] == 3
        assert call("/api/tasks/intents", {**request, "operationId": str(uuid.uuid4())}, other_auth)[2]["code"] == "intent-limit"
    finally:
        sql(f"USE AIOfficeLocal; UPDATE aioffice.TaskEvents SET PayloadJson=CONVERT(nvarchar(max),0x{event}) WHERE {scope} AND TaskId='{committed_task}' AND Sequence=1;")
    assert call(committed_path, authority=other_auth)[2]["state"] == 1, "Restored exact original event did not recover acceptance"
    assert snapshot() == settled, "Restored original event changed execution effects"
    print("PASS native quota excludes only validated committed acceptance and refuses malformed original task evidence, with restored positive")

    # The public legacy DTO accepts source/question; attempts are server-owned.
    # Stored-event attempts corruption is covered above against the real graph.
    legacy_input = {"dataSourceId": source, "question": question}
    legacy_auth = {**auth, "Idempotency-Key": "web-intent-v1-" + uuid.UUID(operation).hex}
    replay_status, _, replay_body = call("/api/tasks", legacy_input, legacy_auth)
    assert replay_status == 202 and replay_body["taskId"] == expected_task
    conflict_status, _, conflict_body = call("/api/tasks", {**legacy_input, "question": "different"}, legacy_auth)
    assert conflict_status == 409 and conflict_body.get("code") == "operation-conflict", "Legacy changed question did not conflict"
    assert call("/api/tasks", legacy_input, {**auth, "Idempotency-Key": "web-intent-v1-" + uuid.UUID(expired_op).hex})[2]["code"] == "intent-expired"
    assert snapshot() == settled
    legacy_op = str(uuid.uuid4())
    legacy_task = task_identity(tenant, company, owner, legacy_op)
    legacy_counts = effect_counts()
    assert call("/api/tasks", legacy_input, {**auth, "Idempotency-Key": "web-intent-v1-" + uuid.UUID(legacy_op).hex})[2]["taskId"] == legacy_task
    await_completed(legacy_task)
    require_one_graph(legacy_counts, settled, legacy_task)
    settled = snapshot()
    assert call("/api/tasks/intents", {**request, "operationId": legacy_op, "question": "different"})[2]["code"] == "operation-conflict"
    assert call("/api/tasks/intents/" + legacy_op)[0] == 404
    adopted = call("/api/tasks/intents", {**request, "operationId": legacy_op})[2]
    assert adopted["state"] == 1 and adopted["accepted"]["taskId"] == legacy_task and adopted["question"] == question
    assert call("/api/tasks/intents/" + legacy_op + "/submit", {"inputFingerprint": fingerprint})[2]["taskId"] == legacy_task and snapshot() == settled
    print("PASS native legacy reserved-key replay/conflicting body/expiry denial and exact compatible preexisting-task adoption without additional effects")

    # Observe actual runtime requests queued on the SAME shipping company lock,
    # then revoke authority outside that transaction before releasing admission.
    # No sleep-based assumption about which side of the fresh check won.
    def queued_admission(kind, revoke, restore):
        nonlocal settled
        queued_op = operation if kind == "replay" else str(uuid.uuid4())
        queued_path = "/api/tasks/intents" if kind == "prepare" else "/api/tasks/intents/" + queued_op + "/submit"
        payload = {**request, "operationId": queued_op} if kind == "prepare" else {"inputFingerprint": fingerprint}
        if kind == "execute":
            assert call("/api/tasks/intents", {**request, "operationId": queued_op})[0] == 200
        before_effects, before_intents = snapshot(), intent_snapshot()
        gate = "dbo.SubmissionGate_" + uuid.uuid4().hex
        resource = f"aioffice:membership:{tenant}:{company}"
        sql(f"USE AIOfficeLocal; CREATE TABLE {gate}(Released bit NOT NULL,OwnerSpid int NULL); INSERT {gate} VALUES(0,NULL);")
        query = f"""SET NOCOUNT ON; USE AIOfficeLocal; SET XACT_ABORT ON;
          UPDATE {gate} SET OwnerSpid=@@SPID; BEGIN TRANSACTION;
          DECLARE @result int; EXEC @result=sys.sp_getapplock @Resource=N'{resource}',@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=0;
          IF @result<0 THROW 51000,'Owned submission gate unavailable.',1;
          DECLARE @deadline datetime2=DATEADD(second,30,SYSUTCDATETIME());
          WHILE (SELECT Released FROM {gate} WITH(READUNCOMMITTED))=0 AND SYSUTCDATETIME()<@deadline WAITFOR DELAY '00:00:00.050';
          COMMIT TRANSACTION;"""
        locker = subprocess.Popen([*compose, "exec", "-T", "sql", "sh", "-c",
            'SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -I -b -m 1 -h -1 -W -i /dev/stdin'],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, env=environment)
        locker.stdin.write(query); locker.stdin.close(); locker.stdin = None
        executor, pending = None, None
        def await_condition(predicate, phase, seconds):
            deadline = time.monotonic() + seconds
            while time.monotonic() < deadline:
                if predicate(): return
                if locker.poll() is not None or pending is not None and pending.done():
                    raise AssertionError("Native submission " + phase + " ended before observed queued admission")
                time.sleep(.05)
            raise AssertionError("Native submission " + phase + " was not observed")
        try:
            await_condition(lambda: int(sql(f"""USE AIOfficeLocal; SELECT COUNT(*) FROM sys.dm_tran_locks
              WHERE request_session_id=(SELECT OwnerSpid FROM {gate}) AND resource_type=N'APPLICATION'
                AND resource_database_id=DB_ID() AND request_mode=N'X' AND request_status=N'GRANT';""")) > 0, "owned company lock", 8)
            executor = ThreadPoolExecutor(max_workers=1); pending = executor.submit(call, queued_path, payload)
            await_condition(lambda: int(sql(f"""USE AIOfficeLocal; SELECT COUNT(*) FROM sys.dm_exec_requests r
              JOIN sys.dm_exec_sessions s ON s.session_id=r.session_id
              JOIN sys.dm_tran_locks l ON l.request_session_id=r.session_id
              CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) t
              WHERE s.login_name=N'aioffice_runtime' AND r.blocking_session_id=(SELECT OwnerSpid FROM {gate})
                AND r.wait_type=N'LCK_M_X' AND l.resource_type=N'APPLICATION' AND l.resource_database_id=DB_ID()
                AND l.request_mode=N'X' AND l.request_status IN(N'WAIT',N'CONVERT') AND t.text LIKE N'%sp_getapplock%';""")) > 0, "runtime queued company lock", 3)
            sql(f"USE AIOfficeLocal; {revoke}; UPDATE {gate} SET Released=1;")
            status, _, body = pending.result(timeout=15)
            assert status == 403 and body in ("", None), f"Observed queued {kind} expected empty403, got HTTP{int(status)}"
        finally:
            sql(f"USE AIOfficeLocal; UPDATE {gate} SET Released=1;")
            try: locker.communicate(timeout=8)
            except subprocess.TimeoutExpired:
                locker.kill(); locker.communicate(timeout=5)
            if executor is not None: executor.shutdown(wait=True, cancel_futures=True)
            sql(f"USE AIOfficeLocal; {restore}; DROP TABLE {gate};")
        assert locker.returncode == 0 and snapshot() == before_effects and intent_snapshot() == before_intents
        counts = effect_counts()
        status, _, positive = call(queued_path, payload)
        assert status == (200 if kind == "prepare" else 202), "Queued admission restored positive failed"
        if kind == "execute":
            await_completed(positive["taskId"])
            require_one_graph(counts, before_effects, positive["taskId"])
            settled = snapshot()
        else:
            assert snapshot() == before_effects
            if kind == "replay": assert positive["taskId"] == expected_task and intent_snapshot() == before_intents

    member = f"TenantId='{tenant}' AND CompanyId='{company}' AND UserId='{owner}'"
    user = f"TenantId='{tenant}' AND Id='{owner}'"
    source_scope = scope + f" AND Id='{source}'"
    binding_scope = scope + " AND CanonicalReference=N'secretref://env/PILOT_ERP_CONNECTION' COLLATE Latin1_General_100_BIN2"
    revocations = [("membership", "CompanyMemberships", member, "IsActive"), ("global-user", "Users", user, "IsActive"),
        ("source-enabled", "DataSources", source_scope, "IsEnabled"), ("source-read", "DataSources", source_scope, "AllowRead"),
        ("binding", "DataSourceSecretBindings", binding_scope, "IsEnabled")]
    for name, table, predicate, column in revocations:
        assert sql(f"USE AIOfficeLocal; SELECT COUNT(*) FROM aioffice.{table} WHERE {predicate} AND {column}=1;") == "1"
        for kind in ("prepare", "execute", "replay"):
            queued_admission(kind, f"UPDATE aioffice.{table} SET {column}=0 WHERE {predicate}",
                f"UPDATE aioffice.{table} SET {column}=1 WHERE {predicate}")
        print("PASS native observed queued prepare/new execution/committed replay " + name + " revocation denies without effects and restores positive")

    # Block a real original-request read AFTER the initial authority checks.
    # RCSI bypasses row locks, so use an operator transaction's Sch-M and observe
    # the exact runtime Sch-S wait/query before revoking membership/global user.
    def queued_private_read(read_path, table, predicate):
        assert sql("SELECT is_read_committed_snapshot_on FROM sys.databases WHERE name=N'AIOfficeLocal';") == "1"
        object_id = int(sql("USE AIOfficeLocal; SELECT OBJECT_ID(N'aioffice.TaskEvents');"))
        assert object_id > 0
        gate = "dbo.SubmissionReadGate_" + uuid.uuid4().hex
        column = "SubmissionReadBarrier_" + uuid.uuid4().hex
        before_effects, before_intents = snapshot(), intent_snapshot()
        sql(f"USE AIOfficeLocal; CREATE TABLE {gate}(Released bit NOT NULL,OwnerSpid int NULL); INSERT {gate} VALUES(0,NULL);")
        query = f"""SET NOCOUNT ON; USE AIOfficeLocal; SET XACT_ABORT ON;
          UPDATE {gate} SET OwnerSpid=@@SPID; BEGIN TRANSACTION;
          ALTER TABLE aioffice.TaskEvents ADD [{column}] bit NULL;
          DECLARE @deadline datetime2=DATEADD(second,30,SYSUTCDATETIME());
          WHILE (SELECT Released FROM {gate} WITH(READUNCOMMITTED))=0 AND SYSUTCDATETIME()<@deadline WAITFOR DELAY '00:00:00.050';
          ROLLBACK TRANSACTION;"""
        locker = subprocess.Popen([*compose, "exec", "-T", "sql", "sh", "-c",
            'SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -I -b -m 1 -h -1 -W -i /dev/stdin'],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, env=environment)
        locker.stdin.write(query); locker.stdin.close(); locker.stdin = None
        executor, pending = None, None
        def await_condition(predicate, phase):
            deadline = time.monotonic() + 8
            while time.monotonic() < deadline:
                if predicate(): return
                if locker.poll() is not None or pending is not None and pending.done():
                    raise AssertionError("Native submission " + phase + " ended before observed private read")
                time.sleep(.05)
            raise AssertionError("Native submission " + phase + " was not observed")
        try:
            await_condition(lambda: int(sql(f"""USE AIOfficeLocal; SELECT COUNT(*) FROM sys.dm_tran_locks
              WHERE request_session_id=(SELECT OwnerSpid FROM {gate}) AND resource_type=N'OBJECT' AND resource_database_id=DB_ID()
                AND resource_associated_entity_id={object_id} AND request_mode=N'Sch-M' AND request_status=N'GRANT';""")) > 0, "owned private schema lock")
            executor = ThreadPoolExecutor(max_workers=1); pending = executor.submit(call, read_path)
            await_condition(lambda: int(sql(f"""USE AIOfficeLocal; SELECT COUNT(*) FROM sys.dm_exec_requests r
              JOIN sys.dm_exec_sessions s ON s.session_id=r.session_id JOIN sys.dm_tran_locks l ON l.request_session_id=r.session_id
              CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) t
              WHERE s.login_name=N'aioffice_runtime' AND r.blocking_session_id=(SELECT OwnerSpid FROM {gate}) AND r.wait_type=N'LCK_M_SCH_S'
                AND l.resource_type=N'OBJECT' AND l.resource_database_id=DB_ID() AND l.resource_associated_entity_id={object_id}
                AND l.request_mode=N'Sch-S' AND l.request_status IN(N'WAIT',N'CONVERT')
                AND t.text LIKE N'%TaskEvents%' AND t.text LIKE N'%PayloadJson%';""")) > 0, "runtime original-request private read")
            sql(f"USE AIOfficeLocal; UPDATE aioffice.{table} SET IsActive=0 WHERE {predicate}; UPDATE {gate} SET Released=1;")
            status, _, body = pending.result(timeout=15)
            assert status == 403 and body in ("", None), "Queued private intent read did not apply final authority fence"
        finally:
            sql(f"USE AIOfficeLocal; UPDATE {gate} SET Released=1;")
            try: locker.communicate(timeout=8)
            except subprocess.TimeoutExpired:
                locker.kill(); locker.communicate(timeout=5)
            if executor is not None: executor.shutdown(wait=True, cancel_futures=True)
            sql(f"USE AIOfficeLocal; UPDATE aioffice.{table} SET IsActive=1 WHERE {predicate}; DROP TABLE {gate};")
        assert locker.returncode == 0 and call(read_path)[0] == 200
        assert sql(f"USE AIOfficeLocal; SELECT COUNT(*) FROM sys.columns WHERE object_id={object_id} AND name=N'{column}';") == "0"
        assert snapshot() == before_effects and intent_snapshot() == before_intents

    for table, predicate in (("CompanyMemberships", member), ("Users", user)):
        for read_path in (path, "/api/tasks/intents?offset=0&limit=25"):
            queued_private_read(read_path, table, predicate)
    print("PASS native observed queued owner intent detail/list original-request reads apply final membership/global-user fences, empty403/restored200 with unchanged durable bytes")
