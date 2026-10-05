"""Real local SQL/OIDC/broker/API/worker/FE gate. No credentials in output or argv."""
import argparse
from concurrent.futures import ThreadPoolExecutor
import hashlib
from http.cookiejar import CookieJar
import json
import subprocess
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
from pathlib import Path


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("data_directory", type=Path)
    args = parser.parse_args()
    directory = args.data_directory.resolve()
    manifest_file = directory / "installation.json"
    manifest = json.loads(manifest_file.read_text(encoding="utf-8"))
    original_manifest = hashlib.sha256(manifest_file.read_bytes()).hexdigest()
    secrets = [value for key, value in manifest.items() if key.endswith("_PASSWORD")]
    tenant = str(uuid.UUID(manifest["AIOFFICE_TENANT_ID"]))
    company = str(uuid.UUID(manifest["AIOFFICE_COMPANY_ID"]))
    user = str(uuid.UUID(manifest["AIOFFICE_USER_ID"]))
    source = str(uuid.UUID(manifest["AIOFFICE_DATA_SOURCE_ID"]))
    compose = ["docker", "compose", "--env-file", str(directory / "local.env"), "-f", "compose.local.yaml"]

    def clean(message):
        for secret in secrets:
            message = message.replace(secret, "[REDACTED]")
        return message

    def run(*arguments, timeout=900):
        result = subprocess.run([*compose, *arguments], capture_output=True, text=True, timeout=timeout)
        if result.returncode:
            if arguments and arguments[0] == "up":
                bootstrap_log = subprocess.run([*compose, "logs", "--no-color", "--tail", "30", "bootstrap"],
                    capture_output=True, text=True, timeout=20)
                print(clean(bootstrap_log.stdout[-2000:]))
            # Never dump logs or rendered environments. Include only a bounded sanitized CLI error.
            diagnostic = result.stderr or result.stdout
            raise RuntimeError(clean(diagnostic[-1500:]))
        return result.stdout

    def sql(query):
        return run("exec", "-T", "sql", "sh", "-c",
                   'SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" /opt/mssql-tools18/bin/sqlcmd '
                   '-S localhost -U sa -C -I -b -m 1 -h -1 -W -Q "$1"', "sql", "SET NOCOUNT ON; " + query, timeout=30).strip()

    cookies = CookieJar()
    browser = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(cookies))
    web = "http://127.0.0.1:3000"
    api = "http://127.0.0.1:8080"

    def http(path, payload=None, *, base=web, headers=None, method=None):
        request = urllib.request.Request(base + path,
            data=None if payload is None else json.dumps(payload).encode(),
            headers={"Content-Type": "application/json", **(headers or {})},
            method=method or ("GET" if payload is None else "POST"))
        try:
            response = browser.open(request, timeout=15)
        except urllib.error.HTTPError as error:
            response = error
        raw = response.read().decode()
        try:
            value = json.loads(raw)
        except json.JSONDecodeError:
            value = raw
        return response.status, response.headers, value

    def wait_for(probe, seconds=180):
        deadline = time.monotonic() + seconds
        while time.monotonic() < deadline:
            try:
                result = probe()
                if result:
                    return result
            except (OSError, RuntimeError, urllib.error.URLError):
                pass
            time.sleep(2)
        raise RuntimeError("Local runtime readiness deadline exceeded.")

    def ready():
        try:
            assert wait_for(lambda: http("/health", base=api)[0] == 200)
            assert wait_for(lambda: http("/")[0] == 200)
        except RuntimeError:
            states = run("ps", "--all", "--format", "{{.Service}} {{.State}} {{.Health}}")
            print(clean(states))
            # Only startup diagnostics, before any login or access token is created.
            for service in ("core-api", "agent-worker"):
                result = subprocess.run([*compose, "logs", "--no-color", "--tail", "20", service],
                    capture_output=True, text=True, timeout=20)
                print(clean(result.stdout[-2000:]))
            raise

    def login(expected=200):
        status, headers, body = http("/api/local/session/login", {
            "username": "owner", "password": manifest["AIOFFICE_OWNER_PASSWORD"], "companyId": company})
        assert status == expected, f"FE login status {status}, expected {expected}"
        assert "no-store" in headers.get("Cache-Control", "")
        if expected == 200:
            context = body["context"]
            assert context["tenantId"] == tenant and context["companyId"] == company and context["userId"] == user
            assert "admin" in context["roles"]
            cookie = headers.get("Set-Cookie", "")
            assert "HttpOnly" in cookie and "SameSite=lax" in cookie

    profile = json.loads(run("config", "--format", "json"))
    assert profile["name"] == "aioffice-" + uuid.UUID(manifest["AIOFFICE_INSTALLATION_ID"]).hex
    services = profile["services"]
    for service in ("core-api", "agent-worker"):
        environment = services[service]["environment"]
        assert environment["DOTNET_ENVIRONMENT"] == "Development"
        assert environment["RabbitMqWork__HostName"] == "rabbitmq"
        assert environment["RabbitMqWork__UserName"] == "aioffice-local"
        assert environment["RabbitMqWork__Password"] == manifest["AIOFFICE_RABBITMQ_PASSWORD"]
        assert "User ID=aioffice_runtime;" in environment["AIOFFICE_DB_CONNECTION"]
        assert manifest["AIOFFICE_SQL_PASSWORD"] not in json.dumps(environment)
        assert manifest["AIOFFICE_OWNER_PASSWORD"] not in json.dumps(environment)
    assert services["core-api"]["environment"]["AIOffice__PlatformDatabase__ConnectionSecretRef"] == "secretref://env/AIOFFICE_DB_CONNECTION"
    for service in services.values():
        assert all(port["host_ip"] == "127.0.0.1" for port in service.get("ports", []))
    for name in ("sql", "identity-db", "rabbitmq", "redis"):
        assert not services[name].get("ports"), "Internal data service has a host port"
    print("PASS local service configuration, secret references and isolated loopback profile")
    run("up", "--build", "-d", timeout=1500)
    ready()
    status, _, page = http("/")
    assert status == 200 and company in page
    assert all(secret not in page for secret in secrets)
    login()
    selector = "?companyId=" + company
    status, headers, sources = http("/api/local/data-sources" + selector)
    assert status == 200 and "no-store" in headers.get("Cache-Control", "")
    assert any(item["id"] == source and item["allowRead"] and not item["allowWrite"] for item in sources)
    assert all(secret not in json.dumps(sources) for secret in secrets)
    status, _, result = http(f"/api/local/data-sources/{source}/connection-test{selector}", {})
    assert status == 200 and result["succeeded"], "Read-only SQL connection test failed"

    # Explicitly exercise the actual API's independent company authorization using the BFF cookie token.
    token = next(cookie.value for cookie in cookies if cookie.value.count(".") == 2)
    auth = {"Authorization": "Bearer " + token, "X-AIOffice-Company-Id": company}
    foreign_company = str(uuid.uuid4())
    foreign_source = str(uuid.uuid4())
    sql(f"""USE AIOfficeLocal;
        INSERT aioffice.Companies (TenantId, Id, Code, Name) VALUES ('{tenant}', '{foreign_company}', N'FOREIGN', N'Foreign company');
        INSERT aioffice.DataSources (TenantId, CompanyId, Id, LogicalName, Kind, Environment, Purpose, ConnectionSecretReference, MaxConcurrency)
        VALUES ('{tenant}', '{foreign_company}', '{foreign_source}', N'Foreign source', N'sql-server', N'Development', N'Isolation test', N'secretref://env/PILOT_ERP_CONNECTION', 1);""")
    wrong = {**auth, "X-AIOffice-Company-Id": foreign_company}
    assert http("/api/auth/context", base=api, headers=wrong)[0] == 403
    assert http("/api/data-sources", base=api, headers=wrong)[0] == 403
    denied_status, _, denied_result = http(f"/api/data-sources/{foreign_source}/connection-test", {}, base=api, headers=auth)
    assert denied_status == 200 and not denied_result["succeeded"] and denied_result["code"] == "not_found"
    permission_result = sql("""USE AIOfficeSample;
        EXECUTE AS LOGIN=N'aioffice_reader';
        BEGIN TRY
            BEGIN TRANSACTION;
            INSERT dbo.LocalSample VALUES (99, N'Forbidden write');
            ROLLBACK TRANSACTION;
            SELECT N'WRITE_ALLOWED';
        END TRY
        BEGIN CATCH
            IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
            IF ERROR_NUMBER()=229 SELECT N'WRITE_DENIED'; ELSE THROW;
        END CATCH;
        REVERT;""")
    assert permission_result == "WRITE_DENIED", f"Read-only SQL result: {permission_result!r}"
    assert sql("""SELECT CASE WHEN IS_SRVROLEMEMBER('sysadmin','aioffice_runtime')=0
        AND IS_SRVROLEMEMBER('dbcreator','aioffice_runtime')=0 THEN N'RESTRICTED' ELSE N'PRIVILEGED' END;""") == "RESTRICTED"
    print("PASS FE login, authoritative context, read-only SQL source and company isolation")

    # Authority must be re-resolved even with the same previously issued JWT and BFF cookie.
    source_scope = f"TenantId='{tenant}' AND CompanyId='{company}'"
    role_scope = source_scope + f" AND UserId='{user}'"

    def fingerprint(table, scope, order):
        result = sql(f"""USE AIOfficeLocal; SELECT CONVERT(varchar(64), HASHBYTES('SHA2_256',
            (SELECT * FROM aioffice.{table} WHERE {scope} ORDER BY {order}
             FOR JSON PATH, INCLUDE_NULL_VALUES)), 2);""")
        assert len(result) == 64, "SQL fixture fingerprint failed"
        return result

    source_before = fingerprint("DataSources", source_scope, "Id")
    roles_before = fingerprint("RoleAssignments", role_scope, "RoleKey")
    backup = "tempdb.dbo.AIOfficeRoleBackup_" + uuid.uuid4().hex
    sql(f"""USE AIOfficeLocal; SELECT TenantId, CompanyId, UserId, RoleKey, CreatedAtUtc
        INTO {backup} FROM aioffice.RoleAssignments WHERE {role_scope};""")
    metadata = {
        "logicalName": "acceptance-denied-" + uuid.uuid4().hex,
        "kind": "sql-server", "environment": "Development", "purpose": "Role revocation acceptance",
        "connectionSecretReference": "secretref://env/ROLE_REVOCATION_DENIED",
        "allowRead": False, "allowWrite": True, "maxConcurrency": 7, "isEnabled": False,
    }
    narrow = {key: metadata[key] for key in ("logicalName", "purpose", "maxConcurrency", "isEnabled")}
    try:
        sql(f"USE AIOfficeLocal; DELETE FROM aioffice.RoleAssignments WHERE {role_scope} AND RoleKey COLLATE Latin1_General_100_BIN2=N'admin';")
        status, _, context = http("/api/auth/context", base=api, headers=auth)
        assert status == 200 and "admin" not in context["roles"], "Issued token retained revoked authority"
        assert http("/api/data-sources", base=api, headers=auth)[0] == 200
        assert http("/api/local/data-sources" + selector)[0] == 200
        for path, method in (("/api/data-sources/", "POST"), (f"/api/data-sources/{source}", "PUT")):
            assert http(path, metadata, base=api, headers=auth, method=method)[0] == 403
        assert http(f"/api/data-sources/{source}/metadata", narrow, base=api, headers=auth, method="PUT")[0] == 403
        assert http(f"/api/local/data-sources/{source}/metadata{selector}", narrow,
                    headers={"Origin": web}, method="PUT")[0] == 403
        for path, method in (("/api/local/data-sources", "POST"), (f"/api/local/data-sources/{source}", "PUT")):
            status, headers, body = http(path + selector, metadata, headers={"Origin": web}, method=method)
            assert status == 403, "BFF did not preserve authoritative source-management denial"
            assert "no-store" in headers.get("Cache-Control", "")
            assert all(secret not in json.dumps(body) for secret in [*secrets, token, metadata["connectionSecretReference"]])
        assert fingerprint("DataSources", source_scope, "Id") == source_before, "Denied mutation changed stored source metadata"
    finally:
        # Keep all original roles and timestamps, including custom assignments, even on failure.
        sql(f"""USE AIOfficeLocal; SET XACT_ABORT ON; BEGIN TRANSACTION;
            DELETE FROM aioffice.RoleAssignments WHERE {role_scope};
            INSERT aioffice.RoleAssignments (TenantId, CompanyId, UserId, RoleKey, CreatedAtUtc)
                SELECT TenantId, CompanyId, UserId, RoleKey, CreatedAtUtc FROM {backup};
            DROP TABLE {backup}; COMMIT TRANSACTION;""")
        assert fingerprint("RoleAssignments", role_scope, "RoleKey") == roles_before, "Role fixture restoration changed assignments"
    status, _, context = http("/api/auth/context", base=api, headers=auth)
    assert status == 200 and "admin" in context["roles"], "Restored admin authority unavailable"
    for path, method in (("/api/data-sources/", "POST"), (f"/api/data-sources/{foreign_source}", "PUT")):
        assert http(path, metadata, base=api, headers=wrong, method=method)[0] == 403
    assert http(f"/api/data-sources/{foreign_source}/metadata", narrow, base=api, headers=wrong, method="PUT")[0] == 403
    assert http(f"/api/data-sources/{foreign_source}/metadata", narrow, base=api, headers=auth, method="PUT")[0] == 404

    # Exercise successful BFF create/update against the real database, then remove only this fixture.
    fixture_name = "acceptance-admin-" + uuid.uuid4().hex
    allowed = {**metadata, "logicalName": fixture_name, "connectionSecretReference": "secretref://env/PILOT_ERP_CONNECTION",
               "allowRead": True, "allowWrite": False, "isEnabled": True}
    fixture_id = None
    gate = "tempdb.dbo.AIOfficeMetadataGate_" + uuid.uuid4().hex
    gate_created = False
    locker = executor = pending = None
    try:
        status, headers, created = http("/api/local/data-sources" + selector, allowed, headers={"Origin": web})
        assert status == 201, f"Company admin source create failed with HTTP {status}"
        assert "no-store" in headers.get("Cache-Control", "")
        assert "connectionSecretReference" not in created
        fixture_id = str(uuid.UUID(created["id"]))
        updated = {**allowed, "maxConcurrency": 3, "isEnabled": False}
        status, headers, result = http(f"/api/local/data-sources/{fixture_id}{selector}", updated,
            headers={"Origin": web}, method="PUT")
        assert status == 200 and not result["isEnabled"] and result["maxConcurrency"] == 3
        assert "no-store" in headers.get("Cache-Control", "") and "connectionSecretReference" not in result
        assert sql(f"""USE AIOfficeLocal; SELECT COUNT(*) FROM aioffice.DataSources
            WHERE {source_scope} AND Id='{fixture_id}' AND IsEnabled=0 AND MaxConcurrency=3;""") == "1"

        # The strict endpoint cannot accept client-controlled authority or protected fields.
        before_invalid = fingerprint("DataSources", source_scope, "Id")
        for bad in ({key: value for key, value in narrow.items() if key != "purpose"},
                    {**narrow, "allowWrite": True}, {**narrow, "connectionSecretReference": "secretref://env/UNTRUSTED"}):
            assert http(f"/api/data-sources/{fixture_id}/metadata", bad, base=api, headers=auth, method="PUT")[0] == 400
            assert http(f"/api/local/data-sources/{fixture_id}/metadata{selector}", bad,
                        headers={"Origin": web}, method="PUT")[0] == 400
        assert fingerprint("DataSources", source_scope, "Id") == before_invalid

        # RCSI readers see committed versions: an uncommitted guard rename cannot
        # block the uniqueness query. Hold an X lock on the owned TARGET instead.
        # Core reads its old version, then its actual UPDATE waits on that same key.
        # Committing the rotation only after observing this write proves after-load
        # protection without changing database isolation or adding production hooks.
        assert sql("SELECT is_read_committed_snapshot_on FROM sys.databases WHERE name=N'AIOfficeLocal';") == "1", "RCSI acceptance requires the unchanged disposable-stack isolation"
        created_at = sql(f"USE AIOfficeLocal; SELECT CONVERT(varchar(33),CreatedAtUtc,126) FROM aioffice.DataSources WHERE {source_scope} AND Id='{fixture_id}';")
        sql(f"CREATE TABLE {gate} (Phase int NOT NULL, LockerSessionId int NOT NULL); INSERT {gate} VALUES (0,0);")
        gate_created = True

        def race_diagnostics():
            # Never output request/SQL text, headers, payloads, connection strings
            # or exception messages. Classify server phases and expose numeric waits only.
            diagnostic = {"lockerRunning": locker.poll() is None, "http": "pending"}
            if pending is not None and pending.done():
                try:
                    diagnostic["http"] = {"status": pending.result()[0]}
                except Exception as error:
                    diagnostic["http"] = {"exceptionType": type(error).__name__}
            try:
                diagnostic["readCommittedSnapshot"] = sql("SELECT is_read_committed_snapshot_on FROM sys.databases WHERE name=N'AIOfficeLocal';") == "1"
                waits = sql(f"""USE AIOfficeLocal;
                    SELECT CONCAT(r.session_id,N',',r.blocking_session_id,N',',r.wait_type,N',',
                        CASE WHEN CHARINDEX(N'UPDATE [aioffice].[DataSources]',t.text)>0 THEN N'source-update'
                             WHEN t.text LIKE N'%EXISTS%' AND t.text LIKE N'%LogicalName%' THEN N'uniqueness'
                             WHEN t.text LIKE N'%DataSources%' AND t.text LIKE N'%ConnectionSecretReference%' THEN N'source-load'
                             WHEN t.text LIKE N'%DataSources%' THEN N'source-other' ELSE N'other' END,N',',
                        CASE WHEN bt.text LIKE N'%{gate}%' THEN 1 ELSE 0 END)
                    FROM sys.dm_exec_requests r
                    LEFT JOIN sys.dm_exec_requests b ON b.session_id=r.blocking_session_id
                    CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) t
                    OUTER APPLY sys.dm_exec_sql_text(b.sql_handle) bt
                    WHERE r.database_id=DB_ID() AND r.blocking_session_id>0 AND r.session_id<>@@SPID;""")
                diagnostic["blockedRequests"] = []
                for row in waits.splitlines():
                    session_id, blocker_id, wait_type, phase, owned = row.split(",")
                    diagnostic["blockedRequests"].append({"sessionId": int(session_id), "blockingSessionId": int(blocker_id),
                        "waitType": wait_type, "queryPhase": phase, "ownedBlocker": owned == "1"})
                locks = sql(f"""USE AIOfficeLocal;
                    SELECT CONCAT(l.resource_type,N',',l.request_mode,N',',l.request_status,N',',ISNULL(p.index_id,-1),N',',COUNT(*))
                    FROM sys.dm_tran_locks l
                    JOIN sys.dm_exec_requests r ON r.session_id=l.request_session_id
                    CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) t
                    LEFT JOIN sys.partitions p ON p.hobt_id=l.resource_associated_entity_id
                    WHERE l.resource_database_id=DB_ID() AND t.text LIKE N'%{gate}%'
                        AND (p.object_id=OBJECT_ID(N'aioffice.DataSources')
                             OR (l.resource_type=N'OBJECT' AND l.resource_associated_entity_id=OBJECT_ID(N'aioffice.DataSources')))
                    GROUP BY l.resource_type,l.request_mode,l.request_status,p.index_id;""")
                diagnostic["ownedSourceLocks"] = []
                for row in locks.splitlines():
                    resource, mode, status, index_id, count = row.split(",")
                    diagnostic["ownedSourceLocks"].append({"resourceType": resource, "lockMode": mode,
                        "lockStatus": status, "indexId": int(index_id), "lockCount": int(count)})
            except Exception as error:
                diagnostic["diagnosticExceptionType"] = type(error).__name__
            print("Metadata race diagnostics " + json.dumps(diagnostic, sort_keys=True), flush=True)

        def write_gate_probe(metadata_only):
            protected_assignments = " AND ".join(
                f"CHARINDEX(N'[{name}] =',t.text)=0" for name in
                ("Kind", "Environment", "AllowRead", "AllowWrite", "ConnectionSecretReference"))
            shape = protected_assignments if metadata_only else "CHARINDEX(N'[Kind] =',t.text)>0"
            return f"""USE AIOfficeLocal; SELECT COUNT(*) FROM sys.dm_exec_requests r
                CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) t
                JOIN sys.dm_tran_locks waiting ON waiting.request_session_id=r.session_id
                JOIN sys.dm_tran_locks held ON held.request_session_id=r.blocking_session_id
                    AND held.resource_database_id=waiting.resource_database_id
                    AND held.resource_type=waiting.resource_type
                    AND ISNULL(held.resource_subtype,N'')=ISNULL(waiting.resource_subtype,N'')
                    AND held.resource_associated_entity_id=waiting.resource_associated_entity_id
                    AND held.resource_description=waiting.resource_description
                    AND held.resource_lock_partition=waiting.resource_lock_partition
                JOIN sys.partitions p ON p.hobt_id=held.resource_associated_entity_id
                JOIN sys.indexes i ON i.object_id=p.object_id AND i.index_id=p.index_id
                WHERE r.database_id=DB_ID() AND r.blocking_session_id=(SELECT LockerSessionId FROM {gate})
                    AND r.wait_type LIKE N'LCK_M_%' AND waiting.resource_database_id=DB_ID()
                    AND waiting.resource_type=N'KEY' AND waiting.request_status IN (N'WAIT',N'CONVERT')
                    AND held.request_status=N'GRANT' AND held.request_mode=N'X'
                    AND p.object_id=OBJECT_ID(N'aioffice.DataSources') AND i.is_primary_key=1
                    AND CHARINDEX(N'UPDATE [aioffice].[DataSources]',t.text)>0
                    AND CHARINDEX(N'[LogicalName] =',t.text)>0 AND CHARINDEX(N'[Purpose] =',t.text)>0
                    AND CHARINDEX(N'[MaxConcurrency] =',t.text)>0 AND CHARINDEX(N'WHERE',t.text)>0
                    AND CHARINDEX(N'[TenantId] =',t.text)>0 AND CHARINDEX(N'[CompanyId] =',t.text)>0
                    AND CHARINDEX(N'[Id] =',t.text)>0 AND {shape};"""

        def wait_for_lock(blocked=False, metadata_only=True):
            deadline = time.monotonic() + 8
            while time.monotonic() < deadline:
                if locker.poll() is not None:
                    race_diagnostics()
                    raise RuntimeError("Owned SQL race session ended before the gate")
                if blocked:
                    probe = write_gate_probe(metadata_only)
                else:
                    probe = f"""USE AIOfficeLocal; SELECT COUNT(*) FROM sys.dm_exec_requests r
                        JOIN sys.dm_tran_locks held ON held.request_session_id=r.session_id
                        JOIN sys.partitions p ON p.hobt_id=held.resource_associated_entity_id
                        JOIN sys.indexes i ON i.object_id=p.object_id AND i.index_id=p.index_id
                        WHERE r.session_id=(SELECT LockerSessionId FROM {gate}) AND r.wait_type=N'WAITFOR'
                            AND held.resource_database_id=DB_ID() AND held.resource_type=N'KEY'
                            AND held.request_status=N'GRANT' AND held.request_mode=N'X'
                            AND p.object_id=OBJECT_ID(N'aioffice.DataSources') AND i.is_primary_key=1;"""
                if int(sql(probe)) > 0:
                    return
                if blocked and pending.done():
                    race_diagnostics()
                    raise RuntimeError("Metadata HTTP request completed before the required server-load interleaving")
                time.sleep(0.1)
            race_diagnostics()
            raise RuntimeError("Real SQL metadata race did not reach the required server-load interleaving")

        def run_write_race(payload, rotation, metadata_only):
            nonlocal locker, executor, pending
            pending = None
            sql(f"UPDATE {gate} SET Phase=0,LockerSessionId=0;")
            lock_query = f"""SET NOCOUNT ON; SET XACT_ABORT ON; USE AIOfficeLocal;
                UPDATE {gate} SET LockerSessionId=@@SPID;
                BEGIN TRANSACTION;
                UPDATE aioffice.DataSources WITH (ROWLOCK) SET Kind=N'{rotation['kind']}',
                    Environment=N'{rotation['environment']}',AllowRead={int(rotation['allowRead'])},
                    AllowWrite={int(rotation['allowWrite'])},ConnectionSecretReference=N'{rotation['reference']}'
                    WHERE {source_scope} AND Id='{fixture_id}';
                IF @@ROWCOUNT<>1 BEGIN ROLLBACK; THROW 51000,'Owned target missing',1; END;
                DECLARE @deadline datetime2=DATEADD(second,12,SYSUTCDATETIME());
                WHILE (SELECT Phase FROM {gate})=0 AND SYSUTCDATETIME()<@deadline WAITFOR DELAY '00:00:00.100';
                IF (SELECT Phase FROM {gate})<>1 BEGIN ROLLBACK; THROW 51000,'Metadata race gate timeout',1; END;
                COMMIT;"""
            locker = subprocess.Popen([*compose, "exec", "-T", "sql", "sh", "-c",
                'SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" /opt/mssql-tools18/bin/sqlcmd '
                '-S localhost -U sa -C -I -b -m 1 -h -1 -W -Q "$1"', "sql", lock_query],
                stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
            wait_for_lock()
            suffix = "/metadata" if metadata_only else ""
            executor = ThreadPoolExecutor(max_workers=1)
            pending = executor.submit(http, f"/api/local/data-sources/{fixture_id}{suffix}{selector}", payload,
                                      headers={"Origin": web}, method="PUT")
            wait_for_lock(blocked=True, metadata_only=metadata_only)
            # No release is possible until the exact UPDATE waits on this target's
            # granted primary-key X lock. A read/other row/other locker is insufficient.
            assert not pending.done(), "Write gate must precede HTTP completion"
            print("PASS owned target UPDATE blocked after server load: " + ("metadata-only" if metadata_only else "full-PUT negative control"), flush=True)
            sql(f"UPDATE {gate} SET Phase=1;")
            _, lock_error = locker.communicate(timeout=15)
            assert locker.returncode == 0, clean(lock_error[-1500:])
            status, headers, response = pending.result(timeout=15)
            executor.shutdown(wait=True)
            executor = None
            assert status == 200 and "no-store" in headers.get("Cache-Control", "")
            assert "connectionSecretReference" not in response
            assert all(response[key] == payload[key] for key in ("logicalName", "purpose", "maxConcurrency", "isEnabled"))
            assert sql(f"""USE AIOfficeLocal; SELECT COUNT(*) FROM aioffice.DataSources
                WHERE {source_scope} AND Id='{fixture_id}' AND LogicalName=N'{payload['logicalName']}'
                    AND Purpose=N'{payload['purpose']}' AND MaxConcurrency={payload['maxConcurrency']}
                    AND IsEnabled={int(payload['isEnabled'])}
                    AND ConnectionSecretReference=N'{rotation['reference']}'
                    AND CONVERT(varchar(33),CreatedAtUtc,126)='{created_at}';""") == "1"
            protected_count = sql(f"""USE AIOfficeLocal; SELECT COUNT(*) FROM aioffice.DataSources
                WHERE {source_scope} AND Id='{fixture_id}' AND Kind=N'{rotation['kind']}'
                    AND Environment=N'{rotation['environment']}' AND AllowRead={int(rotation['allowRead'])}
                    AND AllowWrite={int(rotation['allowWrite'])}
                    AND ConnectionSecretReference=N'{rotation['reference']}';""")
            assert protected_count == ("1" if metadata_only else "0"), "Protected-column preservation check or negative control failed"
            status, _, fresh_sources = http("/api/local/data-sources" + selector)
            assert status == 200
            fresh = next(item for item in fresh_sources if item["id"] == fixture_id)
            expected_policy = rotation if metadata_only else payload
            assert all(response[key] == expected_policy[key] for key in ("kind", "environment", "allowRead", "allowWrite"))
            assert all(fresh[key] == expected_policy[key] for key in ("kind", "environment", "allowRead", "allowWrite"))
            assert all(fresh[key] == payload[key] for key in ("logicalName", "purpose", "maxConcurrency", "isEnabled"))
            return fresh

        # Fresh client GET precedes the uncommitted rotation. No duplicate name exists.
        assert http("/api/local/data-sources" + selector)[0] == 200
        fresh = run_write_race({"logicalName": "acceptance-metadata-" + uuid.uuid4().hex,
            "purpose": "Metadata-only concurrency acceptance", "maxConcurrency": 7, "isEnabled": False},
            {"kind": "Postgres", "environment": "Production", "allowRead": False, "allowWrite": True,
             "reference": "secretref://env/SOURCE246_ROTATED"}, metadata_only=True)
        # Actual full-PUT negative control: after this client GET, commit an interim
        # policy before Core loads, making every stale policy assignment EF-modified.
        # Otherwise assigning the same tracked snapshot would be a false negative.
        stale_full = {key: fresh[key] for key in ("logicalName", "purpose", "maxConcurrency", "isEnabled",
                                                  "kind", "environment", "allowRead", "allowWrite")}
        stale_full.update(logicalName="acceptance-full-control-" + uuid.uuid4().hex,
                          purpose="Full-PUT negative control", maxConcurrency=11, isEnabled=True,
                          connectionSecretReference=None)
        sql(f"""USE AIOfficeLocal; UPDATE aioffice.DataSources SET Kind=N'InterimKind',Environment=N'Staging',
            AllowRead=1,AllowWrite=0,ConnectionSecretReference=N'secretref://env/SOURCE246_INTERIM'
            WHERE {source_scope} AND Id='{fixture_id}';""")
        run_write_race(stale_full, {"kind": "RotatedKind", "environment": "RotatedEnvironment",
            "allowRead": True, "allowWrite": False, "reference": "secretref://env/SOURCE246_CONTROL_ROTATED"}, metadata_only=False)
    finally:
        # Only this gate/process and the single owned row are eligible for cleanup.
        if locker is not None and locker.poll() is None:
            if gate_created:
                sql(f"UPDATE {gate} SET Phase=2;")
            try:
                locker.communicate(timeout=15)
            except subprocess.TimeoutExpired:
                locker.kill()
                locker.communicate(timeout=5)
        if executor is not None:
            executor.shutdown(wait=True, cancel_futures=True)
        if gate_created:
            sql(f"DROP TABLE {gate};")
        if fixture_id is not None:
            sql(f"USE AIOfficeLocal; DELETE FROM aioffice.DataSources WHERE {source_scope} AND Id='{fixture_id}';")
    assert fingerprint("DataSources", source_scope, "Id") == source_before, "Admin fixture cleanup changed an existing source"
    print("PASS real SQL admin revoke/restore, issued-session API/BFF denial, unchanged sources and admin CRUD")
    print("PASS real SQL metadata-only strict contract and protected-column rotation after server load")
    print("PASS real SQL full-PUT negative control fails protected-policy preservation under the same write gate")

    # The fallback executor performs real read-only metadata collection without fabricating an AI answer.
    status, _, accepted = http("/api/local/tasks" + selector,
        {"dataSourceId": source, "question": "Inspect the local sample database metadata"})
    assert status == 202, f"Worker task submission status {status}"
    task = accepted["taskId"]
    def completed():
        status, _, snapshot = http(f"/api/local/tasks/{task}{selector}")
        # System.Text.Json's default numeric enum: TaskExecutionStatus.Completed == 6.
        return status == 200 and snapshot.get("taskStatus") in (6, "Completed") and bool(snapshot.get("resultPayloadJson"))
    wait_for(completed)
    print("PASS real RabbitMQ worker execution and persisted result")

    # Data changes and revoked access must survive a full stop/start and a repeated bootstrap.
    sql("USE AIOfficeSample; INSERT dbo.LocalSample VALUES (2, N'Retained data');")
    sql(f"USE AIOfficeLocal; UPDATE aioffice.Users SET IsActive=0 WHERE TenantId='{tenant}' AND Id='{user}';")
    assert http("/api/auth/context", base=api, headers=auth)[0] == 403
    login(expected=403)
    run("down", "--remove-orphans")  # deliberately keep every named volume
    # Configuration generation must preserve identifiers and credentials too.
    generated = subprocess.run(["pwsh", "-NoProfile", "-File", "infra/initialize-local-config.ps1",
        "-DataDirectory", str(directory)], capture_output=True, text=True, timeout=30)
    assert generated.returncode == 0, "Repeat configuration generation failed"
    assert hashlib.sha256(manifest_file.read_bytes()).hexdigest() == original_manifest
    run("up", "-d", timeout=600)
    ready()
    assert sql("USE AIOfficeSample; SELECT COUNT(*) FROM dbo.LocalSample WHERE Id=2;") == "1"
    assert http("/api/auth/context", base=api, headers=auth)[0] == 403
    login(expected=403)
    assert sql(f"USE AIOfficeLocal; SELECT COUNT(*) FROM aioffice.Users WHERE TenantId='{tenant}' AND Id='{user}';") == "1"
    assert sql(f"USE AIOfficeLocal; SELECT COUNT(*) FROM aioffice.DataSources WHERE Id='{source}';") == "1"
    sql(f"USE AIOfficeLocal; UPDATE aioffice.Users SET IsActive=1 WHERE TenantId='{tenant}' AND Id='{user}';")
    login()
    assert completed()
    print("PASS repeat configuration/bootstrap, retained SQL and identity, inactive-user denial and retained task")
    print("PASS complete local stack integration")


if __name__ == "__main__":
    main()
