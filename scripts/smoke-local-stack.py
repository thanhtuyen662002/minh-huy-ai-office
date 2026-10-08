"""Real local SQL/OIDC/broker/API/worker/FE gate. No credentials in output or argv."""
import argparse
from concurrent.futures import ThreadPoolExecutor
import hashlib
import hmac
import base64
from http.cookiejar import CookieJar
import json
import os
import re
import subprocess
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
import importlib.util
from pathlib import Path


def binding_permission_diagnostic_query():
    """Evaluate the shipping predicates as the fixture login; emit only labels/bits."""
    verifier = Path("src/Platform.Persistence/BindingStorePermissionVerifier.cs").read_text(encoding="utf-8")
    query = verifier.split('internal const string VerificationSql = """', 1)[1].split('""";', 1)[0]
    query = re.sub(r"--[^\n]*", "", query)
    expression = query.split("SELECT CASE WHEN", 1)[1].rsplit("THEN 1 ELSE 0 END;", 1)[0].strip()
    predicates = re.split(r"\n\s{10}AND (?=[A-Z])", expression)
    checks = [f"SELECT N'permission_check_{index:02d}' AS CheckId, "
        f"CASE WHEN ({predicate.strip()}) THEN N'PASS' ELSE N'FAIL_OR_UNKNOWN' END AS Result"
        for index, predicate in enumerate(predicates, 1)]
    return "USE AIOfficeLocal; EXECUTE AS LOGIN=N'aioffice_runtime'; " + " UNION ALL ".join(checks) + "; REVERT;"


