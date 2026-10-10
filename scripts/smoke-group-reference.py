"""Shipping group references on the exact owned disposable SQL/RabbitMQ CI stack."""
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime
import base64
import hashlib
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


def validated_brain_fixture(raw):
    # Pure closed metadata/cipher fixture decoding; never accepts SQL, private
    # clear text or a key. Called only after the outer owned-resource guard.
    assert isinstance(raw, str) and 0 < len(raw.encode("utf-8")) <= 8192
    def unique(pairs):
        result = {}
        for key, value in pairs:
            assert key not in result
            result[key] = value
        return result
    value = json.loads(raw, object_pairs_hook=unique)
    ids = ("requestId", "glossaryId", "publisherId", "batchId", "messageId")
    numbers = ("claimEpoch", "sourceVersion", "deletionGeneration", "messageRevision", "credentialEpoch", "grantVersion", "accountVersion")
    assert isinstance(value, dict) and set(value) == set(ids + numbers + (
        "createdAtUtc", "requestEnvelope", "glossaryEnvelope", "requestHash", "glossaryHash", "sourceSetHash"))
    for field in ids:
        assert isinstance(value[field], str) and str(uuid.UUID(value[field])) == value[field] and uuid.UUID(value[field]).int != 0
    assert value["requestId"] != value["glossaryId"]
    for field in numbers:
        assert type(value[field]) is int and (0 if field == "deletionGeneration" else 1) <= value[field] <= 9223372036854775807
    assert value["claimEpoch"] == 4
    assert isinstance(value["createdAtUtc"], str) and re.fullmatch(r"\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{7}\+00:00", value["createdAtUtc"])
    datetime.fromisoformat(value["createdAtUtc"])
    for kind in ("request", "glossary"):
        envelope = value[kind + "Envelope"]
        assert isinstance(envelope, str) and re.fullmatch(r"[0-9A-F]{60,2048}", envelope) and len(envelope) % 2 == 0
        cipher = bytes.fromhex(envelope)
        assert cipher[0] == 1 and hashlib.sha256(cipher).hexdigest().upper() == value[kind + "Hash"]
    assert isinstance(value["sourceSetHash"], str) and re.fullmatch(r"[0-9A-F]{64}", value["sourceSetHash"])
    assert value["sourceSetHash"] == hashlib.sha256(f"{value['messageId']}/{value['messageRevision']}".encode("ascii")).hexdigest().upper()
    return value


def require_reference_result(result, mode, expected_lines):
    # Preserve exact successful output. Failure diagnostics are closed tokens,
    # never arbitrary child stdout/stderr or exception/connection information.
    lines = result.stdout.splitlines()
    if result.returncode == 0 and lines == expected_lines:
        return
    detail = ""
    phases = {"setup", "claim", "source", "preparation", "brain", "dependencies", "commit", "savepoint",
        "flush", "rollback", "expirychecks", "clockrollback", "replay", "duplicatechecks", "unknown"}
    prefix = "FAIL owned NoWork runtime phase-"
    if (mode in {"no-work-expiry", "no-work-commit", "no-work-mars"} and result.returncode == 1
            and len(lines) == 2 and lines[1] == "FAIL owned reference runtime " + mode and lines[0].startswith(prefix)):
        phase = lines[0][len(prefix):]
        if phase in phases:
            detail = "; phase=" + phase
    exit_code = str(result.returncode) if type(result.returncode) is int and -255 <= result.returncode <= 255 else "unknown"
    raise AssertionError("Owned reference executable failed at " + mode + "; exit=" + exit_code + detail)


def close_owned_reference_child(child, kill_owned):
    # The caller has already guarded/created this exact owned child. A failed
    # observation is never evidence that it stopped. Attempt every independent
    # bounded closure step and preserve the first failure for the outer cleanup.
    if child is None:
        return
    failure = None
    running = True
    try:
        if child.stdin is not None:
            child.stdin.close()
    except BaseException as error:
        failure = error
    finally:
        child.stdin = None  # communicate must not flush a closed/failed input.
    try:
        running = child.poll() is None
    except BaseException as error:
        if failure is None: failure = error
    if running:
        try: kill_owned()  # This callback inspects the exact container ID/label.
        except BaseException as error:
            if failure is None: failure = error
    try:
        child.communicate(timeout=15)
    except BaseException as error:
        if failure is None: failure = error
        # Drain/setup failure can race container creation or a lost kill reply.
        # Re-inspect ownership before the second kill, then always drain again.
        try: kill_owned()
        except BaseException as error:
            if failure is None: failure = error
        try: child.communicate(timeout=5)
        except BaseException as error:
            if failure is None: failure = error
    if failure is not None: raise failure


def temporary_sql(sql, setup, restore, action):
    # Setup may apply even if its reply is lost. Restoration owns that boundary,
    # including setup errors, and never replaces the first meaningful failure.
    failure = None
    try:
        sql(setup)
        action()
    except BaseException as error:
        failure = error
    finally:
        for statement in [restore] if isinstance(restore, str) else restore:
            try: sql(statement)
            except BaseException as error:
                if failure is None: failure = error
    if failure is not None: raise failure


def reference_statistics_command(command):
    # The held proof child must remain alive while statistics are measured.
    # A second docker run cannot reuse its --name. Preserve every owned guard,
    # label, network, credential-reference and resource argument otherwise.
    assert isinstance(command, list) and command.count("--name") == 1 and command.count("--label") == 1
    name_index, label_index = command.index("--name") + 1, command.index("--label") + 1
    assert name_index < len(command) and label_index < len(command)
    name = command[name_index]
    assert isinstance(name, str) and re.fullmatch(r"aioffice-reference-proof-[0-9a-f]{32}", name)
    suffix = name.removeprefix("aioffice-reference-proof-")
    assert command[label_index] == "aioffice.owned-proof=" + suffix
    independent = command.copy()
    independent[name_index] = "aioffice-reference-stats-" + suffix
    return independent


def wait_reference_statistics(broker_stats, wait, *, ack, deliver, phase):
    observed = None

    def coherent():
        nonlocal observed
        observed = broker_stats()
        return observed["ack"] == ack and observed["deliver"] == deliver and observed["consumers"] == 1

    # ACK, delivery and consumer management statistics need not become visible
    # together. One complete snapshot must meet all original exact conditions.
    try:
        wait(coherent)  # Retain the existing30-second bound.
    except AssertionError as error:
        if observed is None: raise
        raise AssertionError(f"Owned reference {phase} observation failed: "
            f"ack={observed['ack']}, expected_ack={ack}, "
            f"deliver={observed['deliver']}, expected_deliver={deliver}, consumers={observed['consumers']}") from error
    return observed


def verify_pending_broker_restart(*, directory, api, pause_worker, resume_worker, broker_state,
        restart_broker, queue_counts, broker_stats, publish, inspect_pending, full_graph, count, wait):
    require_owned(directory, api)  # Before any callback or process/service change.
    original = full_graph()
    assert count() == 2 and queue_counts() == (0, 0)
    previous = broker_state()
    assert previous[2] is True
    failure = None
    baseline = None
    try:
        # Restoration owns a stop whose successful reply may be lost.
        pause_worker()
        wait(lambda: broker_stats()["consumers"] == 0)
        assert queue_counts() == (0, 0) and count() == 2 and full_graph() == original
        publish()  # Shipping mandatory/persistent confirmed ORIGINAL reference.
        assert queue_counts() == (1, 0) and count() == 2 and full_graph() == original
        inspect_pending()  # Exact persistent properties/reference; requeue, no SQL effects.
        wait(lambda: queue_counts() == (1, 0))
        assert count() == 2 and full_graph() == original
        restart_broker(previous[0])  # Only the inspected owned container ID.

        def restarted():
            current = broker_state()
            return current[0] == previous[0] and current[1] > previous[1] and current[2] is True
        wait(restarted, 60)

        def statistics_ready():
            try: statistics = broker_stats()
            except AssertionError: return False  # Bounded actual management readiness.
            assert statistics["consumers"] == 0
            return True
        wait(statistics_ready, 60)
        # No publication after restart may hide a lost durable reference.
        assert queue_counts() == (1, 0) and count() == 2 and full_graph() == original
        inspect_pending()
        wait(lambda: queue_counts() == (1, 0))
        assert count() == 2 and full_graph() == original
        baseline = broker_stats()  # Counters may reset with the broker process.
        assert baseline["consumers"] == 0
    except BaseException as error:
        failure = error
    finally:
        try: resume_worker()
        except BaseException as error:
            if failure is None: failure = error
    if failure is not None: raise failure
    resumed = None

    def original_delivery_acknowledged():
        nonlocal resumed
        resumed = broker_stats()
        return (resumed["ack"] == baseline["ack"] + 1
            and resumed["deliver"] == baseline["deliver"] + 1 and resumed["consumers"] == 1)

    # Management observations can expose ACK before delivery/consumer statistics.
    # Require every original exact delta in one snapshot within the existing bound.
    try:
        wait(original_delivery_acknowledged)
    except AssertionError as error:
        if resumed is None: raise
        raise AssertionError("Owned restarted reference delivery observation failed: "
            f"ack_delta={resumed['ack'] - baseline['ack']}, "
            f"deliver_delta={resumed['deliver'] - baseline['deliver']}, consumers={resumed['consumers']}") from error
    assert queue_counts() == (0, 0) and count() == 2 and full_graph() == original


def require_source_key(source_key):
    try:
        if not isinstance(source_key, str) or len(source_key) != 44:
            raise ValueError()
        decoded = base64.b64decode(source_key, validate=True)
        if len(decoded) != 32 or base64.b64encode(decoded).decode("ascii") != source_key:
            raise ValueError()
    except Exception:
        raise RuntimeError("Owned reference source key is unavailable.") from None


