"""Shipping group references on the exact owned disposable SQL/RabbitMQ CI stack."""
from concurrent.futures import ThreadPoolExecutor
import json
import os
from pathlib import Path
import re
import subprocess
import time
import uuid


def require_owned(directory, api):
    if not (os.environ.get("CI") == "true" and os.environ.get("GITHUB_ACTIONS") == "true"
            and os.environ.get("RUNNER_TEMP") and directory.resolve() == (Path(os.environ["RUNNER_TEMP"]) / "aioffice-local").resolve()
            and api == "http://127.0.0.1:8080"):
        raise RuntimeError("Group reference proof requires the owned disposable GitHub CI fixture.")


def verify(*, directory, api, manifest, tenant, company, service, source, sql, compose, environment, pipeline):
    require_owned(directory, api)  # Before configuration, files, credentials, SQL or processes.
    tenant, company, service, source = (str(uuid.UUID(value)) for value in (tenant, company, service, source))
    installation = uuid.UUID(manifest["AIOFFICE_INSTALLATION_ID"]).hex
    scope = f"TenantId='{tenant}' AND CompanyId='{company}' AND BindingId='{source}'"
    grant = scope + f" AND ServiceId='{service}' AND Capability=2"
    suffix = uuid.uuid4().hex
    image = "aioffice-reference-proof-" + suffix
    name = "aioffice-reference-proof-" + suffix
    child_environment = dict(os.environ)
    password, broker_password = (manifest[key] for key in ("AIOFFICE_RUNTIME_PASSWORD", "AIOFFICE_RABBITMQ_PASSWORD"))
    assert all(re.fullmatch(r"[A-Za-z0-9_-]{32,128}", value) for value in (password, broker_password))
    child_environment.update({"AIOFFICE_OWNED_GROUP_REFERENCE_PROOF": "true",
        "AIOFFICE_GROUP_REFERENCE_PROOF_CONNECTION": "Server=sql;Database=AIOfficeLocal;User ID=aioffice_runtime;Password="
            + password + ";Encrypt=true;TrustServerCertificate=true",
        "AIOFFICE_GROUP_REFERENCE_PROOF_BROKER_PASSWORD": broker_password})
    command = ["docker", "run", "--rm", "--name", name, "--label", "aioffice.owned-proof=" + suffix,
        "--network", "aioffice-" + installation + "_default", "--read-only", "--cap-drop", "ALL",
        "--security-opt", "no-new-privileges:true", "--tmpfs", "/tmp:rw,noexec,nosuid,size=16m", "-i",
        "-e", "CI", "-e", "GITHUB_ACTIONS", "-e", "AIOFFICE_OWNED_GROUP_REFERENCE_PROOF",
        "-e", "AIOFFICE_GROUP_REFERENCE_PROOF_CONNECTION", "-e", "AIOFFICE_GROUP_REFERENCE_PROOF_BROKER_PASSWORD", image]
    events = sql(f"SELECT CONVERT(varchar(36),Id) FROM aioffice.GroupIngressOutbox WHERE {scope} ORDER BY CommittedSequence;").splitlines()
    events = [str(uuid.UUID(value.strip())) for value in events]
    assert len(events) == 2 and len(set(events)) == 2, "Expected two actual Core/spool committed references"
    hold = None

    def configuration(index):
        return json.dumps({"tenantId": tenant, "companyId": company, "serviceId": service, "sourceId": source, "eventId": events[index]})

    def run(mode, index=0):
        result = subprocess.run([*command, mode], input=configuration(index), env=child_environment,
            capture_output=True, text=True, timeout=170)
        lines = result.stdout.splitlines()
        expected = {"publish": "PASS owned reference runtime shipping outbox publication",
            "consume-replay": "PASS owned reference runtime redelivery original SQL receipt and broker ACK",
            "duplicates": "PASS owned reference runtime100 concurrent original inbox receipts"}.get(mode, "PASS owned reference runtime refusal " + mode)
        assert result.returncode == 0 and lines == [expected], "Owned reference executable failed at " + mode

    def digest(table, order, projection="*"):
        value = sql(f"SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),COALESCE("
            f"(SELECT {projection} FROM aioffice.{table} WHERE {scope} ORDER BY {order} FOR JSON PATH,INCLUDE_NULL_VALUES),N'[]'))),2);")
        assert re.fullmatch(r"[0-9A-F]{64}", value)
        return value

    def protected_graph():
        # Only this separate native source's delivery fields are intentionally
        # mutable. Preserve every byte of the other five tables and all original
        # outbox identity/commit fields; the parent still checks its full6 graph.
        tables = [("GroupMessages", "Id"), ("GroupMessageRevisions", "CommittedSequence"),
            ("GroupIngressReceipts", "EventIdentityHash"), ("GroupSourceStates", "BindingId"), ("GroupCoverageGaps", "Id")]
        return [digest(table, order) for table, order in tables] + [digest("GroupIngressOutbox", "CommittedSequence",
            "TenantId,CompanyId,BindingId,Id,MessageId,Revision,CommittedSequence")]

    def full_graph():
        return protected_graph() + [digest("GroupIngressOutbox", "CommittedSequence"), digest("GroupIngressInbox", "CommittedSequence")]

    def count():
        return int(sql(f"SELECT COUNT(*) FROM aioffice.GroupIngressInbox WHERE {scope};"))

    def queue_counts():
        queue = f"minhhuy.group-ingress.v1.{uuid.UUID(tenant).hex}.{uuid.UUID(company).hex}.{uuid.UUID(service).hex}"
        result = subprocess.run([*compose, "exec", "-T", "rabbitmq", "rabbitmqctl", "-q", "list_queues",
            "name", "messages_ready", "messages_unacknowledged", "--formatter=json"],
            env=environment, capture_output=True, text=True, timeout=20)
        assert result.returncode == 0, "Owned broker queue observation failed"
        rows = json.loads(result.stdout)
        row = next(item for item in rows if item["name"] == queue)
        values = (row["messages_ready"], row["messages_unacknowledged"])
        assert all(type(value) is int and value >= 0 for value in values)
        return values

    def wait(predicate, seconds=30):
        deadline = time.monotonic() + seconds
        while time.monotonic() < deadline:
            if predicate(): return
            time.sleep(.1)
        raise AssertionError("Owned reference observation deadline exceeded")

    def kill_owned():
        inspected = subprocess.run(["docker", "inspect", "--format", '{{.Id}}|{{index .Config.Labels "aioffice.owned-proof"}}', name],
            capture_output=True, text=True, timeout=10)
        if inspected.returncode: return False
        identity, label = inspected.stdout.strip().split("|", 1)
        assert re.fullmatch(r"[0-9a-f]{64}", identity) and label == suffix, "Owned reference container identity mismatch"
        killed = subprocess.run(["docker", "kill", identity], capture_output=True, text=True, timeout=10)
        assert killed.returncode == 0, "Owned reference container kill failed"
        return True

    def queued_extract_revoke():
        # Observe the actual runtime waiter on this source's SQL row lock, then
        # revoke Extract before releasing it. No timing-only concurrency claim.
        gate = "dbo.GroupInboxGate_" + suffix
        sql(f"CREATE TABLE {gate}(Released bit NOT NULL,OwnerSpid int NULL); INSERT {gate} VALUES(0,NULL);")
        query = f"""SET NOCOUNT ON; USE AIOfficeLocal; SET XACT_ABORT ON;
          UPDATE {gate} SET OwnerSpid=@@SPID; BEGIN TRANSACTION;
          SELECT BindingId FROM aioffice.GroupSourceStates WITH(UPDLOCK,HOLDLOCK) WHERE {scope};
          DECLARE @deadline datetime2=DATEADD(second,30,SYSUTCDATETIME());
          WHILE (SELECT Released FROM {gate} WITH(READUNCOMMITTED))=0 AND SYSUTCDATETIME()<@deadline WAITFOR DELAY '00:00:00.050';
          COMMIT TRANSACTION;"""
        locker = subprocess.Popen([*compose, "exec", "-T", "sql", "sh", "-c",
            'SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -I -b -m 1 -h -1 -W -i /dev/stdin'],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, env=environment)
        locker.stdin.write(query); locker.stdin.close(); locker.stdin = None
        executor = ThreadPoolExecutor(max_workers=1)
        try:
            wait(lambda: int(sql(f"SELECT COUNT(*) FROM sys.dm_tran_locks WHERE request_session_id=(SELECT OwnerSpid FROM {gate})"
                " AND resource_database_id=DB_ID() AND resource_type IN(N'KEY',N'PAGE') AND request_status=N'GRANT' AND request_mode IN(N'U',N'X',N'RangeS-U');")) > 0, 8)
            pending = executor.submit(run, "deny", 1)
            wait(lambda: int(sql(f"""SELECT COUNT(*) FROM sys.dm_exec_requests r
              JOIN sys.dm_exec_sessions s ON s.session_id=r.session_id
              JOIN sys.dm_tran_locks l ON l.request_session_id=r.session_id
              JOIN sys.dm_tran_locks held ON held.request_session_id=(SELECT OwnerSpid FROM {gate})
                AND held.resource_type=l.resource_type AND held.resource_database_id=l.resource_database_id
                AND held.resource_associated_entity_id=l.resource_associated_entity_id
                AND held.resource_description=l.resource_description AND held.request_status=N'GRANT'
              WHERE s.login_name=N'aioffice_runtime' AND r.blocking_session_id=(SELECT OwnerSpid FROM {gate})
                AND r.wait_type LIKE N'LCK_M_%' AND l.resource_database_id=DB_ID()
                AND l.resource_type IN(N'KEY',N'PAGE') AND l.request_status IN(N'WAIT',N'CONVERT');""")) > 0, 12)
            sql(f"UPDATE aioffice.GroupServiceGrants SET IsEnabled=0 WHERE {grant}; UPDATE {gate} SET Released=1;")
            pending.result(timeout=25)
        finally:
            try: sql(f"UPDATE {gate} SET Released=1;")
            finally:
                try: locker.communicate(timeout=10)
                except subprocess.TimeoutExpired:
                    locker.kill(); locker.communicate(timeout=5)
                executor.shutdown(wait=True, cancel_futures=True)
                sql(f"UPDATE aioffice.GroupServiceGrants SET IsEnabled=1 WHERE {grant}; DROP TABLE {gate};")
        assert locker.returncode == 0

    before = protected_graph()
    portal = sql("SELECT CONCAT((SELECT COUNT(*) FROM aioffice.Users),N'|',(SELECT COUNT(*) FROM aioffice.Tasks),N'|',"
        "(SELECT COUNT(*) FROM aioffice.TaskDispatches),N'|',(SELECT COUNT(*) FROM aioffice.TaskCheckpoints));")
    assert count() == 0
    sql(f"INSERT aioffice.GroupServiceGrants(TenantId,CompanyId,ServiceId,BindingId,Capability,Version,IsEnabled)"
        f" VALUES('{tenant}','{company}','{service}','{source}',2,1,1);")
    built = subprocess.run(["docker", "build", "-f", "tests/GroupReference.RuntimeProof/Dockerfile", "-t", image, "."],
        capture_output=True, text=True, timeout=300)
    assert built.returncode == 0, "Owned group reference executable build failed"
    try:
        run("publish")
        assert queue_counts() == (1, 0) and count() == 0 and protected_graph() == before
        hold = subprocess.Popen([*command, "consume-hold"], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.PIPE, text=True, env=child_environment)
        hold.stdin.write(configuration(0)); hold.stdin.close(); hold.stdin = None
        wait(lambda: count() == 1 and queue_counts() == (0, 1))
        assert count() == 1 and queue_counts() == (0, 1) and protected_graph() == before
        original_receipt = digest("GroupIngressInbox", "CommittedSequence")
        assert kill_owned(), "Owned reference child missing before committed lost-ACK kill"
        held_stdout, _ = hold.communicate(timeout=15)
        assert hold.returncode == 137 and held_stdout.splitlines() == ["CHECKPOINT owned reference inbox committed before broker ACK"]
        wait(lambda: queue_counts() == (1, 0))
        run("consume-replay")
        wait(lambda: queue_counts() == (0, 0))
        assert count() == 1 and digest("GroupIngressInbox", "CommittedSequence") == original_receipt and protected_graph() == before
        print("PASS actual reference SQL inbox commit/lost broker ACK/owned child death/redelivery original receipt/one effect and empty queue", flush=True)
        run("duplicates")
        assert count() == 1 and digest("GroupIngressInbox", "CommittedSequence") == original_receipt and protected_graph() == before
        print("PASS actual reference100 concurrent original SQL inbox receipts with unchanged private graph and batch cursor", flush=True)

        unchanged = full_graph()
        sql(f"ALTER TABLE aioffice.GroupIngressInbox ADD CONSTRAINT CK_CiGroupInboxRollback CHECK(BindingId<>'{source}' OR CommittedSequence<>2);")
        try: run("rollback", 1)
        finally: sql("ALTER TABLE aioffice.GroupIngressInbox DROP CONSTRAINT CK_CiGroupInboxRollback;")
        assert full_graph() == unchanged
        sql(f"UPDATE aioffice.GroupServiceGrants SET IsEnabled=0 WHERE {grant};")
        try: run("deny", 1)
        finally: sql(f"UPDATE aioffice.GroupServiceGrants SET IsEnabled=1 WHERE {grant};")
        assert full_graph() == unchanged
        print("PASS actual reference SQL inbox insert rollback and current Extract revoke retain exact full graph/backlog", flush=True)
        queued_extract_revoke()
        assert full_graph() == unchanged
        print("PASS actual reference observed source-locked queued Extract revoke refuses unchanged graph before current restoration", flush=True)

        sql("REVOKE UPDATE ON OBJECT::aioffice.GroupIngressInbox(ReceivedAtUtc) FROM aioffice_binding_runtime;"
            " GRANT UPDATE ON OBJECT::aioffice.GroupIngressInbox(ReceivedAtUtc) TO aioffice_runtime;")
        try:
            assert sql("EXECUTE AS LOGIN=N'aioffice_runtime'; SELECT HAS_PERMS_BY_NAME(N'aioffice.GroupIngressInbox',N'OBJECT',N'UPDATE',N'ReceivedAtUtc',N'COLUMN'); REVERT;") == "1"
            run("unsafe", 1)
        finally:
            sql("REVOKE UPDATE ON OBJECT::aioffice.GroupIngressInbox(ReceivedAtUtc) FROM aioffice_runtime;"
                " DENY UPDATE ON OBJECT::aioffice.GroupIngressInbox(ReceivedAtUtc) TO aioffice_binding_runtime;")
        assert full_graph() == unchanged
        print("PASS actual reference unsafe effective inbox column rights refuse before commit with exact owned restore", flush=True)

        pipeline("enable")
        wait(lambda: count() == 2 and queue_counts() == (0, 0), 60)
        assert protected_graph() == before
        stable = full_graph()
        pipeline("restart")
        wait(lambda: queue_counts() == (0, 0))
        assert count() == 2 and full_graph() == stable
        assert sql("SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE login_name=N'aioffice_runtime' AND status=N'sleeping'"
            " AND (transaction_isolation_level<>2 OR open_transaction_count<>0);") == "0"
        assert portal == sql("SELECT CONCAT((SELECT COUNT(*) FROM aioffice.Users),N'|',(SELECT COUNT(*) FROM aioffice.Tasks),N'|',"
            "(SELECT COUNT(*) FROM aioffice.TaskDispatches),N'|',(SELECT COUNT(*) FROM aioffice.TaskCheckpoints));")
        print("PASS actual shipping Core producer/Worker consumer DI and broker delivery/restart preserve SQL graph/cursor and no fake portal task", flush=True)
    finally:
        try:
            if hold is not None and hold.poll() is None:
                kill_owned()
                hold.communicate(timeout=15)
        finally:
            try: pipeline("disable")
            finally: subprocess.run(["docker", "image", "rm", image], capture_output=True, text=True, timeout=30)