def erp_permission_diagnostic_query():
    """Only fixed predicate numbers/bits from the owned sample identity."""
    verifier = Path("src/Platform.Persistence/ErpReadOnlyConnectionVerifier.cs").read_text(encoding="utf-8")
    query = verifier.split('internal const string VerificationSql = """', 1)[1].split('""";', 1)[0]
    query = re.sub(r"--[^\n]*", "", query)
    expression = query.split("SELECT CASE WHEN", 1)[1].rsplit("THEN 1 ELSE 0 END;", 1)[0].strip()
    predicates = re.split(r"\n\s{10}AND (?=[A-Z])", expression)
    checks = [f"SELECT N'erp_check_{index:02d}' AS CheckId, "
        f"CASE WHEN ({predicate.strip()}) THEN N'PASS' ELSE N'FAIL_OR_UNKNOWN' END AS Result"
        for index, predicate in enumerate(predicates, 1)]
    checks.extend([
        "SELECT N'erp_schema_business_visibility', CASE WHEN NOT EXISTS (SELECT 1 FROM sys.schemas s WHERE s.name NOT IN(N'sys',N'INFORMATION_SCHEMA') AND ISNULL(HAS_PERMS_BY_NAME(s.name,N'SCHEMA',N'VIEW DEFINITION'),0)<>1) THEN N'PASS' ELSE N'FAIL_OR_UNKNOWN' END",
        "SELECT N'erp_schema_catalog_visibility', CASE WHEN NOT EXISTS (SELECT 1 FROM sys.schemas s WHERE s.name IN(N'sys',N'INFORMATION_SCHEMA') AND ISNULL(HAS_PERMS_BY_NAME(s.name,N'SCHEMA',N'VIEW DEFINITION'),0)<>1) THEN N'PASS' ELSE N'FAIL_OR_UNKNOWN' END",
        "SELECT N'erp_schema_effective_rights', CASE WHEN NOT EXISTS (SELECT 1 FROM sys.schemas s CROSS APPLY sys.fn_my_permissions(s.name,N'SCHEMA') p WHERE p.permission_name NOT IN(N'SELECT',N'VIEW DEFINITION',N'VIEW SECURITY DEFINITION',N'VIEW PERFORMANCE DEFINITION')) THEN N'PASS' ELSE N'FAIL_OR_UNKNOWN' END"])
    return "USE AIOfficeSample; EXECUTE AS LOGIN=N'aioffice_reader'; " + " UNION ALL ".join(checks) + "; REVERT;"


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
    # Retained password-grant compatibility controls opt in explicitly on this
    # owned smoke fixture. Shipping local setup now defaults to browser PKCE.
    legacy_environment = {**os.environ, "AIOFFICE_BROWSER_OIDC_ENABLED": "false", "AIOFFICE_LOCAL_UI_ENABLED": "true"}

    def clean(message):
        for secret in secrets:
            message = message.replace(secret, "[REDACTED]")
        return message

    def run(*arguments, timeout=900, environment=None):
        result = subprocess.run([*compose, *arguments], capture_output=True, text=True, timeout=timeout,
                                env=legacy_environment if environment is None else environment)
        if result.returncode:
            if arguments and arguments[0] == "up":
                bootstrap_log = subprocess.run([*compose, "logs", "--no-color", "--tail", "30", "bootstrap"],
                    capture_output=True, text=True, timeout=20)
                print(clean(bootstrap_log.stdout[-2000:]))
                # An actual permission failure needs effective-rights evidence.
                # Never print SQL, principal names, headers, credentials or error details.
                try:
                    diagnostic = sql(binding_permission_diagnostic_query())
                    for line in diagnostic.splitlines():
                        if re.fullmatch(r"permission_check_\d{2}\s+(?:PASS|FAIL_OR_UNKNOWN)", line.strip()):
                            print(line.strip())
                except Exception:
                    print("Binding permission diagnostics unavailable.")
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
            "username": "owner", "password": manifest["AIOFFICE_OWNER_PASSWORD"], "companyId": company},
            headers={"Origin": web})
        assert status == expected, f"FE login status {status}, expected {expected}"
        assert "no-store" in headers.get("Cache-Control", "")
        if expected == 200:
            context = body["context"]
            assert context["tenantId"] == tenant and context["companyId"] == company and context["userId"] == user
            assert "admin" in context["roles"]
            cookie = headers.get("Set-Cookie", "")
            assert "HttpOnly" in cookie and "SameSite=lax" in cookie

    shipping_environment = {key: value for key, value in os.environ.items()
                            if key not in {"AIOFFICE_BROWSER_OIDC_ENABLED", "AIOFFICE_LOCAL_UI_ENABLED"}}
    shipping = json.loads(run("config", "--format", "json", environment=shipping_environment))
    assert shipping["services"]["web"]["environment"]["AIOFFICE_BROWSER_OIDC_ENABLED"] == "true"
    assert shipping["services"]["web"]["environment"]["AIOFFICE_LOCAL_UI_ENABLED"] == "false"
    print("PASS shipping local setup defaults to browser PKCE and disabled password UI")
    profile = json.loads(run("config", "--format", "json"))
    assert profile["services"]["web"]["environment"]["AIOFFICE_BROWSER_OIDC_ENABLED"] == "false"
    assert profile["services"]["web"]["environment"]["AIOFFICE_LOCAL_UI_ENABLED"] == "true"
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
    public_issuer = "http://127.0.0.1:" + str(services["identity"]["ports"][0]["published"]) + "/realms/aioffice-local"
    assert services["identity"]["environment"]["KC_HOSTNAME"] == public_issuer.removesuffix("/realms/aioffice-local")
    assert services["identity"]["environment"]["KC_HOSTNAME_BACKCHANNEL_DYNAMIC"] == "true"
    assert services["core-api"]["environment"]["AIOffice__Authentication__Authority"] == public_issuer
    assert services["core-api"]["environment"]["AIOffice__Authentication__MetadataAddress"] == "http://identity:8080/realms/aioffice-local/.well-known/openid-configuration"
    browser_key = services["web"]["environment"]["AIOFFICE_BROWSER_OIDC_TRANSACTION_KEY"]
    expected_key = base64.urlsafe_b64encode(hmac.digest(manifest["AIOFFICE_RUNTIME_PASSWORD"].encode(),
        ("aioffice-local-browser-key-v1|" + manifest["AIOFFICE_INSTALLATION_ID"]).encode(), hashlib.sha256)).decode().rstrip("=")
    assert browser_key == expected_key and re.fullmatch("[A-Za-z0-9_-]{43}", browser_key)
    assert all(secret not in json.dumps(services["web"]["environment"]) for secret in secrets)
    print("PASS local service configuration, secret references and isolated loopback profile")
    run("up", "--build", "-d", timeout=1500)
    ready()
    identity = "http://127.0.0.1:" + str(services["identity"]["ports"][0]["published"])
    with urllib.request.urlopen(identity + "/realms/aioffice-local/.well-known/openid-configuration", timeout=15) as discovery:
        document = json.loads(discovery.read(65537))
        assert document["issuer"] == public_issuer
        assert document["authorization_endpoint"] == public_issuer + "/protocol/openid-connect/auth"
    print("PASS actual public issuer and explicit internal metadata configuration with private purpose-separated browser key")
    browser_redirect = web + "/api/local/session/oidc/callback"

    def identity_admin(path, payload=None, method=None):
        form = urllib.parse.urlencode({"client_id": "admin-cli", "grant_type": "password",
            "username": "bootstrap-admin", "password": manifest["AIOFFICE_IDENTITY_ADMIN_PASSWORD"]}).encode()
        with urllib.request.urlopen(urllib.request.Request(identity + "/realms/master/protocol/openid-connect/token",
            data=form, headers={"Content-Type": "application/x-www-form-urlencoded"}), timeout=15) as response:
            admin_token = json.loads(response.read(65537))["access_token"]
        request = urllib.request.Request(identity + "/admin/realms/aioffice-local/" + path,
            data=None if payload is None else json.dumps(payload).encode(),
            headers={"Authorization": "Bearer " + admin_token, "Content-Type": "application/json"},
            method=method or ("GET" if payload is None else "PUT"))
        with urllib.request.urlopen(request, timeout=15) as response:
            raw = response.read(65537)
            assert len(raw) <= 65536, "Identity client response exceeded its bound"
            return json.loads(raw) if raw else None

    def browser_client_contract(expected_enabled=True):
        clients = identity_admin("clients?clientId=aioffice-browser&search=false")
        assert len(clients) == 1 and clients[0]["clientId"] == "aioffice-browser"
        client_id = str(uuid.UUID(clients[0]["id"]))
        client = identity_admin("clients/" + client_id)
        assert client["id"] == client_id and client["enabled"] is expected_enabled
        assert client["publicClient"] and client["standardFlowEnabled"] and client["protocol"] == "openid-connect"
        for flag in ("directAccessGrantsEnabled", "implicitFlowEnabled", "serviceAccountsEnabled", "fullScopeAllowed"):
            assert not client[flag], "Browser client enabled an unsupported flow/scope"
        assert client["redirectUris"] == [browser_redirect] and not client["webOrigins"]
        assert set(client["defaultClientScopes"]) == {"basic", "profile", "email"} and not client["optionalClientScopes"]
        attributes = client["attributes"]
        assert attributes["aioffice.installation-id"] == manifest["AIOFFICE_INSTALLATION_ID"]
        assert attributes["pkce.code.challenge.method"] == "S256"
        assert attributes["id.token.signed.response.alg"] == attributes["access.token.signed.response.alg"] == "RS256"
        mappers = {mapper["name"]: mapper for mapper in client["protocolMappers"]}
        assert set(mappers) == {"identity-provider", "api-audience"}
        for mapper in mappers.values():
            assert mapper["config"]["access.token.claim"] == "true" and mapper["config"]["id.token.claim"] == "false"
        assert mappers["identity-provider"]["config"]["claim.value"] == "local-keycloak"
        assert mappers["api-audience"]["config"]["included.custom.audience"] == "aioffice-local"
        return client_id, client

    browser_client_id, _ = browser_client_contract()
    print("PASS actual owned browser client, exact callback, Code/S256-only flows and access-only API audience")
    status, _, page = http("/")
    assert status == 200 and company in page
    assert all(secret not in page for secret in secrets)
    login()
    selector = "?companyId=" + company
    # These are real cookie mutations and issued sessions, not mocked identity responses.
    session_credentials = {"username": "owner", "password": manifest["AIOFFICE_OWNER_PASSWORD"], "companyId": company}
    def cookie_snapshot():
        return sorted((cookie.domain, cookie.path, cookie.name, cookie.value) for cookie in cookies)
    issued_cookies = cookie_snapshot()
    issued_token = next(cookie.value for cookie in cookies if cookie.name == "aioffice_local_access_token")
    def preserved_session():
        assert cookie_snapshot() == issued_cookies, "Denied request mutated issued cookies"
        status, headers, body = http("/api/local/session" + selector)
        assert status == 200 and "no-store" in headers.get("Cache-Control", "")
        status, headers, body = http("/api/auth/context", base=api,
            headers={"Authorization": "Bearer " + issued_token, "X-AIOffice-Company-Id": company})
        assert status == 200 and body["tenantId"] == tenant and body["companyId"] == company and body["userId"] == user
        assert "no-store" in headers.get("Cache-Control", "")
    rejected_origins = [{}, {"Origin": "null"}, {"Origin": "https://foreign.example.invalid"},
        {"Origin": "http://127.0.0.1:3001"}, {"Origin": web + "/"},
        {"Origin": "https://foreign.example.invalid", "X-Forwarded-Host": "foreign.example.invalid", "X-Forwarded-Proto": "https"}]
    for origin_headers in rejected_origins:
        for path, payload in (("/api/local/session/login", session_credentials), ("/api/local/session/logout", None)):
            status, headers, body = http(path, payload, headers=origin_headers, method="POST")
            assert status == 403 and "no-store" in headers.get("Cache-Control", "")
            assert not headers.get_all("Set-Cookie"), "Denied request emitted a cookie"
            assert all(secret not in json.dumps(body) for secret in secrets) and issued_token not in json.dumps(body)
            preserved_session()
    for payload, extra_headers, expected in (
        ({**session_credentials, "tenantId": tenant}, {}, 400),
        ({**session_credentials, "password": "á" * 4096}, {}, 413),
        (session_credentials, {"Content-Type": "text/plain"}, 415)):
        status, headers, body = http("/api/local/session/login", payload, headers={"Origin": web, **extra_headers})
        assert status == expected and "no-store" in headers.get("Cache-Control", "")
        assert not headers.get_all("Set-Cookie")
        assert all(secret not in json.dumps(body) for secret in secrets) and issued_token not in json.dumps(body)
        preserved_session()
    status, headers, body = http("/api/local/session/logout", headers={"Origin": web}, method="POST")
    assert status == 200 and "no-store" in headers.get("Cache-Control", "")
    assert "Max-Age=0" in headers.get("Set-Cookie", "") and "HttpOnly" in headers.get("Set-Cookie", "")
    assert not any(cookie.name == "aioffice_local_access_token" for cookie in cookies)
    status, headers, body = http("/api/local/session" + selector)
    assert status == 401 and "no-store" in headers.get("Cache-Control", "")
    login()
    print("PASS actual OIDC session Origin and input refusals preserve issued API/BFF authority; same-origin logout clears and sign-in restores cookie")
    status, headers, sources = http("/api/local/data-sources" + selector)
    assert status == 200 and "no-store" in headers.get("Cache-Control", "")
    assert any(item["id"] == source and item["allowRead"] and not item["allowWrite"] for item in sources)
    assert all(secret not in json.dumps(sources) for secret in secrets)
    status, _, result = http(f"/api/local/data-sources/{source}/connection-test{selector}", {}, headers={"Origin": web})
    if not (status == 200 and result["succeeded"]) and os.environ.get("CI") == "true" and os.environ.get("GITHUB_ACTIONS") == "true" \
            and os.environ.get("RUNNER_TEMP") and directory == (Path(os.environ["RUNNER_TEMP"]) / "aioffice-local").resolve():
        try:
            for line in sql(erp_permission_diagnostic_query()).splitlines():
                if re.fullmatch(r"erp_(?:check_\d{2}|schema_(?:business_visibility|catalog_visibility|effective_rights))\s+(?:PASS|FAIL_OR_UNKNOWN)", line.strip()):
                    print(line.strip(), flush=True)
        except Exception:
            print("Owned ERP permission diagnostics unavailable.", flush=True)
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

    # A foreign tenant deliberately reuses the same company/user IDs. Its
    # member and role must never enter the caller's actual SQL projection.
    directory_tenant = str(uuid.uuid4())
    sql(f"""USE AIOfficeLocal; SET XACT_ABORT ON; BEGIN TRANSACTION;
        INSERT aioffice.Companies(TenantId,Id,Code,Name) VALUES('{directory_tenant}','{company}',N'member-fixture',N'FOREIGN_DIRECTORY_COMPANY');
        INSERT aioffice.Users(TenantId,Id,IdentityProvider,Subject,DisplayName) VALUES
            ('{directory_tenant}','{user}',N'PRIVATE_DIRECTORY_PROVIDER',N'PRIVATE_DIRECTORY_SUBJECT',N'FOREIGN_DIRECTORY_MEMBER');
        INSERT aioffice.CompanyMemberships(TenantId,CompanyId,UserId) VALUES('{directory_tenant}','{company}','{user}');
        INSERT aioffice.RoleAssignments(TenantId,CompanyId,UserId,RoleKey) VALUES('{directory_tenant}','{company}','{user}',N'FOREIGN_DIRECTORY_ROLE');
        COMMIT TRANSACTION;""")
    try:
        for member_path, member_base, member_headers in (
            ("/api/company/members?offset=0&limit=25", api, auth),
            ("/api/local/company/members" + selector + "&offset=0&limit=25", web, {})):
            member_status, member_headers_out, member_page = http(member_path, base=member_base, headers=member_headers)
            assert member_status == 200 and "no-store" in member_headers_out.get("Cache-Control", "")
            assert member_page["companyId"] == company and member_page["offset"] == 0 and member_page["limit"] == 25
            assert len(member_page["items"]) == 1 and not member_page["hasMore"]
            member = member_page["items"][0]
            assert set(member) == {"userId", "displayName", "userActive", "membershipActive", "roles"}
            assert member["userId"] == user and member["userActive"] and member["membershipActive"] and "admin" in member["roles"]
            assert all(secret not in json.dumps(member_page) for secret in [*secrets, token]) and "secretref://" not in json.dumps(member_page)
            assert "FOREIGN_DIRECTORY_" not in json.dumps(member_page) and "PRIVATE_DIRECTORY_" not in json.dumps(member_page)
    finally:
        sql(f"""USE AIOfficeLocal; SET XACT_ABORT ON; BEGIN TRANSACTION;
            DELETE aioffice.RoleAssignments WHERE TenantId='{directory_tenant}';
            DELETE aioffice.CompanyMemberships WHERE TenantId='{directory_tenant}';
            DELETE aioffice.Users WHERE TenantId='{directory_tenant}';
            DELETE aioffice.Companies WHERE TenantId='{directory_tenant}'; COMMIT TRANSACTION;""")
    assert http("/api/company/members", base=api, headers=wrong)[0] == 403
    assert http("/api/company/members?limit=101", base=api, headers=auth)[0] == 400

    def fingerprint(table, scope, order):
        result = sql(f"""USE AIOfficeLocal; SELECT CONVERT(varchar(64), HASHBYTES('SHA2_256',
            (SELECT * FROM aioffice.{table} WHERE {scope} ORDER BY {order}
             FOR JSON PATH, INCLUDE_NULL_VALUES)), 2);""")
        assert len(result) == 64, "SQL fixture fingerprint failed"
        return result

    source_before = fingerprint("DataSources", source_scope, "Id")
    identity_scope = f"TenantId='{tenant}' AND Id='{user}'"
    identity_before = fingerprint("Users", identity_scope, "Id")
    identity_backup = "tempdb.dbo.AIOfficeIdentityBackup_" + uuid.uuid4().hex
    sql(f"USE AIOfficeLocal; SELECT TenantId,Id,IdentityProvider,Subject INTO {identity_backup} FROM aioffice.Users WHERE {identity_scope};")

    def restore_identity():
        sql(f"""USE AIOfficeLocal; UPDATE u SET IdentityProvider=b.IdentityProvider,Subject=b.Subject
            FROM aioffice.Users u JOIN {identity_backup} b ON u.TenantId=b.TenantId AND u.Id=b.Id;""")

    try:
        for identity_column, identity_expression in (
            ("IdentityProvider", "UPPER(IdentityProvider)"),
            ("Subject", "UPPER(Subject)"),
            ("IdentityProvider", "IdentityProvider+N' '"),
            ("Subject", "Subject+N' '")):
            try:
                sql(f"USE AIOfficeLocal; UPDATE aioffice.Users SET {identity_column}={identity_expression} WHERE {identity_scope};")
                # SQL equality, including binary equality's space padding, is
                # not the final authority for opaque signed identity strings.
                assert sql(f"""USE AIOfficeLocal; SELECT COUNT(*) FROM aioffice.Users u
                    JOIN {identity_backup} b ON u.TenantId=b.TenantId AND u.Id=b.Id
                    WHERE u.IdentityProvider COLLATE Latin1_General_100_CI_AS=b.IdentityProvider COLLATE Latin1_General_100_CI_AS
                    AND u.Subject COLLATE Latin1_General_100_CI_AS=b.Subject COLLATE Latin1_General_100_CI_AS;""") == "1", "Native identity alias negative control failed"
                for identity_path, identity_base, identity_headers in (
                    ("/api/auth/context", api, auth), ("/api/local/session" + selector, web, {})):
                    denied_status, denied_headers, denied_body = http(identity_path, base=identity_base, headers=identity_headers)
                    assert denied_status == 403 and "no-store" in denied_headers.get("Cache-Control", ""), "Opaque identity alias granted authority"
                    assert all(secret not in json.dumps(denied_body) for secret in [*secrets, token])
            finally:
                restore_identity()
                assert fingerprint("Users", identity_scope, "Id") == identity_before, "Identity fixture restoration changed stored fields"
                assert http("/api/auth/context", base=api, headers=auth)[0] == 200
    finally:
        restore_identity()
        sql(f"DROP TABLE {identity_backup};")
    print("PASS real SQL native identity alias negative controls, exact API/BFF denial and retained identity restoration")
    bindings_before = fingerprint("DataSourceSecretBindings", source_scope, "Id")
    assert sql(f"""USE AIOfficeLocal; SELECT COUNT(*) FROM aioffice.DataSourceSecretBindings
        WHERE {source_scope} AND CanonicalReference=N'secretref://env/PILOT_ERP_CONNECTION'
            COLLATE Latin1_General_100_BIN2 AND IsEnabled=1 AND Version=1;""") == "1"

    def runtime_statement(statement, expected="DENIED"):
        # Run only in the disposable hosted stack as its actual runtime identity.
        # Always roll back a successful adversarial statement too.
        proof = sql(f"""USE AIOfficeLocal; EXECUTE AS LOGIN=N'aioffice_runtime';
            BEGIN TRY BEGIN TRANSACTION; {statement}; ROLLBACK TRANSACTION; SELECT N'ALLOWED'; END TRY
            BEGIN CATCH IF @@TRANCOUNT>0 ROLLBACK TRANSACTION;
                IF ERROR_NUMBER() IN (229,262,15151,15247,15517,15406,1088,4701) SELECT N'DENIED'; ELSE THROW;
            END CATCH; REVERT;""")
        assert proof == expected, "Runtime SQL permission proof mismatch"

    runtime_statement("UPDATE aioffice.DataSourceSecretBindings SET IsEnabled=0")
    runtime_statement("UPDATE aioffice.DataSourceSecretBindings SET Label=N'Forbidden column write'")
    runtime_statement("DELETE FROM aioffice.DataSourceSecretBindings")
    runtime_statement(f"""INSERT aioffice.DataSourceSecretBindings
        (TenantId,CompanyId,Id,CanonicalReference,Label) VALUES
        ('{tenant}','{company}','{uuid.uuid4()}',N'secretref://env/FORBIDDEN',N'Forbidden')""")
    runtime_statement("ALTER TABLE aioffice.DataSourceSecretBindings ADD Forbidden int NULL")
    runtime_statement("TRUNCATE TABLE aioffice.DataSourceSecretBindings")
    runtime_statement("ALTER AUTHORIZATION ON OBJECT::aioffice.DataSourceSecretBindings TO aioffice_runtime")
    runtime_statement("ALTER ROLE db_owner ADD MEMBER aioffice_runtime")
    runtime_statement("EXECUTE AS USER=N'dbo'; REVERT")
    assert fingerprint("DataSourceSecretBindings", source_scope, "Id") == bindings_before
    print("PASS real runtime binding SELECT and INSERT/UPDATE/column/DELETE/ALTER/TRUNCATE/ownership/escalation denial")
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
        for member_path, member_base, member_headers in (
            ("/api/company/members", api, auth), ("/api/local/company/members" + selector, web, {})):
            denied_status, denied_headers, denied_body = http(member_path, base=member_base, headers=member_headers)
            assert denied_status == 403 and "no-store" in denied_headers.get("Cache-Control", "")
            assert all(secret not in json.dumps(denied_body) for secret in [*secrets, token])
        choice_status, choice_headers, choice_body = http("/api/data-sources/registration-options", base=api, headers=auth)
        assert choice_status == 403 and "no-store" in choice_headers.get("Cache-Control", ""), "Binding choices retained revoked admin authority"
        assert "secretref://" not in json.dumps(choice_body)
        assert http("/api/data-sources", base=api, headers=auth)[0] == 200
        assert http("/api/local/data-sources" + selector)[0] == 200
        revoked_registration = {"bindingId": source, "bindingVersion": "1", "operationId": str(uuid.uuid4()),
            "logicalName": "revoked-registration-" + uuid.uuid4().hex, "environment": "Development",
            "purpose": "Fresh admin revocation acceptance", "maxConcurrency": 2}
        for denied_path, denied_base, denied_headers in (
            ("/api/data-sources/read-only-registration", api, auth),
            ("/api/local/data-sources/read-only-registration" + selector, web, {"Origin": web})):
            denied_status, denied_response_headers, denied_body = http(denied_path, revoked_registration,
                base=denied_base, headers=denied_headers)
            assert denied_status == 403 and "no-store" in denied_response_headers.get("Cache-Control", "")
            assert "secretref://" not in json.dumps(denied_body)
        assert sql(f"USE AIOfficeLocal; SELECT COUNT(*) FROM aioffice.DataSourceRegistrationAudits WHERE {source_scope} AND OperationId='{revoked_registration['operationId']}';") == "0"
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
    assert http("/api/company/members", base=api, headers=auth)[0] == 200
    print("PASS real SQL scoped admin member directory, safe DTO and issued-session API/BFF revocation")
    assert sql("""USE AIOfficeLocal; SELECT COUNT(*) FROM sys.dm_exec_sessions
        WHERE login_name=N'aioffice_runtime' AND status=N'sleeping'
          AND is_user_process=1 AND transaction_isolation_level<>2;""") == "0", "Registration leaked session isolation into the runtime connection pool"
    choice_status, choice_headers, choices = http("/api/data-sources/registration-options?offset=0&limit=1", base=api, headers=auth)
    assert choice_status == 200 and "no-store" in choice_headers.get("Cache-Control", "")
    assert len(choices["items"]) == 1 and choices["offset"] == 0 and choices["limit"] == 1
    choice = choices["items"][0]
    assert set(choice) == {"bindingId", "label", "version", "versionToken"} and choice["version"] > 0
    assert choice["versionToken"] == str(choice["version"])
    assert "secretref://" not in json.dumps(choices) and all(secret not in json.dumps(choices) for secret in [*secrets, token])
    assert sql(f"USE AIOfficeLocal; SELECT COUNT(*) FROM aioffice.DataSourceSecretBindings WHERE TenantId='{tenant}' AND CompanyId='{company}' AND Id='{uuid.UUID(choice['bindingId'])}' AND IsEnabled=1 AND Version={int(choice['version'])};") == "1"
    assert http("/api/data-sources/registration-options", base=api, headers=wrong)[0] == 403
    assert http("/api/data-sources/registration-options?limit=101", base=api, headers=auth)[0] == 400
    print("PASS real SQL scoped nonsecret binding choices and fresh admin revoke/restore")
    for path, method in (("/api/data-sources/", "POST"), (f"/api/data-sources/{foreign_source}", "PUT")):
        assert http(path, metadata, base=api, headers=wrong, method=method)[0] == 403
    assert http(f"/api/data-sources/{foreign_source}/metadata", narrow, base=api, headers=wrong, method="PUT")[0] == 403
    assert http(f"/api/data-sources/{foreign_source}/metadata", narrow, base=api, headers=auth, method="PUT")[0] == 404

    # Exercise successful BFF create/update against the real database, then remove only this fixture.
    fixture_name = "acceptance-admin-" + uuid.uuid4().hex
    allowed = {**metadata, "logicalName": fixture_name, "connectionSecretReference": "secretref://env/PILOT_ERP_CONNECTION",
               "allowRead": True, "allowWrite": False, "isEnabled": True}
    fixture_id = None
    fixture_grants = [(str(uuid.uuid4()), reference) for reference in
        ("SOURCE246_ROTATED", "SOURCE246_INTERIM", "SOURCE246_CONTROL_ROTATED")]
    gate = "tempdb.dbo.AIOfficeMetadataGate_" + uuid.uuid4().hex
    gate_created = False
    locker = executor = pending = None
    try:
        for grant_id, reference in fixture_grants:
            sql(f"""USE AIOfficeLocal; INSERT aioffice.DataSourceSecretBindings
                (TenantId,CompanyId,Id,CanonicalReference,Label,IsEnabled,Version) VALUES
                ('{tenant}','{company}','{grant_id}',N'secretref://env/{reference}',N'Owned PR247 SQL barrier fixture',1,1);""")
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
        for grant_id, _ in fixture_grants:
            sql(f"USE AIOfficeLocal; DELETE FROM aioffice.DataSourceSecretBindings WHERE {source_scope} AND Id='{grant_id}';")
    assert fingerprint("DataSources", source_scope, "Id") == source_before, "Admin fixture cleanup changed an existing source"
    assert fingerprint("DataSourceSecretBindings", source_scope, "Id") == bindings_before, "Barrier cleanup changed an existing grant"
    print("PASS real SQL admin revoke/restore, issued-session API/BFF denial, unchanged sources and admin CRUD")
    print("PASS real SQL metadata-only strict contract and protected-column rotation after server load")
    print("PASS real SQL full-PUT negative control fails protected-policy preservation under the same write gate")

    # Own every adversarial object/row; operator actions here are disposable CI only.
    # Execute the shipping verifier SQL as the actual runtime principal too.
    permission_source = Path("src/Platform.Persistence/BindingStorePermissionVerifier.cs").read_text(encoding="utf-8")
    permission_sql = permission_source.split('internal const string VerificationSql = """', 1)[1].split('""";', 1)[0]

    def permission_proof():
        return sql("USE AIOfficeLocal; EXECUTE AS LOGIN=N'aioffice_runtime'; " + permission_sql + " REVERT;")

    # Verify the supported engine's permission catalog rather than interpreting
    # an invalid HAS_PERMS_BY_NAME query's NULL as an effective denial.
    assert sql("SELECT COUNT(*) FROM sys.fn_builtin_permissions('DATABASE') "
               "WHERE permission_name=N'IMPERSONATE ANY USER';") == "0"
    assert permission_proof() == "1", "Runtime binding store permission proof failed"
    # An operator mistake must fail closed while services are already running too.
    # Dropping only this disposable runtime role deliberately restores db_datawriter
    # rights; the proof and source boundary must reject that unsafe configuration.
    try:
        sql("USE AIOfficeLocal; ALTER ROLE aioffice_binding_runtime DROP MEMBER aioffice_runtime;")
        assert permission_proof() == "0"
        runtime_statement("UPDATE aioffice.DataSourceSecretBindings SET Label=N'Unsafe-role fixture'", expected="ALLOWED")
        status, headers, denied = http(f"/api/data-sources/{source}/connection-test", {}, base=api, headers=auth)
        assert status == 403, f"Unsafe permission identity must be forbidden, status {status}"
        assert "no-store" in headers.get("Cache-Control", "")
        assert all(secret not in json.dumps(denied) for secret in secrets)
        choice_status, choice_headers, choice_body = http("/api/data-sources/registration-options", base=api, headers=auth)
        assert choice_status == 403 and "no-store" in choice_headers.get("Cache-Control", ""), "Unsafe binding store released choices"
        assert "secretref://" not in json.dumps(choice_body) and all(secret not in json.dumps(choice_body) for secret in secrets)
    finally:
        sql("USE AIOfficeLocal; ALTER ROLE aioffice_binding_runtime ADD MEMBER aioffice_runtime;")
    assert permission_proof() == "1"
    owned_source = str(uuid.uuid4())
    owned_grant = str(uuid.uuid4())
    owned_reference = "SCOPED_CASE_" + uuid.uuid4().hex.upper()
    module = "aioffice.BindingModuleGate_" + uuid.uuid4().hex
    trigger = "aioffice.BindingTriggerGate_" + uuid.uuid4().hex
    owned_scope = source_scope + f" AND Id='{owned_source}'"
    grant_scope = source_scope + f" AND Id='{owned_grant}'"
    module_created = trigger_created = False

    def connection_denied(source_id):
        status, headers, result = http(f"/api/data-sources/{source_id}/connection-test", {}, base=api, headers=auth)
        assert status == 403, f"Unauthorized source must be forbidden, status {status}"
        assert "no-store" in headers.get("Cache-Control", "")
        assert all(secret not in json.dumps(result) for secret in secrets)
        assert "secretref" not in json.dumps(result).lower()

    try:
        sql(f"""USE AIOfficeLocal; INSERT aioffice.DataSourceSecretBindings
            (TenantId,CompanyId,Id,CanonicalReference,Label,IsEnabled,Version)
            VALUES ('{tenant}','{company}','{owned_grant}',N'secretref://env/{owned_reference}',N'Owned security fixture',1,1);
            INSERT aioffice.DataSources
            (TenantId,CompanyId,Id,LogicalName,Kind,Environment,Purpose,ConnectionSecretReference,AllowRead,AllowWrite,MaxConcurrency,IsEnabled)
            VALUES ('{tenant}','{company}','{owned_source}',N'Owned security source {owned_source}',N'sql-server',N'Test',
                N'Owned security fixture',N'secretref://env/{owned_reference.lower()}',1,0,1,1);""")
        connection_denied(owned_source)  # Resource case differs from the reviewed grant.
        # The unique SQL index treats resource case as exact but rejects the same
        # scope/reference twice. Keep this proof transaction-owned and rollback.
        duplicate = sql(f"""USE AIOfficeLocal; BEGIN TRY BEGIN TRANSACTION;
            INSERT aioffice.DataSourceSecretBindings (TenantId,CompanyId,Id,CanonicalReference,Label)
            VALUES ('{tenant}','{company}','{uuid.uuid4()}',N'secretref://env/{owned_reference}',N'Duplicate fixture');
            ROLLBACK TRANSACTION; SELECT N'DUPLICATE_ALLOWED'; END TRY
            BEGIN CATCH IF @@TRANCOUNT>0 ROLLBACK TRANSACTION;
                IF ERROR_NUMBER() IN (2601,2627) SELECT N'DUPLICATE_DENIED'; ELSE THROW; END CATCH;""")
        assert duplicate == "DUPLICATE_DENIED"
        denied = {**allowed, "logicalName": "scope-denied-" + uuid.uuid4().hex,
                  "connectionSecretReference": "secretref://env/" + owned_reference.lower()}
        assert http("/api/data-sources/", denied, base=api, headers=auth)[0] == 403
        sql(f"USE AIOfficeLocal; UPDATE aioffice.DataSources SET ConnectionSecretReference=N'secretref://env/{owned_reference}' WHERE {owned_scope};")
        sql(f"USE AIOfficeLocal; UPDATE aioffice.DataSourceSecretBindings SET IsEnabled=0,Version=Version+1 WHERE {grant_scope};")
        connection_denied(owned_source)
        assert http("/api/data-sources/", {**denied, "connectionSecretReference": "secretref://env/" + owned_reference}, base=api, headers=auth)[0] == 403
        assert http("/api/tasks", {"dataSourceId": owned_source, "question": "revoked grant"},
                    base=api, headers={**auth, "Idempotency-Key": "revoke-" + uuid.uuid4().hex})[0] == 403
        # Missing grant and a grant only in another company both deny an existing row.
        sql(f"USE AIOfficeLocal; DELETE FROM aioffice.DataSourceSecretBindings WHERE {grant_scope};")
        connection_denied(owned_source)
        sql(f"""USE AIOfficeLocal; INSERT aioffice.DataSourceSecretBindings
            (TenantId,CompanyId,Id,CanonicalReference,Label,IsEnabled,Version)
            VALUES ('{tenant}','{foreign_company}','{owned_grant}',N'secretref://env/{owned_reference}',N'Owned foreign grant',1,1);""")
        connection_denied(owned_source)
        sql(f"""USE AIOfficeLocal; DELETE FROM aioffice.DataSourceSecretBindings
            WHERE TenantId='{tenant}' AND CompanyId='{foreign_company}' AND Id='{owned_grant}';
            INSERT aioffice.DataSourceSecretBindings (TenantId,CompanyId,Id,CanonicalReference,Label,IsEnabled,Version)
            VALUES ('{tenant}','{company}','{owned_grant}',N'secretref://env/{owned_reference}',N'Owned security fixture',1,1);
            UPDATE aioffice.DataSources SET ConnectionSecretReference=N'secretref://env/PILOT_ERP_CONNECTION' WHERE {owned_scope};""")

        # Direct USER impersonation can cross the binding owner's privilege
        # boundary. Its real effective permission must close the runtime/API
        # guard even though the nonexistent DATABASE permission was removed.
        try:
            sql("USE AIOfficeLocal; GRANT IMPERSONATE ON USER::aioffice_binding_operator_owner TO aioffice_runtime;")
            assert permission_proof() == "0"
            runtime_statement("EXECUTE AS USER=N'aioffice_binding_operator_owner'; "
                f"UPDATE aioffice.DataSourceSecretBindings SET Label=N'Impersonation fixture' WHERE {grant_scope}; REVERT;",
                expected="ALLOWED")
            connection_denied(source)
        finally:
            sql("USE AIOfficeLocal; REVOKE IMPERSONATE ON USER::aioffice_binding_operator_owner FROM aioffice_runtime;")
        assert permission_proof() == "1"
        print("PASS real SQL USER impersonation escalation denied by permission and API fences")

        # A dbo-owned ordinary module cannot cross the grant table's distinct owner.
        definition = f"CREATE PROCEDURE {module} AS UPDATE aioffice.DataSourceSecretBindings SET Label=N'Indirect write' WHERE {grant_scope};"
        sql("USE AIOfficeLocal; EXEC(N'" + definition.replace("'", "''") + "'); " + f"GRANT EXECUTE ON OBJECT::{module} TO aioffice_runtime;")
        module_created = True
        runtime_statement("EXEC " + module)
        assert permission_proof() == "0"
        connection_denied(owned_source)
        # EXECUTE AS OWNER demonstrates why direct DENY is insufficient for arbitrary
        # modules. Transaction rollback keeps the fixture state; the guard rejects it.
        definition = f"ALTER PROCEDURE {module} WITH EXECUTE AS OWNER AS UPDATE aioffice.DataSourceSecretBindings SET Label=N'Elevated indirect write' WHERE {grant_scope};"
        sql("USE AIOfficeLocal; EXEC(N'" + definition.replace("'", "''") + "');")
        runtime_statement("EXEC " + module, expected="ALLOWED")
        assert permission_proof() == "0"
        connection_denied(owned_source)
        sql(f"USE AIOfficeLocal; DROP PROCEDURE {module};")
        module_created = False
        assert permission_proof() == "1"

        # Trigger execution needs no explicit runtime EXECUTE grant. Verify inventory
        # denial with an enabled EXECUTE AS OWNER trigger on a runtime-writable table.
        definition = f"CREATE TRIGGER {trigger} ON aioffice.DataSources WITH EXECUTE AS OWNER AFTER UPDATE AS BEGIN SET NOCOUNT ON; UPDATE aioffice.DataSourceSecretBindings SET Label=N'Trigger write' WHERE {grant_scope}; END;"
        sql("USE AIOfficeLocal; EXEC(N'" + definition.replace("'", "''") + "');")
        trigger_created = True
        runtime_statement(f"UPDATE aioffice.DataSources SET Purpose=N'Trigger fixture' WHERE {owned_scope}", expected="ALLOWED")
        assert permission_proof() == "0"
        connection_denied(owned_source)
        sql(f"USE AIOfficeLocal; DROP TRIGGER {trigger};")
        trigger_created = False
        assert permission_proof() == "1"
        status, _, connection = http(f"/api/data-sources/{owned_source}/connection-test", {}, base=api, headers=auth)
        assert status == 200 and connection["succeeded"]
    finally:
        if trigger_created:
            sql(f"USE AIOfficeLocal; DROP TRIGGER {trigger};")
        if module_created:
            sql(f"USE AIOfficeLocal; DROP PROCEDURE {module};")
        sql(f"""USE AIOfficeLocal; DELETE FROM aioffice.DataSources WHERE {owned_scope};
            DELETE FROM aioffice.DataSourceSecretBindings WHERE TenantId='{tenant}'
                AND CompanyId IN ('{company}','{foreign_company}') AND Id='{owned_grant}';""")
    assert fingerprint("DataSources", source_scope, "Id") == source_before
    assert fingerprint("DataSourceSecretBindings", source_scope, "Id") == bindings_before
    assert permission_proof() == "1"
    print("PASS exact grant case/scope, legacy missing/revoked grant denial, and indirect procedure/trigger permission fences")

    # Owned onboarding fixture is retained for restart proof. Immutable history
    # is never deleted for cleanup; the final CI stack owns its disposable volume.
    registration = {"bindingId": choice["bindingId"], "bindingVersion": choice["versionToken"],
                    "operationId": str(uuid.uuid4()), "logicalName": "registered-erp-" + uuid.uuid4().hex,
                    "environment": "Development", "purpose": "Read-only onboarding acceptance", "maxConcurrency": 2}
    registration_path = "/api/local/data-sources/read-only-registration" + selector
    status, registration_headers, registered = http(registration_path, registration, headers={"Origin": web})
    assert status == 200 and "no-store" in registration_headers.get("Cache-Control", ""), "Audited source registration failed"
    registered_source = str(uuid.UUID(registered["id"]))
    assert registered["allowRead"] and not registered["allowWrite"] and registered["kind"] == "sql-server"
    assert "secretref://" not in json.dumps(registered) and "connectionSecretReference" not in registered
    audit_scope = source_scope + f" AND OperationId='{registration['operationId']}'"
    assert sql(f"""USE AIOfficeLocal; SELECT COUNT(*) FROM aioffice.DataSourceRegistrationAudits
        WHERE {audit_scope} AND ActorUserId='{user}' AND DataSourceId='{registered_source}'
          AND BindingId='{registration['bindingId']}' AND BindingVersion={registration['bindingVersion']};""") == "1"
    assert http(registration_path, registration, headers={"Origin": web})[2]["id"] == registered_source
    assert http(registration_path, {**registration, "purpose": "Conflicting retry"}, headers={"Origin": web})[0] == 409
    assert http(registration_path, {**registration, "operationId": str(uuid.uuid4())}, headers={"Origin": web})[0] == 409
    assert http(registration_path, {**registration, "allowWrite": True}, headers={"Origin": web})[0] == 400
    assert http(registration_path, registration, headers={"Origin": "https://foreign.invalid"})[0] == 403
    assert http("/api/data-sources/read-only-registration", registration, base=api, headers=wrong)[0] == 403
    assert fingerprint("DataSourceSecretBindings", source_scope, "Id") == bindings_before
    audit_before = fingerprint("DataSourceRegistrationAudits", source_scope, "Id")
    runtime_statement("UPDATE aioffice.DataSourceRegistrationAudits SET RequestHash=REPLICATE(N'0',64)")
    runtime_statement("DELETE FROM aioffice.DataSourceRegistrationAudits")
    runtime_statement("ALTER TABLE aioffice.DataSourceRegistrationAudits ADD Forbidden int NULL")
    runtime_statement("TRUNCATE TABLE aioffice.DataSourceRegistrationAudits")
    runtime_statement("ALTER AUTHORIZATION ON OBJECT::aioffice.DataSourceRegistrationAudits TO aioffice_runtime")
    assert fingerprint("DataSourceRegistrationAudits", source_scope, "Id") == audit_before
    # SQL Server permits a column GRANT to override a table DENY. Prove the
    # unsafe right is real, and that registration refuses it before any write.
    fresh_registration = {**registration, "operationId": str(uuid.uuid4()), "logicalName": "column-guard-" + uuid.uuid4().hex}
    try:
        sql("""USE AIOfficeLocal;
            REVOKE UPDATE ON OBJECT::aioffice.DataSourceRegistrationAudits (RequestHash) FROM aioffice_binding_runtime;
            GRANT UPDATE ON OBJECT::aioffice.DataSourceRegistrationAudits (RequestHash) TO aioffice_runtime;""")
        runtime_statement("UPDATE aioffice.DataSourceRegistrationAudits SET RequestHash=REPLICATE(N'0',64)", expected="ALLOWED")
        denied_status, denied_headers, denied_body = http(registration_path, fresh_registration, headers={"Origin": web})
        assert denied_status == 403 and "no-store" in denied_headers.get("Cache-Control", "")
        assert "secretref://" not in json.dumps(denied_body)
        assert sql(f"USE AIOfficeLocal; SELECT COUNT(*) FROM aioffice.DataSources WHERE {source_scope} AND LogicalName=N'{fresh_registration['logicalName']}';") == "0"
    finally:
        sql("""USE AIOfficeLocal;
            REVOKE UPDATE ON OBJECT::aioffice.DataSourceRegistrationAudits (RequestHash) FROM aioffice_runtime;
            DENY UPDATE ON OBJECT::aioffice.DataSourceRegistrationAudits (RequestHash) TO aioffice_binding_runtime;""")
    assert fingerprint("DataSourceRegistrationAudits", source_scope, "Id") == audit_before

    # A real SQL failure after source INSERT must roll both rows back. This
    # operator-only constraint targets one owned operation, then is removed.
    failed_operation = str(uuid.uuid4())
    failed_registration = {**registration, "operationId": failed_operation, "logicalName": "rollback-erp-" + uuid.uuid4().hex}
    sql(f"""USE AIOfficeLocal; ALTER TABLE aioffice.DataSourceRegistrationAudits
        ADD CONSTRAINT CK_CiRegistrationRollback CHECK (OperationId<>'{failed_operation}');""")
    try:
        assert http(registration_path, failed_registration, headers={"Origin": web})[0] == 409
        assert sql(f"USE AIOfficeLocal; SELECT COUNT(*) FROM aioffice.DataSources WHERE {source_scope} AND LogicalName=N'{failed_registration['logicalName']}';") == "0"
        assert sql(f"USE AIOfficeLocal; SELECT COUNT(*) FROM aioffice.DataSourceRegistrationAudits WHERE {source_scope} AND OperationId='{failed_operation}';") == "0"
    finally:
        sql("USE AIOfficeLocal; ALTER TABLE aioffice.DataSourceRegistrationAudits DROP CONSTRAINT CK_CiRegistrationRollback;")
    assert fingerprint("DataSourceRegistrationAudits", source_scope, "Id") == audit_before
    print("PASS actual SQL/BFF read-only registration, idempotency, scope, immutable audit and atomic failure rollback")

    # The fallback executor performs real read-only metadata collection without fabricating an AI answer.
    status, _, accepted = http("/api/local/tasks" + selector,
        {"dataSourceId": source, "question": "Inspect the local sample database metadata"}, headers={"Origin": web})
    assert status == 202, f"Worker task submission status {status}"
    task = accepted["taskId"]
    def completed():
        status, _, snapshot = http(f"/api/local/tasks/{task}{selector}")
        # System.Text.Json's default numeric enum: TaskExecutionStatus.Completed == 6.
        return status == 200 and snapshot.get("taskStatus") in (6, "Completed") and bool(snapshot.get("resultPayloadJson"))
    wait_for(completed)
    print("PASS real RabbitMQ worker execution and persisted result")

    # Pause the actual consumer before admission, revoke authority after the
    # message is durably published, then restart it. No sleeps define the race.
    for revocation in ("grant", "membership"):
        run("stop", "agent-worker")
        idempotency = "queued-revocation-" + uuid.uuid4().hex
        status, _, queued = http("/api/tasks", {"dataSourceId": source, "question": "Queued revocation fixture"},
            base=api, headers={**auth, "Idempotency-Key": idempotency})
        assert status == 202
        queued_id = str(uuid.UUID(queued["taskId"]))
        task_scope = source_scope + f" AND TaskId='{queued_id}'"
        wait_for(lambda: sql(f"""USE AIOfficeLocal; SELECT COUNT(*) FROM aioffice.TaskDispatches
            WHERE {task_scope} AND State=N'Published';""") == "1")
        try:
            if revocation == "grant":
                sql(f"""USE AIOfficeLocal; UPDATE aioffice.DataSourceSecretBindings SET IsEnabled=0,Version=Version+1
                    WHERE {source_scope} AND CanonicalReference=N'secretref://env/PILOT_ERP_CONNECTION' COLLATE Latin1_General_100_BIN2;""")
                connection_denied(source)
            else:
                sql(f"USE AIOfficeLocal; UPDATE aioffice.CompanyMemberships SET IsActive=0 WHERE {role_scope};")
            assert http("/api/tasks", {"dataSourceId": source, "question": "Queued revocation fixture"},
                base=api, headers={**auth, "Idempotency-Key": idempotency})[0] == 403
            run("start", "agent-worker")
            wait_for(lambda: sql(f"""USE AIOfficeLocal; SELECT COUNT(*) FROM aioffice.Tasks
                WHERE {source_scope} AND Id='{queued_id}' AND Status=N'Failed';""") == "1")
            assert sql(f"""USE AIOfficeLocal; SELECT COUNT(*) FROM aioffice.TaskDispatches
                WHERE {task_scope} AND State=N'DeadLettered';""") == "1"
            assert sql(f"""USE AIOfficeLocal; SELECT COUNT(*) FROM aioffice.TaskStepExecutions
                WHERE {task_scope} AND LastFailureClass=N'Authorization';""") == "1"
            assert sql(f"USE AIOfficeLocal; SELECT COUNT(*) FROM aioffice.TaskCheckpoints WHERE {task_scope};") == "0"
        finally:
            # Explicit operator restore, never bootstrap replay or implicit upsert.
            if revocation == "grant":
                sql(f"""USE AIOfficeLocal; UPDATE aioffice.DataSourceSecretBindings SET IsEnabled=1,Version=Version+1
                    WHERE {source_scope} AND CanonicalReference=N'secretref://env/PILOT_ERP_CONNECTION' COLLATE Latin1_General_100_BIN2;""")
            else:
                sql(f"USE AIOfficeLocal; UPDATE aioffice.CompanyMemberships SET IsActive=1 WHERE {role_scope};")
            run("start", "agent-worker")
    print("PASS queued RabbitMQ grant/membership revocation, replay denial and durable authorization dead-letter without checkpoints")

    # Data changes and revoked access must survive a full stop/start and a repeated bootstrap.
    sql("USE AIOfficeSample; INSERT dbo.LocalSample VALUES (2, N'Retained data');")
    retained_grant_version = int(sql(f"""USE AIOfficeLocal; UPDATE aioffice.DataSourceSecretBindings
        SET IsEnabled=0,Version=Version+1 WHERE {source_scope}
          AND CanonicalReference=N'secretref://env/PILOT_ERP_CONNECTION' COLLATE Latin1_General_100_BIN2;
        SELECT Version FROM aioffice.DataSourceSecretBindings WHERE {source_scope}
          AND CanonicalReference=N'secretref://env/PILOT_ERP_CONNECTION' COLLATE Latin1_General_100_BIN2;"""))
    sql(f"USE AIOfficeLocal; UPDATE aioffice.Users SET IsActive=0 WHERE TenantId='{tenant}' AND Id='{user}';")
    assert http("/api/auth/context", base=api, headers=auth)[0] == 403
    login(expected=403)
    # A deliberate owned-client disablement must survive bootstrap too; it is
    # restored explicitly only in this disposable fixture after qualification.
    disabled_client_id, disabled_browser_client = browser_client_contract()
    assert disabled_client_id == browser_client_id
    disabled_browser_client["enabled"] = False
    identity_admin("clients/" + browser_client_id, disabled_browser_client)
    run("down", "--remove-orphans")  # deliberately keep every named volume
    # Configuration generation must preserve identifiers and credentials too.
    generated = subprocess.run(["pwsh", "-NoProfile", "-File", "infra/initialize-local-config.ps1",
        "-DataDirectory", str(directory)], capture_output=True, text=True, timeout=30)
    assert generated.returncode == 0, "Repeat configuration generation failed"
    assert hashlib.sha256(manifest_file.read_bytes()).hexdigest() == original_manifest
    run("up", "-d", timeout=600)
    ready()
    retained_client_id, retained_browser_client = browser_client_contract(expected_enabled=False)
    assert retained_client_id == browser_client_id
    retained_browser_client["enabled"] = True
    identity_admin("clients/" + browser_client_id, retained_browser_client)
    assert browser_client_contract()[0] == browser_client_id
    assert sql("USE AIOfficeSample; SELECT COUNT(*) FROM dbo.LocalSample WHERE Id=2;") == "1"
    assert http("/api/auth/context", base=api, headers=auth)[0] == 403
    login(expected=403)
    assert sql(f"USE AIOfficeLocal; SELECT COUNT(*) FROM aioffice.Users WHERE TenantId='{tenant}' AND Id='{user}';") == "1"
    assert sql(f"USE AIOfficeLocal; SELECT COUNT(*) FROM aioffice.DataSources WHERE Id='{source}';") == "1"
    assert sql(f"""USE AIOfficeLocal; SELECT COUNT(*) FROM aioffice.DataSourceSecretBindings WHERE {source_scope}
        AND CanonicalReference=N'secretref://env/PILOT_ERP_CONNECTION' COLLATE Latin1_General_100_BIN2
        AND IsEnabled=0 AND Version={retained_grant_version};""") == "1", "Repeat bootstrap recreated or reset a revoked grant"
    sql(f"USE AIOfficeLocal; UPDATE aioffice.Users SET IsActive=1 WHERE TenantId='{tenant}' AND Id='{user}';")
    login()
    connection_denied(source)
    sql(f"""USE AIOfficeLocal; UPDATE aioffice.DataSourceSecretBindings SET IsEnabled=1,Version=Version+1
        WHERE {source_scope} AND CanonicalReference=N'secretref://env/PILOT_ERP_CONNECTION' COLLATE Latin1_General_100_BIN2
          AND Version={retained_grant_version};""")
    assert completed()
    assert sql(f"USE AIOfficeLocal; SELECT COUNT(*) FROM aioffice.DataSources WHERE {source_scope} AND Id='{registered_source}';") == "1"
    assert fingerprint("DataSourceRegistrationAudits", source_scope, "Id") == audit_before
    print("PASS repeat configuration/bootstrap, retained SQL/identity/revoked grant, inactive-user denial and retained task")
    print("PASS retained browser client identity/disablement and explicit disposable-fixture restore")
    member_spec = importlib.util.spec_from_file_location("membership_access_proof", Path("scripts/smoke-membership-access.py"))
    member_proof = importlib.util.module_from_spec(member_spec)
    member_spec.loader.exec_module(member_proof)
    # The preceding restart proof explicitly signs in again. Use that issued
    # current identity for the new race, not the token captured before restart.
    member_auth = {**auth, "Authorization": "Bearer " + next(cookie.value for cookie in cookies if cookie.name == "aioffice_local_access_token")}
    member_proof.verify(directory=directory, manifest=manifest, compose=compose, environment=legacy_environment,
        http=http, sql=sql, runtime_statement=runtime_statement, identity_admin=identity_admin,
        identity=identity, api=api, web=web, auth=member_auth)
    erp_spec = importlib.util.spec_from_file_location("erp_read_credentials_proof", Path("scripts/smoke-erp-readonly.py"))
    erp_proof = importlib.util.module_from_spec(erp_spec)
    erp_spec.loader.exec_module(erp_proof)
    erp_proof.verify(directory=directory, manifest=manifest, http=http, sql=sql, run=run,
        wait_for=wait_for, api=api, web=web, auth=member_auth, diagnostic_sql=erp_permission_diagnostic_query)
    print("PASS complete local stack integration")


if __name__ == "__main__":
    main()