def verify(*, directory, api, manifest, tenant, company, service, source, sql, compose, environment, pipeline, source_key=None, prepare_effect_source=None):
    require_owned(directory, api)  # Before configuration, files, credentials, SQL or processes.
    require_source_key(source_key)
    assert callable(prepare_effect_source), "Owned clean effect source preparation is unavailable"
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
        "AIOFFICE_GROUP_REFERENCE_PROOF_BROKER_PASSWORD": broker_password,
        "AIOFFICE_GROUP_REFERENCE_PROOF_SOURCE_KEY": source_key})
    # The crash proof must be a child of Docker's init. Namespace PID1 can
    # ignore its own SIGKILL and wait until cancellation instead of dying.
    command = ["docker", "run", "--init", "--rm", "--name", name, "--label", "aioffice.owned-proof=" + suffix,
        "--network", "aioffice-" + installation + "_default", "--read-only", "--cap-drop", "ALL",
        "--security-opt", "no-new-privileges:true", "--tmpfs", "/tmp:rw,noexec,nosuid,size=16m", "-i",
        "-e", "CI", "-e", "GITHUB_ACTIONS", "-e", "AIOFFICE_OWNED_GROUP_REFERENCE_PROOF",
        "-e", "AIOFFICE_GROUP_REFERENCE_PROOF_CONNECTION", "-e", "AIOFFICE_GROUP_REFERENCE_PROOF_BROKER_PASSWORD",
        "-e", "AIOFFICE_GROUP_REFERENCE_PROOF_SOURCE_KEY", image]
    events = sql(f"SELECT CONVERT(varchar(36),Id) FROM aioffice.GroupIngressOutbox WHERE {scope} ORDER BY CommittedSequence;").splitlines()
    events = [str(uuid.UUID(value.strip())) for value in events]
    assert len(events) == 2 and len(set(events)) == 2, "Expected two actual Core/spool committed references"
    hold = None
    failure = None
    def cleanup(action):
        nonlocal failure
        try: action()
        except BaseException as error:
            if failure is None: failure = error

    def configuration(index):
        return json.dumps({"tenantId": tenant, "companyId": company, "serviceId": service, "sourceId": source, "eventId": events[index]})

    def run(mode, index=0):
        result = subprocess.run([*command, mode], input=configuration(index), env=child_environment,
            capture_output=True, text=True, timeout=170)
        lines = result.stdout.splitlines()
        expected = {"publish": "PASS owned reference runtime shipping outbox publication",
            "recovery-startup": "PASS owned reference runtime recovery-only startup SQL permission and inert DI",
            "publish-existing": "PASS owned reference runtime shipping original accepted reference publication",
            "inspect-pending": "PASS owned reference runtime exact persistent original queued reference retained",
            "consume-replay": "PASS owned reference runtime redelivery original SQL receipt and broker ACK",
            "duplicates": "PASS owned reference runtime100 concurrent original inbox receipts",
            "claim-replay": "PASS owned claim runtime100 concurrent original receipts never renew expired lease",
            "claim-fence": "PASS owned claim runtime actual expiry monotone replacement active contention and stale handle refusal",
            "claim-deny": "PASS owned claim runtime refusal claim-deny",
            "claim-unsafe": "PASS owned claim runtime refusal claim-unsafe",
            "claim-rollback": "PASS owned claim runtime refusal claim-rollback",
            "source-read": "PASS owned source runtime actual Core protected originals scoped configured keys and current context without portal identity",
            "source-deny": "PASS owned source runtime current Extract denies before protected keys",
            "source-foreign": "PASS owned source runtime foreign selected identity refuses before protected keys",
            "source-expiry": "PASS owned source runtime controlled key-await expiry commits SQL witness and refuses context after clock rollback",
            "brain-read": "PASS owned brain runtime actual SQL request glossary exact evidence configured keys and final current context",
            "brain-foreign": "PASS owned brain runtime selected identity or glossary policy refuses before keys",
            "brain-policy-deny": "PASS owned brain runtime selected identity or glossary policy refuses before keys",
            "brain-deny": "PASS owned brain runtime current Extract refuses before keys",
            "brain-expiry": "PASS owned brain runtime controlled key-await expiry commits SQL witness and denies after clock rollback",
            "no-work-expiry": "PASS owned NoWork runtime flushed two SQL effects rollback with source lock retained clean detach witness only and clock rollback denial",
            "no-work-commit": "PASS owned NoWork runtime actual atomic SQL receipt disposition exact original replay and new nonce duplicate refusal",
            "no-work-mars": "PASS owned NoWork runtime MARS refuses before connection keys or effects",
            "note-expiry": "PASS owned note runtime eleven flushed SQL effects rollback with source lock retained clean detach witness only and clock rollback denial",
            "note-key-expiry": "PASS owned note runtime configured write key outside SQL expiry witness only without effects and clock rollback denial",
            "note-commit": "PASS owned note runtime protected two notes literal evidence atomic NotesCommitted exact original replay changed proposal and new nonce refusal",
            "work-schema": "PASS owned work schema runtime migrated empty scoped brain and effective least privilege",
            "work-unsafe": "PASS owned work schema runtime unsafe effective permission refusal",
            "allocation-replay": "PASS owned allocation runtime100 concurrent original receipts and caught-up cursor",
            "allocation-deny": "PASS owned allocation runtime refusal allocation-deny",
            "allocation-unsafe": "PASS owned allocation runtime refusal allocation-unsafe",
            "allocation-rollback": "PASS owned allocation runtime refusal allocation-rollback"}.get(mode, "PASS owned reference runtime refusal " + mode)
        expected_lines = [expected] if mode != "claim-fence" else [
            "PASS owned claim runtime durable SQL expiry witness refuses original nonce and handle after clock rollback", expected]
        require_reference_result(result, mode, expected_lines)

    def broker_stats():
        result = subprocess.run([*reference_statistics_command(command), "statistics"], input=configuration(0), env=child_environment,
            capture_output=True, text=True, timeout=30)
        assert result.returncode == 0, f"Owned reference broker statistics unavailable exit={result.returncode}"
        value = json.loads(result.stdout)
        assert set(value) == {"ack", "deliver", "consumers"} and all(type(number) is int and number >= 0 for number in value.values())
        return value

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

    def service_identity(service_name):
        assert service_name in ("rabbitmq", "agent-worker")
        selected = subprocess.run([*compose, "ps", "--all", "--quiet", service_name],
            env=environment, capture_output=True, text=True, timeout=15)
        identity = selected.stdout.strip()
        assert selected.returncode == 0 and re.fullmatch(r"[0-9a-f]{64}", identity)
        inspected = subprocess.run(["docker", "inspect", "--format",
            '{{.Id}}|{{.State.StartedAt}}|{{.State.Running}}|{{index .Config.Labels "com.docker.compose.project"}}|{{index .Config.Labels "com.docker.compose.service"}}', identity],
            capture_output=True, text=True, timeout=15)
        assert inspected.returncode == 0
        fields = inspected.stdout.strip().split("|")
        assert len(fields) == 5 and fields[0] == identity and fields[3:] == ["aioffice-" + installation, service_name]
        assert re.fullmatch(r"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,9})?Z", fields[1]) and fields[2] in ("true", "false")
        return identity, datetime.fromisoformat(fields[1].replace("Z", "+00:00")), fields[2] == "true"

    def broker_state():
        identity, started, running = service_identity("rabbitmq")
        health = subprocess.run(["docker", "inspect", "--format", '{{.State.Health.Status}}', identity],
            capture_output=True, text=True, timeout=15)
        assert health.returncode == 0 and health.stdout.strip() in ("healthy", "starting", "unhealthy")
        return identity, started, running and health.stdout.strip() == "healthy"

    def restart_broker(identity):
        assert service_identity("rabbitmq")[0] == identity
        result = subprocess.run(["docker", "restart", "-t", "10", identity],
            capture_output=True, text=True, timeout=60)
        assert result.returncode == 0, "Owned reference broker restart failed"

    def worker_control(operation, identity):
        assert operation in ("stop", "start") and service_identity("agent-worker")[0] == identity
        result = subprocess.run([*compose, operation, "agent-worker"], env=environment,
            capture_output=True, text=True, timeout=45)
        assert result.returncode == 0, "Owned reference worker restoration/control failed"

    def kill_owned():
        inspected = subprocess.run(["docker", "inspect", "--type", "container", "--format", '{{.Id}}|{{index .Config.Labels "aioffice.owned-proof"}}', name],
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
        query = f"""SET NOCOUNT ON; USE AIOfficeLocal; SET XACT_ABORT ON;
          UPDATE {gate} SET OwnerSpid=@@SPID; BEGIN TRANSACTION;
          SELECT BindingId FROM aioffice.GroupSourceStates WITH(UPDLOCK,HOLDLOCK) WHERE {scope};
          DECLARE @deadline datetime2=DATEADD(second,30,SYSUTCDATETIME());
          WHILE (SELECT Released FROM {gate} WITH(READUNCOMMITTED))=0 AND SYSUTCDATETIME()<@deadline WAITFOR DELAY '00:00:00.050';
          COMMIT TRANSACTION;"""
        locker = None
        executor = None
        failure = None
        def cleanup(action):
            nonlocal failure
            try: action()
            except BaseException as error:
                if failure is None: failure = error

        try:
            sql(f"CREATE TABLE {gate}(Released bit NOT NULL,OwnerSpid int NULL); INSERT {gate} VALUES(0,NULL);")
            locker = subprocess.Popen([*compose, "exec", "-T", "sql", "sh", "-c",
                'SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -I -b -m 1 -h -1 -W -i /dev/stdin'],
                stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, env=environment)
            locker.stdin.write(query); locker.stdin.close(); locker.stdin = None
            executor = ThreadPoolExecutor(max_workers=1)
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
        except BaseException as error:
            failure = error
        finally:
            cleanup(lambda: sql(f"IF OBJECT_ID(N'{gate}',N'U') IS NOT NULL UPDATE {gate} SET Released=1;"))
            if locker is not None:
                if locker.stdin is not None:
                    cleanup(locker.stdin.close)
                    locker.stdin = None
                def drain():
                    nonlocal failure
                    try: locker.communicate(timeout=10)
                    except BaseException as error:
                        if failure is None: failure = error
                        cleanup(locker.kill)
                        cleanup(lambda: locker.communicate(timeout=5))
                        raise
                cleanup(drain)
            if executor is not None: cleanup(lambda: executor.shutdown(wait=True, cancel_futures=True))
            cleanup(lambda: sql(f"UPDATE aioffice.GroupServiceGrants SET IsEnabled=1 WHERE {grant};"))
            cleanup(lambda: sql(f"IF OBJECT_ID(N'{gate}',N'U') IS NOT NULL DROP TABLE {gate};"))
        if failure is not None: raise failure
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
        startup_graph = full_graph()
        run("recovery-startup")
        assert full_graph() == startup_graph and count() == 0
        print("PASS actual recovery-only Worker startup SQL permission and validated inert DI with unchanged full graph", flush=True)
        run("publish")
        assert queue_counts() == (1, 0) and count() == 0 and protected_graph() == before
        hold = subprocess.Popen([*command, "consume-hold"], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.PIPE, text=True, env=child_environment)
        hold.stdin.write(configuration(0)); hold.stdin.close(); hold.stdin = None
        wait(lambda: count() == 1 and queue_counts() == (0, 1))
        assert count() == 1 and queue_counts() == (0, 1) and protected_graph() == before
        original_receipt = digest("GroupIngressInbox", "CommittedSequence")
        # Observe this actual held channel before killing it. Management
        # counters are sampled; later snapshots cannot recreate an unobserved
        # short-lived channel. Every original restart count stays exact.
        wait_reference_statistics(broker_stats, wait, ack=0, deliver=1, phase="before owned held delivery kill")
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
        temporary_sql(sql,
            f"ALTER TABLE aioffice.GroupIngressInbox ADD CONSTRAINT CK_CiGroupInboxRollback CHECK(BindingId<>'{source}' OR CommittedSequence<>2);",
            "IF OBJECT_ID(N'aioffice.CK_CiGroupInboxRollback',N'C') IS NOT NULL ALTER TABLE aioffice.GroupIngressInbox DROP CONSTRAINT CK_CiGroupInboxRollback;",
            lambda: run("rollback", 1))
        assert full_graph() == unchanged
        temporary_sql(sql, f"UPDATE aioffice.GroupServiceGrants SET IsEnabled=0 WHERE {grant};",
            f"UPDATE aioffice.GroupServiceGrants SET IsEnabled=1 WHERE {grant};", lambda: run("deny", 1))
        assert full_graph() == unchanged
        print("PASS actual reference SQL inbox insert rollback and current Extract revoke retain exact full graph/backlog", flush=True)
        queued_extract_revoke()
        assert full_graph() == unchanged
        print("PASS actual reference observed source-locked queued Extract revoke refuses unchanged graph before current restoration", flush=True)

        def unsafe_column():
            assert sql("EXECUTE AS LOGIN=N'aioffice_runtime'; SELECT HAS_PERMS_BY_NAME(N'aioffice.GroupIngressInbox',N'OBJECT',N'UPDATE',N'ReceivedAtUtc',N'COLUMN'); REVERT;") == "1"
            run("unsafe", 1)
        temporary_sql(sql,
            "REVOKE UPDATE ON OBJECT::aioffice.GroupIngressInbox(ReceivedAtUtc) FROM aioffice_binding_runtime;"
                " GRANT UPDATE ON OBJECT::aioffice.GroupIngressInbox(ReceivedAtUtc) TO aioffice_runtime;",
            ["REVOKE UPDATE ON OBJECT::aioffice.GroupIngressInbox(ReceivedAtUtc) FROM aioffice_runtime;",
                "DENY UPDATE ON OBJECT::aioffice.GroupIngressInbox(ReceivedAtUtc) TO aioffice_binding_runtime;"], unsafe_column)
        assert full_graph() == unchanged
        print("PASS actual reference unsafe effective inbox column rights refuse before commit with exact owned restore", flush=True)

        pipeline("enable")
        wait(lambda: count() == 2 and queue_counts() == (0, 0) and sql(f"SELECT COUNT(*) FROM aioffice.GroupIngressOutbox WHERE {scope} AND PublishedAtUtc IS NOT NULL;") == "2", 60)
        assert protected_graph() == before
        stable = full_graph()
        # Held delivery + redelivery + event2; only the latter two are ACKed.
        prior_stats = wait_reference_statistics(broker_stats, wait, ack=2, deliver=3, phase="before application restart")
        pipeline("restart")
        wait(lambda: broker_stats()["consumers"] == 1)
        run("publish-existing")  # Actual persistent mandatory publication after restart; no new graph.
        wait_reference_statistics(broker_stats, wait, ack=prior_stats["ack"] + 1,
            deliver=prior_stats["deliver"] + 1, phase="after application restart")
        assert queue_counts() == (0, 0)
        assert count() == 2 and full_graph() == stable
        assert sql("SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE login_name=N'aioffice_runtime' AND status=N'sleeping'"
            " AND (transaction_isolation_level<>2 OR open_transaction_count<>0);") == "0"
        assert portal == sql("SELECT CONCAT((SELECT COUNT(*) FROM aioffice.Users),N'|',(SELECT COUNT(*) FROM aioffice.Tasks),N'|',"
            "(SELECT COUNT(*) FROM aioffice.TaskDispatches),N'|',(SELECT COUNT(*) FROM aioffice.TaskCheckpoints));")
        print("PASS actual shipping Core producer/Worker consumer DI and application restart preserve SQL graph/cursor and no fake portal task", flush=True)
        worker_identity, _, worker_running = service_identity("agent-worker")
        assert worker_running
        verify_pending_broker_restart(directory=directory, api=api,
            pause_worker=lambda: worker_control("stop", worker_identity),
            resume_worker=lambda: worker_control("start", worker_identity), broker_state=broker_state,
            restart_broker=restart_broker, queue_counts=queue_counts, broker_stats=broker_stats,
            publish=lambda: run("publish-existing"), inspect_pending=lambda: run("inspect-pending"),
            full_graph=full_graph, count=count, wait=wait)
        assert full_graph() == stable and portal == sql("SELECT CONCAT((SELECT COUNT(*) FROM aioffice.Users),N'|',(SELECT COUNT(*) FROM aioffice.Tasks),N'|',"
            "(SELECT COUNT(*) FROM aioffice.TaskDispatches),N'|',(SELECT COUNT(*) FROM aioffice.TaskCheckpoints));")
        assert sql("SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE login_name=N'aioffice_runtime' AND status=N'sleeping'"
            " AND (transaction_isolation_level<>2 OR open_transaction_count<>0);") == "0"
        print("PASS actual RabbitMQ process restart retains pending persistent original reference before republication and shipping consumer ACK preserves exact SQL graph", flush=True)

        # Separate issue278 allocation proof runs after every retained277 broker
        # and no-cursor-effect assertion. Only this owned source cursor changes.
        allocation_before = full_graph() + [digest("GroupBatchAllocations", "AfterSequence"),
            digest("GroupBatchAllocatedRevisions", "CommittedSequence")]
        assert sql(f"SELECT COUNT(*) FROM aioffice.GroupBatchAllocations WHERE {scope};") == "0"
        temporary_sql(sql,
            f"ALTER TABLE aioffice.GroupBatchAllocations ADD CONSTRAINT CK_CiGroupAllocationRollback CHECK(BindingId<>'{source}');",
            "IF OBJECT_ID(N'aioffice.CK_CiGroupAllocationRollback',N'C') IS NOT NULL ALTER TABLE aioffice.GroupBatchAllocations DROP CONSTRAINT CK_CiGroupAllocationRollback;",
            lambda: run("allocation-rollback"))
        assert full_graph() + [digest("GroupBatchAllocations", "AfterSequence"),
            digest("GroupBatchAllocatedRevisions", "CommittedSequence")] == allocation_before
        temporary_sql(sql, f"UPDATE aioffice.GroupServiceGrants SET IsEnabled=0 WHERE {grant};",
            f"UPDATE aioffice.GroupServiceGrants SET IsEnabled=1 WHERE {grant};", lambda: run("allocation-deny"))
        assert full_graph() + [digest("GroupBatchAllocations", "AfterSequence"),
            digest("GroupBatchAllocatedRevisions", "CommittedSequence")] == allocation_before
        print("PASS actual allocation SQL rollback and current Extract denial preserve complete source cursor and empty reservation graph", flush=True)

        def unsafe_allocation_column():
            assert sql("EXECUTE AS LOGIN=N'aioffice_runtime'; SELECT HAS_PERMS_BY_NAME(N'aioffice.GroupBatchAllocatedRevisions',N'OBJECT',N'UPDATE',N'ContentSha256',N'COLUMN'); REVERT;") == "1"
            run("allocation-unsafe")
        temporary_sql(sql, "GRANT UPDATE ON OBJECT::aioffice.GroupBatchAllocatedRevisions(ContentSha256) TO aioffice_binding_runtime;",
            "DENY UPDATE ON OBJECT::aioffice.GroupBatchAllocatedRevisions(ContentSha256) TO aioffice_binding_runtime;", unsafe_allocation_column)
        assert sql("EXECUTE AS LOGIN=N'aioffice_runtime'; SELECT HAS_PERMS_BY_NAME(N'aioffice.GroupBatchAllocatedRevisions',N'OBJECT',N'UPDATE',N'ContentSha256',N'COLUMN'); REVERT;") == "0"
        assert full_graph() + [digest("GroupBatchAllocations", "AfterSequence"),
            digest("GroupBatchAllocatedRevisions", "CommittedSequence")] == allocation_before
        print("PASS actual allocation unsafe effective immutable column right refuses with exact owned permission restoration", flush=True)

        hold = subprocess.Popen([*command, "allocation-hold"], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.PIPE, text=True, env=child_environment)
        hold.stdin.write(configuration(0)); hold.stdin.close(); hold.stdin = None
        wait(lambda: sql(f"SELECT CONCAT((SELECT COUNT(*) FROM aioffice.GroupBatchAllocations WHERE {scope}),N'|',"
            f"(SELECT COUNT(*) FROM aioffice.GroupBatchAllocatedRevisions WHERE {scope}));") == "1|2")
        assert sql(f"SELECT CONCAT(CommittedSequence,N'|',ScheduledThroughSequence,N'|',"
            f"CASE WHEN FirstPendingAtUtc IS NULL THEN 1 ELSE 0 END,N'|',CASE WHEN LastPendingAtUtc IS NULL THEN 1 ELSE 0 END)"
            f" FROM aioffice.GroupSourceStates WHERE {scope};") == "2|2|1|1"
        allocated_graph = full_graph() + [digest("GroupBatchAllocations", "AfterSequence"),
            digest("GroupBatchAllocatedRevisions", "CommittedSequence")]
        # Every original source/private/inbox byte is retained. Index3 is the
        # explicit scheduled cursor/pending anchor reservation just asserted.
        assert allocated_graph[:3] + allocated_graph[4:8] == allocation_before[:3] + allocation_before[4:8]
        assert kill_owned(), "Owned allocation child missing before lost receipt kill"
        held_stdout, _ = hold.communicate(timeout=15)
        assert hold.returncode == 137 and held_stdout.splitlines() == ["CHECKPOINT owned allocation committed before receipt delivery"]
        run("allocation-replay")
        assert full_graph() + [digest("GroupBatchAllocations", "AfterSequence"),
            digest("GroupBatchAllocatedRevisions", "CommittedSequence")] == allocated_graph
        print("PASS actual allocation commit lost receipt owned process death restart and100 concurrent replays return original batch ledger with one cursor effect", flush=True)
        temporary_sql(sql, f"UPDATE aioffice.GroupServiceGrants SET IsEnabled=0 WHERE {grant};",
            f"UPDATE aioffice.GroupServiceGrants SET IsEnabled=1 WHERE {grant};", lambda: run("allocation-deny"))
        assert full_graph() + [digest("GroupBatchAllocations", "AfterSequence"),
            digest("GroupBatchAllocatedRevisions", "CommittedSequence")] == allocated_graph
        assert portal == sql("SELECT CONCAT((SELECT COUNT(*) FROM aioffice.Users),N'|',(SELECT COUNT(*) FROM aioffice.Tasks),N'|',"
            "(SELECT COUNT(*) FROM aioffice.TaskDispatches),N'|',(SELECT COUNT(*) FROM aioffice.TaskCheckpoints));")
        assert sql("SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE login_name=N'aioffice_runtime' AND status=N'sleeping'"
            " AND (transaction_isolation_level<>2 OR open_transaction_count<>0);") == "0"
        print("PASS actual allocation original replay requires current Extract and restores SQL isolation without portal key model or note effects", flush=True)

        # Additive brain schema proof only: current role rights and empty scoped
        # metadata. No business records or provider calls qualify through it.
        run("work-schema")

        def work_permission_catalog(table):
            assert table in ("GroupCustomerRequests", "GroupEditorGrants")
            return sql(f"SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),COALESCE("
                f"(SELECT class,major_id,minor_id,grantee_principal_id,grantor_principal_id,type,state FROM sys.database_permissions "
                f"WHERE class=1 AND major_id=OBJECT_ID(N'aioffice.{table}') AND grantee_principal_id=DATABASE_PRINCIPAL_ID(N'aioffice_binding_runtime') "
                "ORDER BY minor_id,type FOR JSON PATH,INCLUDE_NULL_VALUES),N'[]'))),2);")

        def unsafe_work_column(table, column):
            assert sql(f"EXECUTE AS LOGIN=N'aioffice_runtime'; SELECT HAS_PERMS_BY_NAME(N'aioffice.{table}',N'OBJECT',N'UPDATE',N'{column}',N'COLUMN'); REVERT;") == "1"
            run("work-unsafe")

        for table, column in [("GroupCustomerRequests", "RequestCode"), ("GroupEditorGrants", "IsEnabled")]:
            original_permissions = work_permission_catalog(table)
            assert re.fullmatch(r"[0-9A-F]{64}", original_permissions)
            temporary_sql(sql, f"GRANT UPDATE ON OBJECT::aioffice.{table}({column}) TO aioffice_binding_runtime;",
                f"DENY UPDATE ON OBJECT::aioffice.{table}({column}) TO aioffice_binding_runtime;",
                lambda table=table, column=column: unsafe_work_column(table, column))
            assert sql(f"EXECUTE AS LOGIN=N'aioffice_runtime'; SELECT HAS_PERMS_BY_NAME(N'aioffice.{table}',N'OBJECT',N'UPDATE',N'{column}',N'COLUMN'); REVERT;") == "0"
            assert work_permission_catalog(table) == original_permissions
            run("work-schema")
        assert full_graph() + [digest("GroupBatchAllocations", "AfterSequence"),
            digest("GroupBatchAllocatedRevisions", "CommittedSequence")] == allocated_graph
        print("PASS actual work schema migrated empty scoped brain least privilege rejects effective identity and editor escalation with exact permission restore", flush=True)

        # Claims add metadata only to the independently allocated owned source.
        # Every original raw/inbox/allocation byte must remain unchanged.
        def claim_graph():
            return [digest("GroupBatchClaimStates", "BatchId"), digest("GroupBatchClaimReceipts", "Epoch")]
        empty_claims = claim_graph()
        assert sql(f"SELECT COUNT(*) FROM aioffice.GroupBatchClaimReceipts WHERE {scope};") == "0"
        temporary_sql(sql,
            f"ALTER TABLE aioffice.GroupBatchClaimReceipts ADD CONSTRAINT CK_CiGroupClaimRollback CHECK(BindingId<>'{source}');",
            "IF OBJECT_ID(N'aioffice.CK_CiGroupClaimRollback',N'C') IS NOT NULL ALTER TABLE aioffice.GroupBatchClaimReceipts DROP CONSTRAINT CK_CiGroupClaimRollback;",
            lambda: run("claim-rollback"))
        assert claim_graph() == empty_claims
        temporary_sql(sql, f"UPDATE aioffice.GroupServiceGrants SET IsEnabled=0 WHERE {grant};",
            f"UPDATE aioffice.GroupServiceGrants SET IsEnabled=1 WHERE {grant};", lambda: run("claim-deny"))
        assert claim_graph() == empty_claims
        assert full_graph() + [digest("GroupBatchAllocations", "AfterSequence"),
            digest("GroupBatchAllocatedRevisions", "CommittedSequence")] == allocated_graph
        print("PASS actual claim SQL rollback and current Extract denial preserve empty claim and original allocation graph", flush=True)

        def unsafe_claim_column(table, column):
            assert sql(f"EXECUTE AS LOGIN=N'aioffice_runtime'; SELECT HAS_PERMS_BY_NAME(N'aioffice.{table}',N'OBJECT',N'UPDATE',N'{column}',N'COLUMN'); REVERT;") == "1"
            run("claim-unsafe")
        for table, column in [("GroupBatchClaimReceipts", "AuthoritySha256"), ("GroupBatchClaimStates", "BatchId")]:
            temporary_sql(sql, f"GRANT UPDATE ON OBJECT::aioffice.{table}({column}) TO aioffice_binding_runtime;",
                f"DENY UPDATE ON OBJECT::aioffice.{table}({column}) TO aioffice_binding_runtime;",
                lambda table=table, column=column: unsafe_claim_column(table, column))
            assert sql(f"EXECUTE AS LOGIN=N'aioffice_runtime'; SELECT HAS_PERMS_BY_NAME(N'aioffice.{table}',N'OBJECT',N'UPDATE',N'{column}',N'COLUMN'); REVERT;") == "0"
            assert claim_graph() == empty_claims
        print("PASS actual claim unsafe effective immutable receipt and state identity rights refuse with exact owned restore", flush=True)

        crashed = subprocess.run([*command, "claim-crash"], input=configuration(0), env=child_environment,
            capture_output=True, text=True, timeout=170)
        assert crashed.returncode == 137 and crashed.stdout.splitlines() == ["CHECKPOINT owned claim committed before receipt delivery"]
        assert sql(f"SELECT CONCAT((SELECT COUNT(*) FROM aioffice.GroupBatchClaimReceipts WHERE {scope}),N'|',"
            f"(SELECT COUNT(*) FROM aioffice.GroupBatchClaimStates WHERE {scope} AND Epoch=1));") == "1|1"
        original_claims = claim_graph()
        original_claim_identity = [digest("GroupBatchClaimStates", "BatchId",
            "TenantId,CompanyId,BindingId,BatchId,Epoch,OwnerId,OperationId,IssuedAtUtc,ExpiresAtUtc"), original_claims[1]]
        assert sql(f"SELECT COUNT(*) FROM aioffice.GroupBatchClaimStates WHERE {scope} AND ExpiryObservedAtUtc IS NOT NULL;") == "0"
        run("claim-replay")
        assert [digest("GroupBatchClaimStates", "BatchId",
            "TenantId,CompanyId,BindingId,BatchId,Epoch,OwnerId,OperationId,IssuedAtUtc,ExpiresAtUtc"),
            digest("GroupBatchClaimReceipts", "Epoch")] == original_claim_identity
        # Only first expiry observation may be added by a successful expired
        # replay. Original epoch, owner, nonce, lease times and receipt bytes
        # above must stay exact; the following denial may not mutate even it.
        assert sql(f"SELECT COUNT(*) FROM aioffice.GroupBatchClaimStates WHERE {scope} AND "
            "(ExpiryObservedAtUtc IS NULL OR (ExpiryObservedAtUtc>=ExpiresAtUtc AND DATEPART(tz,ExpiryObservedAtUtc)=0));") == "1"
        original_claims = claim_graph()
        assert full_graph() + [digest("GroupBatchAllocations", "AfterSequence"),
            digest("GroupBatchAllocatedRevisions", "CommittedSequence")] == allocated_graph
        print("PASS actual claim competing SQL contexts commit lost receipt abrupt owned process death restart and100 replays preserve original lease without renewal", flush=True)
        temporary_sql(sql, f"UPDATE aioffice.GroupServiceGrants SET IsEnabled=0 WHERE {grant};",
            f"UPDATE aioffice.GroupServiceGrants SET IsEnabled=1 WHERE {grant};", lambda: run("claim-deny"))
        assert claim_graph() == original_claims
        run("claim-fence")
        assert sql(f"SELECT CONCAT((SELECT COUNT(*) FROM aioffice.GroupBatchClaimReceipts WHERE {scope}),N'|',"
            f"(SELECT COUNT(*) FROM aioffice.GroupBatchClaimStates WHERE {scope} AND Epoch=3));") == "3|1"
        assert sql(f"SELECT COUNT(*) FROM aioffice.GroupBatchClaimStates WHERE {scope} AND ExpiryObservedAtUtc IS NOT NULL;") == "0"
        print("PASS actual claim durable SQL expiry witness refuses original nonce handle and new acquisition after owned clock rollback", flush=True)
        assert full_graph() + [digest("GroupBatchAllocations", "AfterSequence"),
            digest("GroupBatchAllocatedRevisions", "CommittedSequence")] == allocated_graph
        assert portal == sql("SELECT CONCAT((SELECT COUNT(*) FROM aioffice.Users),N'|',(SELECT COUNT(*) FROM aioffice.Tasks),N'|',"
            "(SELECT COUNT(*) FROM aioffice.TaskDispatches),N'|',(SELECT COUNT(*) FROM aioffice.TaskCheckpoints));")
        assert sql("SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE login_name=N'aioffice_runtime' AND status=N'sleeping'"
            " AND (transaction_isolation_level<>2 OR open_transaction_count<>0);") == "0"
        print("PASS actual claim original replay requires current Extract actual expiry increments fenced epoch stale handles refuse and no portal private model note effects", flush=True)

        source_claims = claim_graph()
        run("source-read")
        run("source-foreign")
        assert claim_graph() == source_claims
        assert full_graph() + [digest("GroupBatchAllocations", "AfterSequence"),
            digest("GroupBatchAllocatedRevisions", "CommittedSequence")] == allocated_graph
        print("PASS actual source reader Core committed protected originals scoped configured keys exact context foreign-ID refusal and unchanged graph", flush=True)
        temporary_sql(sql, f"UPDATE aioffice.GroupServiceGrants SET IsEnabled=0 WHERE {grant};",
            f"UPDATE aioffice.GroupServiceGrants SET IsEnabled=1 WHERE {grant};", lambda: run("source-deny"))
        assert claim_graph() == source_claims
        run("source-read")
        assert claim_graph() == source_claims
        print("PASS actual source reader current Extract denial before keys exact owned restore and original context", flush=True)
        hold = subprocess.Popen([*command, "source-key-revoke"], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.PIPE, text=True, env=child_environment)
        hold.stdin.write(configuration(0)); hold.stdin.close(); hold.stdin = None

        def source_reader_container():
            # The owned image and container share a name. Before creation an
            # untyped inspect can return the image; only a container may qualify.
            inspected = subprocess.run(["docker", "inspect", "--type", "container", "--format", '{{.Id}}|{{index .Config.Labels "aioffice.owned-proof"}}', name],
                capture_output=True, text=True, timeout=10)
            if inspected.returncode:
                return None
            identity, label = inspected.stdout.strip().split("|", 1)
            assert re.fullmatch(r"[0-9a-f]{64}", identity) and label == suffix, "Owned source reader container identity mismatch"
            return identity

        def source_reader_awaiting():
            identity = source_reader_container()
            if identity is None:
                return False
            signal = subprocess.run(["docker", "exec", identity, "test", "-f", "/tmp/aioffice-source-proof-awaiting"],
                capture_output=True, text=True, timeout=10)
            return signal.returncode == 0

        wait(source_reader_awaiting)

        def release_source_reader():
            identity = source_reader_container()
            assert identity is not None
            released = subprocess.run(["docker", "exec", identity, "sh", "-c", ": > /tmp/aioffice-source-proof-release"],
                capture_output=True, text=True, timeout=10)
            assert released.returncode == 0, "Owned source reader release failed"
            output, _ = hold.communicate(timeout=35)
            assert hold.returncode == 0 and output.splitlines() == [
                "CHECKPOINT owned source key resolved outside SQL before final fence",
                "PASS owned source runtime current Extract revocation during key await denies private context before decrypt"]

        temporary_sql(sql, f"UPDATE aioffice.GroupServiceGrants SET IsEnabled=0 WHERE {grant};",
            f"UPDATE aioffice.GroupServiceGrants SET IsEnabled=1 WHERE {grant};", release_source_reader)
        assert claim_graph() == source_claims
        assert full_graph() + [digest("GroupBatchAllocations", "AfterSequence"),
            digest("GroupBatchAllocatedRevisions", "CommittedSequence")] == allocated_graph
        run("source-read")
        assert claim_graph() == source_claims
        print("PASS actual source reader external SQL Extract revocation during configured key await rejects private context before decrypt and exact restore recovers", flush=True)
        source_claim_identity = [digest("GroupBatchClaimStates", "BatchId",
            "TenantId,CompanyId,BindingId,BatchId,Epoch,OwnerId,OperationId,IssuedAtUtc,ExpiresAtUtc"), source_claims[1]]
        run("source-expiry")
        assert [digest("GroupBatchClaimStates", "BatchId",
            "TenantId,CompanyId,BindingId,BatchId,Epoch,OwnerId,OperationId,IssuedAtUtc,ExpiresAtUtc"),
            digest("GroupBatchClaimReceipts", "Epoch")] == source_claim_identity
        assert sql(f"SELECT COUNT(*) FROM aioffice.GroupBatchClaimStates WHERE {scope} AND Epoch=3 AND ExpiryObservedAtUtc=ExpiresAtUtc;") == "1"
        assert full_graph() + [digest("GroupBatchAllocations", "AfterSequence"),
            digest("GroupBatchAllocatedRevisions", "CommittedSequence")] == allocated_graph
        assert portal == sql("SELECT CONCAT((SELECT COUNT(*) FROM aioffice.Users),N'|',(SELECT COUNT(*) FROM aioffice.Tasks),N'|',"
            "(SELECT COUNT(*) FROM aioffice.TaskDispatches),N'|',(SELECT COUNT(*) FROM aioffice.TaskCheckpoints));")
        assert sql("SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE login_name=N'aioffice_runtime' AND status=N'sleeping'"
            " AND (transaction_isolation_level<>2 OR open_transaction_count<>0);") == "0"
        print("PASS actual source reader controlled key-await expiry commits witness-only SQL no private release or effects and refuses after clock rollback", flush=True)

        # Separate read fixture after all original epoch3 expiry/graph gates.
        # Operator publication is test setup, not the still-missing note store.
        brain_tables = [("GroupCustomerRequests", "Id"), ("GroupRequestRevisions", "RequestId,Revision"),
            ("GroupRequestEvidence", "RequestId,RequestRevision,Ordinal"), ("GroupWorkCommitReceipts", "BatchId,OperationId"),
            ("GroupWorkSourceDispositions", "BatchId,MessageId"), ("GroupNotesCommittedOutbox", "Id"),
            ("GroupNotesCommittedItems", "OutboxId,Ordinal"), ("GroupEditorGrants", "UserId"),
            ("GroupGlossaryEntries", "Id"), ("GroupGlossaryRevisions", "EntryId,Revision")]
        def brain_graph():
            return [digest(table, order) for table, order in brain_tables]
        assert all(sql(f"SELECT COUNT(*) FROM aioffice.{table} WHERE {scope};") == "0" for table, _ in brain_tables)
        def original_three_receipts():
            value = sql("SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),"
                f"(SELECT * FROM aioffice.GroupBatchClaimReceipts WHERE {scope} AND Epoch<=3 ORDER BY Epoch FOR JSON PATH,INCLUDE_NULL_VALUES))),2);")
            assert re.fullmatch(r"[0-9A-F]{64}", value)
            return value
        original_three = original_three_receipts()
        result = subprocess.run([*command, "brain-fixture"], input=configuration(0), env=child_environment,
            capture_output=True, text=True, timeout=170)
        assert result.returncode == 0
        fixture = validated_brain_fixture(result.stdout)
        assert original_three_receipts() == original_three
        assert sql(f"SELECT CONCAT((SELECT COUNT(*) FROM aioffice.GroupBatchClaimReceipts WHERE {scope}),N'|',"
            f"(SELECT COUNT(*) FROM aioffice.GroupBatchClaimStates WHERE {scope} AND Epoch=4 AND ExpiryObservedAtUtc IS NULL));") == "4|1"
        operation_id = str(uuid.uuid4())
        common = f"'{tenant}','{company}','{source}'"
        request_id, glossary_id, batch_id = (fixture[key] for key in ("requestId", "glossaryId", "batchId"))
        version, generation, created = (fixture[key] for key in ("sourceVersion", "deletionGeneration", "createdAtUtc"))
        sql("SET XACT_ABORT ON; BEGIN TRANSACTION;"
            " INSERT INTO aioffice.GroupWorkCommitReceipts(TenantId,CompanyId,BindingId,BatchId,OperationId,SourceSetSha256,SelectedMessageCount,NoteCount,Outcome,ServiceId,ClaimEpoch,CredentialEpoch,GrantVersion,SourceVersion,DeletionGeneration,AccountVersion,CommittedAtUtc)"
            f" VALUES({common},'{batch_id}','{operation_id}','{fixture['sourceSetHash']}',1,1,1,'{service}',4,{fixture['credentialEpoch']},{fixture['grantVersion']},{version},{generation},{fixture['accountVersion']},'{created}');"
            " INSERT INTO aioffice.GroupCustomerRequests(TenantId,CompanyId,BindingId,Id,OriginBatchId,OriginOperationId,OriginCandidateOrdinal,RequestCode,Kind,SourceVersion,DeletionGeneration,CurrentRevision,BusinessStatus,BusinessVersion,CreatedAtUtc,UpdatedAtUtc)"
            f" VALUES({common},'{request_id}','{batch_id}','{operation_id}',1,'REQ-{uuid.UUID(request_id).hex.upper()}',1,{version},{generation},1,1,1,'{created}','{created}');"
            " INSERT INTO aioffice.GroupRequestRevisions(TenantId,CompanyId,BindingId,RequestId,Revision,Origin,VerificationLevel,AuthorServiceId,SourceBatchId,ClaimEpoch,SourceVersion,DeletionGeneration,ContentKeyId,ProtectedContent,EnvelopeSha256,CreatedAtUtc)"
            f" VALUES({common},'{request_id}',1,1,1,'{service}','{batch_id}',4,{version},{generation},'owned-native-source-v1',0x{fixture['requestEnvelope']},'{fixture['requestHash']}','{created}');"
            " INSERT INTO aioffice.GroupRequestEvidence(TenantId,CompanyId,BindingId,RequestId,RequestRevision,Ordinal,MessageId,MessageRevision,Kind)"
            f" VALUES({common},'{request_id}',1,1,'{fixture['messageId']}',{fixture['messageRevision']},1);"
            " INSERT INTO aioffice.GroupGlossaryEntries(TenantId,CompanyId,BindingId,Id,CurrentRevision,Version,SourceVersion,DeletionGeneration,IsEnabled,AllowExtraction,PublishedByUserId,CreatedAtUtc)"
            f" VALUES({common},'{glossary_id}',1,1,{version},{generation},1,1,'{fixture['publisherId']}','{created}');"
            " INSERT INTO aioffice.GroupGlossaryRevisions(TenantId,CompanyId,BindingId,EntryId,Revision,SourceVersion,DeletionGeneration,PublishedByUserId,ContentKeyId,ProtectedContent,EnvelopeSha256,CreatedAtUtc)"
            f" VALUES({common},'{glossary_id}',1,{version},{generation},'{fixture['publisherId']}','owned-native-source-v1',0x{fixture['glossaryEnvelope']},'{fixture['glossaryHash']}','{created}'); COMMIT;")
        expected_counts = [1, 1, 1, 1, 0, 0, 0, 0, 1, 1]
        assert [int(sql(f"SELECT COUNT(*) FROM aioffice.{table} WHERE {scope};")) for table, _ in brain_tables] == expected_counts
        brain_stable = brain_graph(); brain_claims = claim_graph()
        run("brain-read"); run("brain-foreign")
        assert brain_graph() == brain_stable and claim_graph() == brain_claims
        temporary_sql(sql, f"UPDATE aioffice.GroupGlossaryEntries SET AllowExtraction=0 WHERE {scope} AND Id='{glossary_id}';",
            f"UPDATE aioffice.GroupGlossaryEntries SET AllowExtraction=1 WHERE {scope} AND Id='{glossary_id}';", lambda: run("brain-policy-deny"))
        temporary_sql(sql, f"UPDATE aioffice.GroupServiceGrants SET IsEnabled=0 WHERE {grant};",
            f"UPDATE aioffice.GroupServiceGrants SET IsEnabled=1 WHERE {grant};", lambda: run("brain-deny"))
        assert brain_graph() == brain_stable and claim_graph() == brain_claims
        run("brain-read")
        assert brain_graph() == brain_stable and claim_graph() == brain_claims
        print("PASS actual brain reader SQL protected request glossary original evidence configured keys foreign selection Extract and glossary denial exact restore", flush=True)

        hold = subprocess.Popen([*command, "brain-key-revoke"], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.PIPE, text=True, env=child_environment)
        hold.stdin.write(configuration(0)); hold.stdin.close(); hold.stdin = None
        def brain_reader_awaiting():
            identity = source_reader_container()
            if identity is None:
                return False
            signal = subprocess.run(["docker", "exec", identity, "test", "-f", "/tmp/aioffice-brain-proof-awaiting"],
                capture_output=True, text=True, timeout=10)
            return signal.returncode == 0
        wait(brain_reader_awaiting)
        def release_brain_reader():
            identity = source_reader_container(); assert identity is not None
            released = subprocess.run(["docker", "exec", identity, "sh", "-c", ": > /tmp/aioffice-brain-proof-release"],
                capture_output=True, text=True, timeout=10)
            assert released.returncode == 0
            output, _ = hold.communicate(timeout=35)
            assert hold.returncode == 0 and output.splitlines() == [
                "CHECKPOINT owned brain key resolved outside SQL before final fence",
                "PASS owned brain runtime SQL Extract revocation during key await denies private context"]
        temporary_sql(sql, f"UPDATE aioffice.GroupServiceGrants SET IsEnabled=0 WHERE {grant};",
            f"UPDATE aioffice.GroupServiceGrants SET IsEnabled=1 WHERE {grant};", release_brain_reader)
        assert brain_graph() == brain_stable and claim_graph() == brain_claims
        run("brain-read")
        assert brain_graph() == brain_stable and claim_graph() == brain_claims
        print("PASS actual brain reader external SQL Extract revocation during configured key await denies private context unchanged graph exact restore recovers", flush=True)

        brain_claim_identity = [digest("GroupBatchClaimStates", "BatchId",
            "TenantId,CompanyId,BindingId,BatchId,Epoch,OwnerId,OperationId,IssuedAtUtc,ExpiresAtUtc"), brain_claims[1]]
        run("brain-expiry")
        assert [digest("GroupBatchClaimStates", "BatchId",
            "TenantId,CompanyId,BindingId,BatchId,Epoch,OwnerId,OperationId,IssuedAtUtc,ExpiresAtUtc"),
            digest("GroupBatchClaimReceipts", "Epoch")] == brain_claim_identity
        assert sql(f"SELECT COUNT(*) FROM aioffice.GroupBatchClaimStates WHERE {scope} AND Epoch=4 AND ExpiryObservedAtUtc=ExpiresAtUtc;") == "1"
        assert brain_graph() == brain_stable
        assert original_three_receipts() == original_three
        assert full_graph() + [digest("GroupBatchAllocations", "AfterSequence"), digest("GroupBatchAllocatedRevisions", "CommittedSequence")] == allocated_graph
        assert portal == sql("SELECT CONCAT((SELECT COUNT(*) FROM aioffice.Users),N'|',(SELECT COUNT(*) FROM aioffice.Tasks),N'|',"
            "(SELECT COUNT(*) FROM aioffice.TaskDispatches),N'|',(SELECT COUNT(*) FROM aioffice.TaskCheckpoints));")
        assert sql("SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE login_name=N'aioffice_runtime' AND status=N'sleeping'"
            " AND (transaction_isolation_level<>2 OR open_transaction_count<>0);") == "0"
        print("PASS actual brain reader controlled key-await expiry persists only SQL witness unchanged notes outbox source and portal and denies after clock rollback", flush=True)

        # Original gap-bearing spool/reference/reader suite is complete.
        # Its immutable bytes remain checked during every later effect phase.
        original_account = str(uuid.UUID(sql(f"SELECT ConnectorAccountId FROM aioffice.GroupBindings WHERE TenantId='{tenant}' AND CompanyId='{company}' AND Id='{source}';")))
        def retained_scope_bytes():
            values = full_graph() + [digest("GroupBatchAllocations", "AfterSequence"),
                digest("GroupBatchAllocatedRevisions", "CommittedSequence")] + brain_graph() + claim_graph()
            for table, order in (("GroupAccountCoverageGaps", "ListenerEpoch,Reason"),
                    ("GroupListenerLeases", "Epoch"), ("GroupListenerCommandReceipts", "OperationId")):
                value = sql("SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),COALESCE("
                    f"(SELECT * FROM aioffice.{table} WHERE TenantId='{tenant}' AND CompanyId='{company}' AND ConnectorAccountId='{original_account}' ORDER BY {order} FOR JSON PATH,INCLUDE_NULL_VALUES),N'[]'))),2);")
                assert re.fullmatch(r"[0-9A-F]{64}", value)
                values.append(value)
            for table, identity in (("GroupBindings", source), ("GroupConnectorAccounts", original_account)):
                value = sql("SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),"
                    f"(SELECT * FROM aioffice.{table} WHERE TenantId='{tenant}' AND CompanyId='{company}' AND Id='{identity}' FOR JSON PATH,INCLUDE_NULL_VALUES))),2);")
                assert re.fullmatch(r"[0-9A-F]{64}", value)
                values.append(value)
            return values
        retained = retained_scope_bytes()
        def assert_retained():
            assert retained_scope_bytes() == retained, "Clean effect fixture changed original gap-bearing source or reader bytes"
        pipeline("disable")
        effect_source, effect_events, effect_key = prepare_effect_source()
        assert str(uuid.UUID(effect_source)) != source and not set(effect_events).intersection(events)
        assert_retained()
        verify_fixed_effects(directory=directory, api=api, tenant=tenant, company=company, service=service,
            source=effect_source, events=effect_events, source_key=effect_key, sql=sql, command=command,
            child_environment=child_environment, assert_retained=assert_retained)
        assert_retained()
    except BaseException as error:
        failure = error
    finally:
        cleanup(lambda: close_owned_reference_child(hold, kill_owned))
        cleanup(lambda: pipeline("disable"))
        cleanup(lambda: subprocess.run(["docker", "image", "rm", image], capture_output=True, text=True, timeout=30))
    if failure is not None: raise failure


def verify_fixed_effects(*, directory, api, tenant, company, service, source, events, source_key, sql,
        command, child_environment, assert_retained):
    require_owned(directory, api)
    require_source_key(source_key)
    tenant, company, service, source = (str(uuid.UUID(value)) for value in (tenant, company, service, source))
    events = [str(uuid.UUID(value)) for value in events]
    assert len(events) == 2 and len(set(events)) == 2 and all(uuid.UUID(value).int != 0 for value in events)
    scope = f"TenantId='{tenant}' AND CompanyId='{company}' AND BindingId='{source}'"
    child_environment = dict(child_environment)
    child_environment["AIOFFICE_GROUP_REFERENCE_PROOF_SOURCE_KEY"] = source_key
    def configuration(index):
        return json.dumps({"tenantId": tenant, "companyId": company, "serviceId": service, "sourceId": source, "eventId": events[index]})
    def run(mode, index=0):
        result = subprocess.run([*command, mode], input=configuration(index), env=child_environment,
            capture_output=True, text=True, timeout=170)
        lines = result.stdout.splitlines()
        expected = {
            "brain-expiry": "PASS owned brain runtime controlled key-await expiry commits SQL witness and denies after clock rollback",
            "no-work-expiry": "PASS owned NoWork runtime flushed two SQL effects rollback with source lock retained clean detach witness only and clock rollback denial",
            "no-work-commit": "PASS owned NoWork runtime actual atomic SQL receipt disposition exact original replay and new nonce duplicate refusal",
            "no-work-mars": "PASS owned NoWork runtime MARS refuses before connection keys or effects",
            "note-expiry": "PASS owned note runtime eleven flushed SQL effects rollback with source lock retained clean detach witness only and clock rollback denial",
            "note-key-expiry": "PASS owned note runtime configured write key outside SQL expiry witness only without effects and clock rollback denial",
            "note-commit": "PASS owned note runtime protected two notes literal evidence atomic NotesCommitted exact original replay changed proposal and new nonce refusal"}
        expected_lines = [expected[mode]]
        require_reference_result(result, mode, expected_lines)
        assert_retained()

    def digest(table, order, columns="*"):
        value = sql("SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),COALESCE("
            f"(SELECT {columns} FROM aioffice.{table} WHERE {scope} ORDER BY {order} FOR JSON PATH,INCLUDE_NULL_VALUES),N'[]'))),2);")
        assert re.fullmatch(r"[0-9A-F]{64}", value)
        return value
    def full_graph():
        return [digest(table, order) for table, order in (("GroupMessages", "Id"), ("GroupMessageRevisions", "CommittedSequence"),
            ("GroupIngressReceipts", "EventIdentityHash"), ("GroupSourceStates", "BindingId"), ("GroupCoverageGaps", "Id"),
            ("GroupIngressOutbox", "CommittedSequence"), ("GroupIngressInbox", "CommittedSequence"))]
    def claim_graph():
        return [digest("GroupBatchClaimStates", "BatchId"), digest("GroupBatchClaimReceipts", "Epoch")]
    def no_gaps():
        assert sql(f"SELECT CONCAT((SELECT COUNT(*) FROM aioffice.GroupCoverageGaps WHERE {scope}),N'|',"
            f"(SELECT COUNT(*) FROM aioffice.GroupAccountCoverageGaps g JOIN aioffice.GroupBindings b ON b.TenantId=g.TenantId AND b.CompanyId=g.CompanyId AND b.ConnectorAccountId=g.ConnectorAccountId WHERE b.TenantId='{tenant}' AND b.CompanyId='{company}' AND b.Id='{source}')); ") == "0|0"
    no_gaps()
    result = subprocess.run([*command, "effect-brain-fixture"], input=configuration(0), env=child_environment,
        capture_output=True, text=True, timeout=170)
    assert result.returncode == 0, "Owned clean effect batch fixture failed"
    fixture = validated_brain_fixture(result.stdout)
    assert_retained()
    assert sql(f"SELECT CONCAT((SELECT COUNT(*) FROM aioffice.GroupIngressInbox WHERE {scope}),N'|',"
        f"(SELECT COUNT(*) FROM aioffice.GroupBatchClaimReceipts WHERE {scope}),N'|',"
        f"(SELECT COUNT(*) FROM aioffice.GroupBatchClaimStates WHERE {scope} AND Epoch=4 AND ExpiryObservedAtUtc IS NULL));") == "2|4|1"
    allocated_graph = full_graph() + [digest("GroupBatchAllocations", "AfterSequence"), digest("GroupBatchAllocatedRevisions", "CommittedSequence")]
    portal = sql("SELECT CONCAT((SELECT COUNT(*) FROM aioffice.Users),N'|',(SELECT COUNT(*) FROM aioffice.Tasks),N'|',"
        "(SELECT COUNT(*) FROM aioffice.TaskDispatches),N'|',(SELECT COUNT(*) FROM aioffice.TaskCheckpoints));")
    brain_tables = [("GroupCustomerRequests", "Id"), ("GroupRequestRevisions", "RequestId,Revision"),
        ("GroupRequestEvidence", "RequestId,RequestRevision,Ordinal"), ("GroupWorkCommitReceipts", "BatchId,OperationId"),
        ("GroupWorkSourceDispositions", "BatchId,MessageId"), ("GroupNotesCommittedOutbox", "Id"),
        ("GroupNotesCommittedItems", "OutboxId,Ordinal"), ("GroupEditorGrants", "UserId"),
        ("GroupGlossaryEntries", "Id"), ("GroupGlossaryRevisions", "EntryId,Revision")]
    def brain_graph():
        return [digest(table, order) for table, order in brain_tables]
    assert all(sql(f"SELECT COUNT(*) FROM aioffice.{table} WHERE {scope};") == "0" for table, _ in brain_tables)
    def original_three_receipts():
        value = sql("SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),"
            f"(SELECT * FROM aioffice.GroupBatchClaimReceipts WHERE {scope} AND Epoch<=3 ORDER BY Epoch FOR JSON PATH,INCLUDE_NULL_VALUES))),2);")
        assert re.fullmatch(r"[0-9A-F]{64}", value)
        return value
    original_three = original_three_receipts()
    operation_id = str(uuid.uuid4())
    common = f"'{tenant}','{company}','{source}'"
    request_id, glossary_id, batch_id = (fixture[key] for key in ("requestId", "glossaryId", "batchId"))
    version, generation, created = (fixture[key] for key in ("sourceVersion", "deletionGeneration", "createdAtUtc"))
    sql("SET XACT_ABORT ON; BEGIN TRANSACTION;"
        " INSERT INTO aioffice.GroupWorkCommitReceipts(TenantId,CompanyId,BindingId,BatchId,OperationId,SourceSetSha256,SelectedMessageCount,NoteCount,Outcome,ServiceId,ClaimEpoch,CredentialEpoch,GrantVersion,SourceVersion,DeletionGeneration,AccountVersion,CommittedAtUtc)"
        f" VALUES({common},'{batch_id}','{operation_id}','{fixture['sourceSetHash']}',1,1,1,'{service}',4,{fixture['credentialEpoch']},{fixture['grantVersion']},{version},{generation},{fixture['accountVersion']},'{created}');"
        " INSERT INTO aioffice.GroupCustomerRequests(TenantId,CompanyId,BindingId,Id,OriginBatchId,OriginOperationId,OriginCandidateOrdinal,RequestCode,Kind,SourceVersion,DeletionGeneration,CurrentRevision,BusinessStatus,BusinessVersion,CreatedAtUtc,UpdatedAtUtc)"
        f" VALUES({common},'{request_id}','{batch_id}','{operation_id}',1,'REQ-{uuid.UUID(request_id).hex.upper()}',1,{version},{generation},1,1,1,'{created}','{created}');"
        " INSERT INTO aioffice.GroupRequestRevisions(TenantId,CompanyId,BindingId,RequestId,Revision,Origin,VerificationLevel,AuthorServiceId,SourceBatchId,ClaimEpoch,SourceVersion,DeletionGeneration,ContentKeyId,ProtectedContent,EnvelopeSha256,CreatedAtUtc)"
        f" VALUES({common},'{request_id}',1,1,1,'{service}','{batch_id}',4,{version},{generation},'owned-native-source-v1',0x{fixture['requestEnvelope']},'{fixture['requestHash']}','{created}');"
        " INSERT INTO aioffice.GroupRequestEvidence(TenantId,CompanyId,BindingId,RequestId,RequestRevision,Ordinal,MessageId,MessageRevision,Kind)"
        f" VALUES({common},'{request_id}',1,1,'{fixture['messageId']}',{fixture['messageRevision']},1);"
        " INSERT INTO aioffice.GroupGlossaryEntries(TenantId,CompanyId,BindingId,Id,CurrentRevision,Version,SourceVersion,DeletionGeneration,IsEnabled,AllowExtraction,PublishedByUserId,CreatedAtUtc)"
        f" VALUES({common},'{glossary_id}',1,1,{version},{generation},1,1,'{fixture['publisherId']}','{created}');"
        " INSERT INTO aioffice.GroupGlossaryRevisions(TenantId,CompanyId,BindingId,EntryId,Revision,SourceVersion,DeletionGeneration,PublishedByUserId,ContentKeyId,ProtectedContent,EnvelopeSha256,CreatedAtUtc)"
        f" VALUES({common},'{glossary_id}',1,{version},{generation},'{fixture['publisherId']}','owned-native-source-v1',0x{fixture['glossaryEnvelope']},'{fixture['glossaryHash']}','{created}'); COMMIT;")
    expected_counts = [1, 1, 1, 1, 0, 0, 0, 0, 1, 1]
    assert [int(sql(f"SELECT COUNT(*) FROM aioffice.{table} WHERE {scope};")) for table, _ in brain_tables] == expected_counts
    brain_stable = brain_graph()
    claim_identity = [digest("GroupBatchClaimStates", "BatchId",
        "TenantId,CompanyId,BindingId,BatchId,Epoch,OwnerId,OperationId,IssuedAtUtc,ExpiresAtUtc"), digest("GroupBatchClaimReceipts", "Epoch")]
    run("brain-expiry")
    assert [digest("GroupBatchClaimStates", "BatchId",
        "TenantId,CompanyId,BindingId,BatchId,Epoch,OwnerId,OperationId,IssuedAtUtc,ExpiresAtUtc"), digest("GroupBatchClaimReceipts", "Epoch")] == claim_identity
    assert sql(f"SELECT COUNT(*) FROM aioffice.GroupBatchClaimStates WHERE {scope} AND Epoch=4 AND ExpiryObservedAtUtc=ExpiresAtUtc;") == "1"
    assert brain_graph() == brain_stable and original_three_receipts() == original_three
    original_assert_retained = assert_retained
    def assert_retained():
        original_assert_retained()
        no_gaps()
    # Append fixed-effect probes only after every retained reader oracle.
    # Synthetic classification proves SQL mechanics, never model quality.
    def original_four_receipts():
        value = sql("SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),"
            f"(SELECT * FROM aioffice.GroupBatchClaimReceipts WHERE {scope} AND Epoch<=4 ORDER BY Epoch FOR JSON PATH,INCLUDE_NULL_VALUES))),2);")
        assert re.fullmatch(r"[0-9A-F]{64}", value)
        return value
    original_four = original_four_receipts()
    assert_retained()
    run("no-work-expiry")
    assert brain_graph() == brain_stable
    assert original_four_receipts() == original_four
    assert sql(f"SELECT CONCAT((SELECT COUNT(*) FROM aioffice.GroupBatchClaimReceipts WHERE {scope}),N'|',"
        f"(SELECT COUNT(*) FROM aioffice.GroupBatchClaimStates WHERE {scope} AND Epoch=5 AND ExpiryObservedAtUtc=ExpiresAtUtc));") == "5|1"
    assert full_graph() + [digest("GroupBatchAllocations", "AfterSequence"), digest("GroupBatchAllocatedRevisions", "CommittedSequence")] == allocated_graph
    assert portal == sql("SELECT CONCAT((SELECT COUNT(*) FROM aioffice.Users),N'|',(SELECT COUNT(*) FROM aioffice.Tasks),N'|',"
        "(SELECT COUNT(*) FROM aioffice.TaskDispatches),N'|',(SELECT COUNT(*) FROM aioffice.TaskCheckpoints));")
    assert sql("SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE login_name=N'aioffice_runtime' AND status=N'sleeping'"
        " AND (transaction_isolation_level<>2 OR open_transaction_count<>0);") == "0"
    assert_retained()
    print("PASS actual NoWork store flushed SQL rollback retains source lock clean tracker and only expiry witness unchanged brain source allocation portal", flush=True)

    run("no-work-commit")
    assert [int(sql(f"SELECT COUNT(*) FROM aioffice.{table} WHERE {scope};")) for table, _ in brain_tables] == [1, 1, 1, 2, 1, 0, 0, 0, 1, 1]
    no_work_stable = brain_graph(); no_work_claims = claim_graph()
    assert [value for index, value in enumerate(no_work_stable) if index not in (3, 4)] == [
        value for index, value in enumerate(brain_stable) if index not in (3, 4)]
    assert original_four_receipts() == original_four
    assert sql(f"SELECT CONCAT((SELECT COUNT(*) FROM aioffice.GroupBatchClaimReceipts WHERE {scope}),N'|',"
        f"(SELECT COUNT(*) FROM aioffice.GroupBatchClaimStates WHERE {scope} AND Epoch=6 AND ExpiryObservedAtUtc IS NULL));") == "6|1"
    run("no-work-mars")
    assert brain_graph() == no_work_stable and claim_graph() == no_work_claims
    assert original_four_receipts() == original_four and original_three_receipts() == original_three
    assert full_graph() + [digest("GroupBatchAllocations", "AfterSequence"), digest("GroupBatchAllocatedRevisions", "CommittedSequence")] == allocated_graph
    assert portal == sql("SELECT CONCAT((SELECT COUNT(*) FROM aioffice.Users),N'|',(SELECT COUNT(*) FROM aioffice.Tasks),N'|',"
        "(SELECT COUNT(*) FROM aioffice.TaskDispatches),N'|',(SELECT COUNT(*) FROM aioffice.TaskCheckpoints));")
    assert sql("SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE login_name=N'aioffice_runtime' AND status=N'sleeping'"
        " AND (transaction_isolation_level<>2 OR open_transaction_count<>0);") == "0"
    assert_retained()
    print("PASS actual NoWork store SQL atomic selected receipt disposition original replay duplicate nonce refusal and MARS before connection unchanged remaining graph", flush=True)

    # Separate fixed protected-note consumer after every original NoWork
    # oracle. Synthetic interpretations qualify SQL mechanics only.
    def original_six_receipts():
        value = sql("SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),"
            f"(SELECT * FROM aioffice.GroupBatchClaimReceipts WHERE {scope} AND Epoch<=6 ORDER BY Epoch FOR JSON PATH,INCLUDE_NULL_VALUES))),2);")
        assert re.fullmatch(r"[0-9A-F]{64}", value)
        return value
    original_six = original_six_receipts()
    def prior_note_objects():
        selections = [("GroupCustomerRequests", "Id", f"Id='{request_id}'"),
            ("GroupRequestRevisions", "RequestId,Revision", f"RequestId='{request_id}'"),
            ("GroupRequestEvidence", "RequestId,RequestRevision,Ordinal", f"RequestId='{request_id}'"),
            ("GroupWorkCommitReceipts", "BatchId,OperationId", "NoteCount<=1"),
            ("GroupWorkSourceDispositions", "BatchId,MessageId", "Outcome=2")]
        values = []
        for table, order, predicate in selections:
            value = sql("SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),COALESCE("
                f"(SELECT * FROM aioffice.{table} WHERE {scope} AND {predicate} ORDER BY {order} FOR JSON PATH,INCLUDE_NULL_VALUES),N'[]'))),2);")
            assert re.fullmatch(r"[0-9A-F]{64}", value)
            values.append(value)
        return values
    prior_notes = prior_note_objects()
    def unchanged_note_source_graph():
        assert_retained()
        assert original_six_receipts() == original_six and original_four_receipts() == original_four and original_three_receipts() == original_three
        assert full_graph() + [digest("GroupBatchAllocations", "AfterSequence"), digest("GroupBatchAllocatedRevisions", "CommittedSequence")] == allocated_graph
        assert portal == sql("SELECT CONCAT((SELECT COUNT(*) FROM aioffice.Users),N'|',(SELECT COUNT(*) FROM aioffice.Tasks),N'|',"
            "(SELECT COUNT(*) FROM aioffice.TaskDispatches),N'|',(SELECT COUNT(*) FROM aioffice.TaskCheckpoints));")
        assert sql("SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE login_name=N'aioffice_runtime' AND status=N'sleeping'"
            " AND (transaction_isolation_level<>2 OR open_transaction_count<>0);") == "0"
    run("note-expiry")
    assert brain_graph() == no_work_stable and prior_note_objects() == prior_notes
    assert sql(f"SELECT CONCAT((SELECT COUNT(*) FROM aioffice.GroupBatchClaimReceipts WHERE {scope}),N'|',"
        f"(SELECT COUNT(*) FROM aioffice.GroupBatchClaimStates WHERE {scope} AND Epoch=7 AND ExpiryObservedAtUtc=ExpiresAtUtc));") == "7|1"
    unchanged_note_source_graph()
    print("PASS actual note store eleven flushed SQL effects rollback source lock retained clean detach only expiry witness unchanged complete brain source allocation portal", flush=True)

    run("note-key-expiry")
    assert brain_graph() == no_work_stable and prior_note_objects() == prior_notes
    assert sql(f"SELECT CONCAT((SELECT COUNT(*) FROM aioffice.GroupBatchClaimReceipts WHERE {scope}),N'|',"
        f"(SELECT COUNT(*) FROM aioffice.GroupBatchClaimStates WHERE {scope} AND Epoch=8 AND ExpiryObservedAtUtc=ExpiresAtUtc));") == "8|1"
    unchanged_note_source_graph()
    print("PASS actual note store configured write key outside SQL expired final fence commits only witness no note disposition outbox source portal effects clock rollback denied", flush=True)

    run("note-commit")
    assert [int(sql(f"SELECT COUNT(*) FROM aioffice.{table} WHERE {scope};")) for table, _ in brain_tables] == [3, 3, 3, 3, 2, 1, 2, 0, 1, 1]
    assert prior_note_objects() == prior_notes
    assert brain_graph()[7:] == no_work_stable[7:]
    assert sql(f"SELECT CONCAT((SELECT COUNT(*) FROM aioffice.GroupBatchClaimReceipts WHERE {scope}),N'|',"
        f"(SELECT COUNT(*) FROM aioffice.GroupBatchClaimStates WHERE {scope} AND Epoch=9 AND ExpiryObservedAtUtc IS NULL));") == "9|1"
    assert sql(f"SELECT COUNT(*) FROM aioffice.GroupCustomerRequests WHERE {scope} AND Id<>'{request_id}' AND CurrentRevision=1"
        " AND BusinessVersion=1 AND BusinessStatus IN(1,3) AND AssignedToUserId IS NULL AND CommittedDueAtUtc IS NULL"
        " AND ConfirmedByUserId IS NULL AND ConfirmedAtUtc IS NULL;") == "2"
    assert sql(f"SELECT COUNT(*) FROM aioffice.GroupNotesCommittedOutbox WHERE {scope} AND NoteCount=2"
        " AND PublishAttempts=0 AND PublishedAtUtc IS NULL AND AvailableAtUtc=CommittedAtUtc;") == "1"
    unchanged_note_source_graph()
    print("PASS actual note store protected request evidence two unconfirmed notes atomic NotesCommitted exact original replay changed proposal and new nonce refusal unchanged original graphs", flush=True)
    assert_retained()
