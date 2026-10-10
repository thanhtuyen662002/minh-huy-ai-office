"""Real shipping .NET client/spool on a separate disposable CI SQL source."""
import base64
import hashlib
import http.server
import json
import os
import re
import secrets
import subprocess
import threading
import time
import urllib.request
import uuid
from datetime import datetime, timezone
from pathlib import Path


def require_owned(directory, api):
    if not (os.environ.get("CI") == "true" and os.environ.get("GITHUB_ACTIONS") == "true"
            and os.environ.get("RUNNER_TEMP") and directory.resolve() == (Path(os.environ["RUNNER_TEMP"]) / "aioffice-local").resolve()
            and api == "http://127.0.0.1:8080"):
        raise RuntimeError("Spool proof requires the owned disposable GitHub CI fixture.")


def verify(*, directory, api, tenant, company, service, key, sql, restart, ready, identity_index):
    require_owned(directory, api)  # Before files, keys, SQL, build, processes or HTTP.
    tenant, company, service = (str(uuid.UUID(value)) for value in (tenant, company, service))
    if not isinstance(key, bytes) or len(key) != 32:
        raise RuntimeError("Owned spool signing key is unavailable.")
    account, source, owner, event = (str(uuid.uuid4()) for _ in range(4))
    registry = f"TenantId='{tenant}' AND CompanyId='{company}'"
    scope = registry + f" AND BindingId='{source}'"
    account_scope = registry + f" AND ConnectorAccountId='{account}'"
    external_account, external_group = "spool-account-" + account, "spool-group-" + source
    literal = lambda value: "N'" + value.replace("'", "''") + "'"
    root = directory / "group-spool-proof"
    root.mkdir(mode=0o700)  # Refuse a pre-existing retained proof root.
    root.chmod(0o700)
    config = {"tenantId": tenant, "companyId": company, "serviceId": service, "accountId": account,
        "sourceId": source, "ownerId": owner, "eventId": event,
        "occurredAtUtc": datetime.now(timezone.utc).isoformat(), "origin": api + "/"}
    child_environment = {**os.environ, "AIOFFICE_OWNED_GROUP_SPOOL_PROOF": "true",
        "AIOFFICE_GROUP_PROOF_SIGNING_KEY": base64.b64encode(key).decode("ascii"),
        "AIOFFICE_GROUP_PROOF_SPOOL_KEY": base64.b64encode(secrets.token_bytes(32)).decode("ascii")}
    project = Path("tests/GroupIntake.RuntimeProof/GroupIntake.RuntimeProof.csproj")
    executable = project.parent / "bin/Release/net10.0/MinhHuy.AIOffice.GroupIntake.RuntimeProof.dll"
    build = subprocess.run(["dotnet", "build", str(project), "--configuration", "Release", "--verbosity", "quiet"],
        capture_output=True, text=True, timeout=150)
    if build.returncode:
        raise RuntimeError("Owned group spool executable build failed.")

    def save_config():
        path = root / "config.json"
        path.write_text(json.dumps(config, separators=(",", ":")), encoding="utf-8")
        path.chmod(0o600)

    def run(mode):
        result = subprocess.run(["dotnet", str(executable), mode], env=child_environment,
            capture_output=True, text=True, timeout=25)
        if result.returncode:
            # Child emits only fixed phase labels; never expose captured streams.
            raise RuntimeError("Owned group spool executable failed at " + mode + ".")

    tables = [("GroupMessages", "Id"), ("GroupMessageRevisions", "CommittedSequence"),
        ("GroupIngressReceipts", "EventIdentityHash"), ("GroupIngressOutbox", "Id"),
        ("GroupSourceStates", "BindingId"), ("GroupCoverageGaps", "Id")]

    def snapshot():
        values = [sql(f"SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),COALESCE("
            f"(SELECT * FROM aioffice.{table} WHERE {scope} ORDER BY {order} FOR JSON PATH,INCLUDE_NULL_VALUES),N'[]'))),2);")
            for table, order in tables]
        assert all(re.fullmatch(r"[0-9A-F]{64}", value) for value in values)
        return values

    def counts():
        return [int(sql(f"SELECT COUNT(*) FROM aioffice.{table} WHERE {scope};")) for table, _ in tables[:4]]

    def retained():
        paths = list((root / "group-spool").rglob("*.spool"))
        assert len(paths) == 1, "Expected one retained native spool capture"
        value = paths[0].read_bytes()
        assert 1 <= len(value) <= 65859 and b"owned native spool" not in value
        return paths[0], value

    # No runtime auto-enrollment or production grant. This is a new owned
    # synthetic source/account, leaving prior ingress/listener sources intact.
    sql(f"""INSERT aioffice.GroupConnectorAccounts
      (TenantId,CompanyId,Id,Provider,ExternalAccountId,IdentityHash,PackageVersion,GitCommit,QualificationJson,Version,IsEnabled)
      VALUES ('{tenant}','{company}','{account}','synthetic',{literal(external_account)},
      '{identity_index('synthetic',external_account,'account-registry')}','owned-fixture','{'0'*40}',N'{{"environment":1,"observations":[]}}',1,1);
      INSERT aioffice.GroupBindings(TenantId,CompanyId,Id,ConnectorAccountId,Role,Provider,ExternalAccountId,ExternalGroupId,IdentityHash,PhysicalGroupHash,DisplayName,Version,DeletionGeneration,IsEnabled)
      VALUES ('{tenant}','{company}','{source}','{account}',1,'synthetic',{literal(external_account)},{literal(external_group)},
      '{identity_index('synthetic',external_account,external_group)}','{identity_index('synthetic','physical-group-registry',external_group)}',N'Owned native spool source',1,0,1);
      INSERT aioffice.GroupServiceGrants(TenantId,CompanyId,ServiceId,BindingId,Capability,Version,IsEnabled)
      VALUES ('{tenant}','{company}','{service}','{source}',1,1,1);""")
    save_config()
    run("acquire")
    lease = json.loads((root / "lease.json").read_text(encoding="utf-8"))
    assert lease["lease"]["epoch"] == 1 and lease["lease"]["ownerId"] == owner
    assert sql(f"SELECT COUNT(*) FROM aioffice.GroupListenerLeases WHERE {account_scope} AND OwnerId='{owner}' AND Epoch=1;") == "1"
    assert counts() == [0] * 4

    committed = threading.Event()
    release = threading.Event()
    captured = {}

    class LostReply(http.server.BaseHTTPRequestHandler):
        def log_message(self, *args):
            pass  # No source, headers, paths or dependency exceptions logged.

        def do_POST(self):
            try:
                if self.path != "/internal/group-ingress/events":
                    raise RuntimeError()
                length = int(self.headers.get("Content-Length", "0"))
                if not 1 <= length <= 65536:
                    raise RuntimeError()
                headers = {"Content-Type": "application/json"}
                for name in ("Service", "Epoch", "Signed-At", "Nonce", "Signature"):
                    header = "X-AIOffice-Group-" + name
                    values = self.headers.get_all(header, [])
                    if len(values) != 1:
                        raise RuntimeError()
                    headers[header] = values[0]
                body = self.rfile.read(length)
                if len(body) != length:
                    raise RuntimeError()
                request = urllib.request.Request(api + self.path, data=body, method="POST", headers=headers)
                with urllib.request.urlopen(request, timeout=12) as response:
                    raw = response.read(8193)
                    if response.status != 200 or len(raw) > 8192 or "no-store" not in response.headers.get("Cache-Control", ""):
                        raise RuntimeError()
                    value = json.loads(raw)
                    if value["source"] != {"tenantId": tenant, "companyId": company, "sourceBindingId": source}:
                        raise RuntimeError()
                    captured["ack"] = value
                committed.set()  # Real Core SQL committed; no client ACK bytes.
                release.wait(15)
            except Exception:
                captured["failed"] = True
                committed.set()
            finally:
                self.close_connection = True

    proxy = http.server.ThreadingHTTPServer(("127.0.0.1", 0), LostReply)
    proxy.daemon_threads = True
    serving = threading.Thread(target=proxy.serve_forever, daemon=True)
    process = None
    failure = None
    try:
        serving.start()
        config["origin"] = f"http://127.0.0.1:{proxy.server_port}/"
        save_config()
        process = subprocess.Popen(["dotnet", str(executable), "capture-send"], env=child_environment,
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        assert committed.wait(12) and not captured.get("failed") and "ack" in captured, "Native client did not reach committed lost-ACK boundary"
        assert process.poll() is None, "Native client completed before forced death"
        process.kill()  # Only this owned child, after observed real commit.
        process.communicate(timeout=5)
        assert process.returncode != 0
    except BaseException as error:
        failure = error
    finally:
        # Attempt every owned cleanup even after partial startup/drain failure,
        # preserve the first failure, and never kill an unrelated process.
        release.set()
        def stop_child():
            if process is not None:
                if process.poll() is None:
                    process.kill()
                process.communicate(timeout=5)
        for cleanup in (stop_child, lambda: proxy.shutdown() if serving.is_alive() else None,
                proxy.server_close, lambda: serving.join(timeout=2) if serving.ident is not None else None):
            try:
                cleanup()
            except BaseException as error:
                if failure is None:
                    failure = error
    if failure is not None:
        raise failure
    retained_path, ciphertext = retained()
    original = snapshot()
    assert counts() == [1] * 4 and captured["ack"]["committedSequence"] == 1
    print("PASS actual .NET encrypted spool100 and forced listener process death after observed SQL commit retain lost-ACK ciphertext/one graph effect")

    restart()
    ready()
    assert snapshot() == original and retained_path.read_bytes() == ciphertext, "Core restart changed committed graph or retained ciphertext"
    config["origin"] = api + "/"
    expiry = datetime.fromisoformat(lease["lease"]["expiresAtUtc"].replace("Z", "+00:00"))
    remaining = (expiry - datetime.now(timezone.utc)).total_seconds()
    assert remaining <= 30, "Owned server lease duration exceeded contract"
    if remaining > 0:
        time.sleep(remaining + .2)
    config["ownerId"] = str(uuid.uuid4())
    save_config()
    run("acquire")
    new_lease = json.loads((root / "lease.json").read_text(encoding="utf-8"))["lease"]
    assert new_lease["epoch"] == 2 and new_lease["ownerId"] != owner
    assert sql(f"SELECT COUNT(*) FROM aioffice.GroupAccountCoverageGaps WHERE {account_scope} AND ListenerEpoch=2 AND Reason='listener-expired';") == "1"
    run("replay")
    recovered = json.loads((root / "commit.json").read_text(encoding="utf-8"))
    assert recovered == {**captured["ack"], "wasAlreadyCommitted": True}
    assert snapshot() == original and counts() == [1] * 4 and not retained_path.exists()
    print("PASS actual .NET spool reopen/new listener epoch/Core restart reconciles original exact SQL ACK and deletes only committed capture with unchanged full6 graph bytes")

    config["eventId"] = str(uuid.uuid4())
    config["occurredAtUtc"] = datetime.now(timezone.utc).isoformat()
    save_config()
    run("capture")
    _, blocked_bytes = retained()
    before = snapshot()
    grant_where = scope + f" AND ServiceId='{service}' AND Capability=1"
    def grant_snapshot():
        return sql(f"SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),"
            f"(SELECT * FROM aioffice.GroupServiceGrants WHERE {grant_where} FOR JSON PATH,INCLUDE_NULL_VALUES))),2);")
    original_grant = grant_snapshot()
    failure = None
    try:
        sql(f"UPDATE aioffice.GroupServiceGrants SET IsEnabled=0 WHERE {grant_where};")
        run("deny")
        assert snapshot() == before and retained()[1] == blocked_bytes, "Fresh backend denial erased backlog or mutated SQL"
    except BaseException as error:
        failure = error
    finally:
        try:
            sql(f"UPDATE aioffice.GroupServiceGrants SET IsEnabled=1 WHERE {grant_where};")
        except BaseException as error:
            if failure is None:
                failure = error
    if failure is not None:
        raise failure
    assert grant_snapshot() == original_grant, "Owned native grant restore did not preserve original bytes"
    run("replay")
    assert counts() == [2] * 4 and not list((root / "group-spool").rglob("*.spool"))
    assert json.loads((root / "commit.json").read_text(encoding="utf-8"))["committedSequence"] == 2
    assert sql("SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE login_name=N'aioffice_runtime' AND status=N'sleeping'"
        " AND (transaction_isolation_level<>2 OR open_transaction_count<>0);") == "0"
    print("PASS actual .NET fresh SQL grant revoke403 retains every encrypted byte/no graph effects; exact restore allows original backlog commit/cursor2 and clean pooled isolation")
