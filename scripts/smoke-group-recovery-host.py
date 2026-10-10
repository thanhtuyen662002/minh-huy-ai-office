"""Actual Worker recovery factory/loop -> signed HTTP -> owned Core SQL only."""
import base64
import http.server
import json
import os
import re
import secrets
import subprocess
import threading
import time
import urllib.error
import urllib.request
import uuid
from datetime import datetime, timezone
from pathlib import Path


def require_owned(directory, api):
    if not (os.environ.get("CI") == "true" and os.environ.get("GITHUB_ACTIONS") == "true"
            and os.environ.get("RUNNER_TEMP") and directory.resolve() == (Path(os.environ["RUNNER_TEMP"]) / "aioffice-local").resolve()
            and api == "http://127.0.0.1:8080"):
        raise RuntimeError("Managed recovery requires the owned disposable GitHub CI fixture.")


def verify(*, directory, api, tenant, company, service, key, runtime_password, sql, restart, ready, identity_index, enroll_source):
    require_owned(directory, api)  # Before config, credentials, files, processes, SQL or HTTP.
    if not isinstance(key, bytes) or len(key) != 32 or not isinstance(runtime_password, str) or not re.fullmatch(r"[A-Za-z0-9_-]{32,128}", runtime_password):
        raise RuntimeError("Owned managed recovery credentials unavailable.")
    if not callable(enroll_source):
        raise RuntimeError("Owned managed recovery enrollment unavailable.")
    tenant, company, service = (str(uuid.UUID(value)) for value in (tenant, company, service))
    account, source, event, unused_owner = (str(uuid.uuid4()) for _ in range(4))
    scope = f"TenantId='{tenant}' AND CompanyId='{company}' AND BindingId='{source}'"
    account_scope = f"TenantId='{tenant}' AND CompanyId='{company}' AND ConnectorAccountId='{account}'"
    external_account, external_group = "spool-account-" + account, "spool-group-" + source
    root = directory / "group-recovery-proof"
    root.mkdir(mode=0o700)  # A new independent root; never reuse the manual-spool proof.
    root.chmod(0o700)
    environment = {**os.environ, "AIOFFICE_OWNED_GROUP_SPOOL_PROOF": "true", "AIOFFICE_OWNED_GROUP_RECOVERY_PROOF": "true",
        "AIOFFICE_GROUP_PROOF_SIGNING_KEY": base64.b64encode(key).decode("ascii"),
        "AIOFFICE_GROUP_PROOF_SPOOL_KEY": base64.b64encode(secrets.token_bytes(32)).decode("ascii"),
        "AIOFFICE_GROUP_PROOF_RUNTIME_CONNECTION": "Server=sql;Database=AIOfficeLocal;User ID=aioffice_runtime;Password="
            + runtime_password + ";Encrypt=true;TrustServerCertificate=true"}
    project = Path("tests/GroupIntake.RuntimeProof/GroupIntake.RuntimeProof.csproj")
    executable = project.parent / "bin/Release/net10.0/MinhHuy.AIOffice.GroupIntake.RuntimeProof.dll"
    built = subprocess.run(["dotnet", "build", str(project), "--configuration", "Release", "--verbosity", "quiet"],
        capture_output=True, text=True, timeout=150)
    if built.returncode:
        raise RuntimeError("Owned managed recovery build failed.")
    config = {"tenantId": tenant, "companyId": company, "serviceId": service, "accountId": account,
        "sourceId": source, "ownerId": unused_owner, "eventId": event,
        "occurredAtUtc": datetime.now(timezone.utc).isoformat(), "origin": api + "/"}

    def save_config():
        path = root / "config.json"
        path.write_text(json.dumps(config, separators=(",", ":")), encoding="utf-8")
        path.chmod(0o600)

    def run(mode):
        result = subprocess.run(["dotnet", str(executable), mode], env=environment, capture_output=True, text=True, timeout=30)
        expected = {"host-deny": "PASS owned managed recovery current denial before key retains ciphertext",
            "host-recover": "PASS owned managed hosted recovery deletes only actual acknowledged capture"}[mode]
        assert result.returncode == 0 and result.stdout.splitlines() == [expected], "Owned managed recovery child failed"

    tables = [("GroupMessages", "Id"), ("GroupMessageRevisions", "CommittedSequence"),
        ("GroupIngressReceipts", "EventIdentityHash"), ("GroupIngressOutbox", "Id"),
        ("GroupSourceStates", "BindingId"), ("GroupCoverageGaps", "Id")]

    def snapshot():
        values = [sql(f"SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),COALESCE("
            f"(SELECT * FROM aioffice.{table} WHERE {scope} ORDER BY {order} FOR JSON PATH,INCLUDE_NULL_VALUES),N'[]'))),2);") for table, order in tables]
        assert all(re.fullmatch(r"[0-9A-F]{64}", value) for value in values)
        return values

    def counts():
        return [int(sql(f"SELECT COUNT(*) FROM aioffice.{table} WHERE {scope};")) for table, _ in tables[:4]]

    def retained():
        path = list((root / "group-spool").rglob("*.spool"))
        assert len(path) == 1, "Expected one managed retained capture"
        value = path[0].read_bytes()
        assert 1 <= len(value) <= 65859 and b"owned managed recovery" not in value
        return path[0], value

    # Fixed owned source/account only; no customer/provider/operator enrollment.
    sql(f"""INSERT aioffice.GroupConnectorAccounts
      (TenantId,CompanyId,Id,Provider,ExternalAccountId,IdentityHash,PackageVersion,GitCommit,QualificationJson,Version,IsEnabled)
      VALUES('{tenant}','{company}','{account}','synthetic',N'{external_account}',
        '{identity_index('synthetic',external_account,'account-registry')}','owned-fixture','{'0'*40}',N'{{"environment":1,"observations":[]}}',1,1);
      INSERT aioffice.GroupBindings(TenantId,CompanyId,Id,ConnectorAccountId,Role,Provider,ExternalAccountId,ExternalGroupId,IdentityHash,PhysicalGroupHash,DisplayName,Version,DeletionGeneration,IsEnabled)
      VALUES('{tenant}','{company}','{source}','{account}',1,'synthetic',N'{external_account}',N'{external_group}',
        '{identity_index('synthetic',external_account,external_group)}','{identity_index('synthetic','physical-group-registry',external_group)}',N'Owned managed recovery',1,0,1);
      INSERT aioffice.GroupServiceGrants(TenantId,CompanyId,ServiceId,BindingId,Capability,Version,IsEnabled)
      VALUES('{tenant}','{company}','{service}','{source}',1,1,1);""")
    enroll_source(source)
    assert counts() == [0] * 4
    reached, release = threading.Event(), threading.Event()
    captured = {"hold": True, "events": [], "leases": [], "denials": 0, "failed": False}

    class NoRedirect(urllib.request.HTTPRedirectHandler):
        def redirect_request(self, *args, **kwargs): return None

    class Proxy(http.server.BaseHTTPRequestHandler):
        def log_message(self, *args): pass

        def do_POST(self):
            try:
                limits = {"/internal/group-ingress/enrollment": 8192, "/internal/group-ingress/listener": 8192, "/internal/group-ingress/events": 65536}
                length = int(self.headers.get("Content-Length", "0"))
                if self.path not in limits or not 1 <= length <= limits[self.path]: raise RuntimeError()
                headers = {"Content-Type": "application/json"}
                for suffix in ("Service", "Epoch", "Signed-At", "Nonce", "Signature"):
                    name = "X-AIOffice-Group-" + suffix
                    values = self.headers.get_all(name, [])
                    if len(values) != 1: raise RuntimeError()
                    headers[name] = values[0]
                body = self.rfile.read(length)
                if len(body) != length: raise RuntimeError()
                request = urllib.request.Request(api + self.path, data=body, method="POST", headers=headers)
                opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
                try:
                    response = opener.open(request, timeout=12)
                except urllib.error.HTTPError as denied:
                    response = denied
                with response:
                    raw = response.read(8193)
                    status = response.status
                    if len(raw) > 8192 or status not in (200, 403): raise RuntimeError()
                    if status == 403:
                        if self.path != "/internal/group-ingress/enrollment": raise RuntimeError()
                        captured["denials"] += 1
                    if status == 200:
                        if not raw or "no-store" not in response.headers.get("Cache-Control", "") or response.headers.get("Content-Type", "").split(";")[0] != "application/json":
                            raise RuntimeError()
                        if self.path == "/internal/group-ingress/listener": captured["leases"].append(json.loads(raw)["lease"])
                        if self.path == "/internal/group-ingress/events":
                            ack = json.loads(raw)
                            if ack["source"] != {"tenantId": tenant, "companyId": company, "sourceBindingId": source} or ack["committedSequence"] != 1:
                                raise RuntimeError()
                            captured["events"].append(ack)
                if self.path == "/internal/group-ingress/events" and captured["hold"]:
                    reached.set(); release.wait(15); return
                self.send_response(status)
                self.send_header("Content-Type", "application/json")
                self.send_header("Cache-Control", "no-store")
                self.send_header("Content-Length", str(len(raw)))
                self.end_headers(); self.wfile.write(raw); self.wfile.flush()
            except Exception:
                captured["failed"] = True; reached.set()
            finally:
                self.close_connection = True

    proxy = serving = process = None
    failure = None
    try:
        proxy = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Proxy)
        proxy.daemon_threads = True
        serving = threading.Thread(target=proxy.serve_forever, daemon=True); serving.start()
        config["origin"] = f"http://127.0.0.1:{proxy.server_port}/"; save_config()
        process = subprocess.Popen(["dotnet", str(executable), "host-capture-send"], env=environment,
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        assert reached.wait(12) and not captured["failed"] and len(captured["events"]) == 1, "Managed capture missed actual commit boundary"
        assert process.poll() is None, "Managed capture completed before forced death"
        process.kill(); process.communicate(timeout=5)
        assert process.returncode != 0
        release.set(); captured["hold"] = False
        pending, ciphertext = retained(); original = snapshot()
        assert counts() == [1] * 4 and len(captured["leases"]) == 2 and captured["events"][0]["wasAlreadyCommitted"] is False
        old_lease = captured["leases"][-1]
        old_owner = str(uuid.UUID(old_lease["ownerId"]))
        assert old_owner != unused_owner and old_lease["epoch"] == 1
        assert sql(f"SELECT COUNT(*) FROM aioffice.GroupListenerLeases WHERE {account_scope} AND OwnerId='{old_owner}' AND Epoch=1;") == "1"
        print("PASS actual managed recovery factory/current SQL authority/owned process death after commit retains exact encrypted capture and one graph", flush=True)
        restart(); ready()
        assert snapshot() == original and pending.read_bytes() == ciphertext
        grant = scope + f" AND ServiceId='{service}' AND Capability=1"
        def grant_snapshot():
            return sql(f"SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),(SELECT * FROM aioffice.GroupServiceGrants WHERE {grant} FOR JSON PATH,INCLUDE_NULL_VALUES))),2);")
        grant_before = grant_snapshot()
        grant_failure = None
        try:
            sql(f"UPDATE aioffice.GroupServiceGrants SET IsEnabled=0 WHERE {grant};")
            run("host-deny")
            assert snapshot() == original and pending.read_bytes() == ciphertext
            assert captured["denials"] == 1 and len(captured["leases"]) == 2 and len(captured["events"]) == 1
        except BaseException as error:
            grant_failure = error
        finally:
            try: sql(f"UPDATE aioffice.GroupServiceGrants SET IsEnabled=1 WHERE {grant};")
            except BaseException as error:
                if grant_failure is None: grant_failure = error
        if grant_failure is not None: raise grant_failure
        assert grant_snapshot() == grant_before and not captured["failed"]
        print("PASS actual managed recovery current SQL grant403 before key retains exact ciphertext/full graph and exact grant restore", flush=True)
        remaining = (datetime.fromisoformat(old_lease["expiresAtUtc"].replace("Z", "+00:00")) - datetime.now(timezone.utc)).total_seconds()
        assert remaining <= 30
        if remaining > 0: time.sleep(remaining + .2)
        run("host-recover")
        assert not captured["failed"] and len(captured["events"]) == 2
        assert captured["events"][1] == {**captured["events"][0], "wasAlreadyCommitted": True}
        new_lease = captured["leases"][-1]
        new_owner = str(uuid.UUID(new_lease["ownerId"]))
        assert new_owner != old_owner and new_lease["epoch"] == 2
        assert sql(f"SELECT COUNT(*) FROM aioffice.GroupListenerLeases WHERE {account_scope} AND OwnerId='{new_owner}' AND Epoch=2;") == "1"
        assert sql(f"SELECT COUNT(*) FROM aioffice.GroupAccountCoverageGaps WHERE {account_scope} AND ListenerEpoch=2 AND Reason='listener-expired';") == "1"
        assert snapshot() == original and counts() == [1] * 4 and not pending.exists()
        assert not list((root / "group-spool").rglob("*.spool"))
        print("PASS actual managed hosted loop/new process owner/epoch2/Core restart replays original SQL ACK and deletes only its capture with unchanged full graph", flush=True)
    except BaseException as error:
        failure = error
    finally:
        release.set()
        def stop_child():
            if process is None: return
            child_failure = None
            running = True
            try: running = process.poll() is None
            except BaseException as error: child_failure = error
            if running:
                try: process.kill()
                except BaseException as error:
                    if child_failure is None: child_failure = error
            try: process.communicate(timeout=5)
            except subprocess.TimeoutExpired as error:
                if child_failure is None: child_failure = error
                try: process.kill()
                except BaseException as error:
                    if child_failure is None: child_failure = error
                try: process.communicate(timeout=5)
                except BaseException as error:
                    if child_failure is None: child_failure = error
            except BaseException as error:
                if child_failure is None: child_failure = error
            if child_failure is not None: raise child_failure
        for cleanup in (stop_child,
                lambda: proxy.shutdown() if proxy is not None and serving is not None and serving.is_alive() else None,
                lambda: proxy.server_close() if proxy is not None else None,
                lambda: serving.join(timeout=2) if serving is not None and serving.ident is not None else None):
            try: cleanup()
            except BaseException as error:
                if failure is None: failure = error
    if failure is not None: raise failure
