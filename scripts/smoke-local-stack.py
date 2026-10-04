"""Real local SQL/OIDC/broker/API/worker/FE gate. No credentials in output or argv."""
import argparse
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
    try:
        sql(f"USE AIOfficeLocal; DELETE FROM aioffice.RoleAssignments WHERE {role_scope} AND RoleKey COLLATE Latin1_General_100_BIN2=N'admin';")
        status, _, context = http("/api/auth/context", base=api, headers=auth)
        assert status == 200 and "admin" not in context["roles"], "Issued token retained revoked authority"
        assert http("/api/data-sources", base=api, headers=auth)[0] == 200
        assert http("/api/local/data-sources" + selector)[0] == 200
        for path, method in (("/api/data-sources/", "POST"), (f"/api/data-sources/{source}", "PUT")):
            assert http(path, metadata, base=api, headers=auth, method=method)[0] == 403
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

    # Exercise successful BFF create/update against the real database, then remove only this fixture.
    fixture_name = "acceptance-admin-" + uuid.uuid4().hex
    allowed = {**metadata, "logicalName": fixture_name, "connectionSecretReference": "secretref://env/PILOT_ERP_CONNECTION",
               "allowRead": True, "allowWrite": False, "isEnabled": True}
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
    finally:
        sql(f"USE AIOfficeLocal; DELETE FROM aioffice.DataSources WHERE {source_scope} AND LogicalName=N'{fixture_name}';")
    assert fingerprint("DataSources", source_scope, "Id") == source_before, "Admin fixture cleanup changed an existing source"
    print("PASS real SQL admin revoke/restore, issued-session API/BFF denial, unchanged sources and admin CRUD")

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
