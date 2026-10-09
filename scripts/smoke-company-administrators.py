"""Real administrator mutation proof; only the owned disposable GitHub CI stack."""
from concurrent.futures import ThreadPoolExecutor
import json
import os
from pathlib import Path
import subprocess
import threading
import time
import urllib.parse
import urllib.request
import uuid


def verify(*, directory, manifest, compose, environment, http, sql,
           runtime_statement, identity_admin, identity, api, web, auth):
    assert os.environ.get("CI") == "true" and os.environ.get("GITHUB_ACTIONS") == "true"
    assert os.environ.get("RUNNER_TEMP") and directory.resolve() == (Path(os.environ["RUNNER_TEMP"]) / "aioffice-local").resolve()
    assert api == "http://127.0.0.1:8080" and web == "http://127.0.0.1:3000"
    tenant, company, owner = (str(uuid.UUID(manifest[f"AIOFFICE_{key}_ID"])) for key in ("TENANT", "COMPANY", "USER"))
    target, race_company, foreign_member = (str(uuid.uuid4()) for _ in range(3))
    foreign_tenant, foreign_target = str(uuid.uuid4()), str(uuid.uuid4())
    username = "administrator-proof-" + uuid.uuid4().hex
    identity_admin("users", {"username": username, "enabled": True, "emailVerified": True,
        "firstName": "Disposable", "lastName": "Administrator", "email": username + "@example.invalid",
        "credentials": [{"type": "password", "value": manifest["AIOFFICE_OWNER_PASSWORD"], "temporary": False}]}, method="POST")
    identities = identity_admin("users?username=" + username + "&exact=true")
    assert len(identities) == 1 and identities[0]["username"] == username
    subject = str(uuid.UUID(identities[0]["id"]))
    form = urllib.parse.urlencode({"client_id": "aioffice-local", "grant_type": "password",
        "username": username, "password": manifest["AIOFFICE_OWNER_PASSWORD"]}).encode()
    with urllib.request.urlopen(urllib.request.Request(identity + "/realms/aioffice-local/protocol/openid-connect/token",
        data=form, headers={"Content-Type": "application/x-www-form-urlencoded"}), timeout=15) as response:
        second_token = json.loads(response.read(65537))["access_token"]
    # All new memberships and immutable receipts survive until CI removes its
    # disposable volume. Cleanup never erases an existing identity or audit.
    sql(f"""USE AIOfficeLocal; SET XACT_ABORT ON; BEGIN TRANSACTION;
        INSERT aioffice.Users(TenantId,Id,IdentityProvider,Subject,DisplayName) VALUES
          ('{tenant}','{target}',N'local-keycloak',N'{subject}',N'Disposable administrator SQL proof'),
          ('{tenant}','{foreign_member}',N'disposable-role-proof',N'{uuid.uuid4()}',N'Disposable foreign role proof');
        INSERT aioffice.CompanyMemberships(TenantId,CompanyId,UserId) VALUES('{tenant}','{company}','{target}');
        INSERT aioffice.RoleAssignments(TenantId,CompanyId,UserId,RoleKey) VALUES
          ('{tenant}','{company}','{target}',N'viewer'),('{tenant}','{company}','{target}',N'supplementary-😀');
        INSERT aioffice.Companies(TenantId,Id,Code,Name) VALUES('{tenant}','{race_company}',N'{race_company}',N'Disposable administrator race');
        INSERT aioffice.CompanyMemberships(TenantId,CompanyId,UserId) VALUES
          ('{tenant}','{race_company}','{owner}'),('{tenant}','{race_company}','{target}'),('{tenant}','{race_company}','{foreign_member}');
        INSERT aioffice.RoleAssignments(TenantId,CompanyId,UserId,RoleKey) VALUES
          ('{tenant}','{race_company}','{owner}',N'admin'),('{tenant}','{race_company}','{target}',N'admin');
        INSERT aioffice.Companies(TenantId,Id,Code,Name) VALUES('{foreign_tenant}','{company}',N'{company}',N'Disposable foreign tenant role proof');
        INSERT aioffice.Users(TenantId,Id,IdentityProvider,Subject,DisplayName)
          VALUES('{foreign_tenant}','{foreign_target}',N'disposable-role-proof',N'{uuid.uuid4()}',N'Disposable foreign tenant member');
        INSERT aioffice.CompanyMemberships(TenantId,CompanyId,UserId) VALUES('{foreign_tenant}','{company}','{foreign_target}');
        COMMIT TRANSACTION;""")
    scope = f"TenantId='{tenant}' AND CompanyId='{company}'"
    member_scope = scope + f" AND UserId='{target}'"
    audit_scope = scope + f" AND TargetUserId='{target}'"
    path = f"/api/local/company/members/{target}/administrator?companyId={company}"
    access_path = f"/api/local/company/members/{target}/access?companyId={company}"

    def operation(version, administrator):
        return {"operationId": str(uuid.uuid4()), "expectedVersion": str(version), "isAdministrator": administrator}

    def change(body, expected=200):
        status, headers, result = http(path, body, headers={"Origin": web})
        assert status == expected, f"Administrator operation expected {expected}, got {status}"
        assert "no-store" in headers.get("Cache-Control", "") and not headers.get_all("Set-Cookie")
        if expected == 200:
            assert set(result) == {"companyId", "userId", "isAdministrator", "membershipVersion", "operationId"}
            assert result["companyId"] == company and result["userId"] == target and result["operationId"] == body["operationId"]
            assert result["isAdministrator"] is body["isAdministrator"]
        return result

    def state():
        return sql(f"""USE AIOfficeLocal; SELECT CONCAT(CONVERT(int,IsActive),N':',Version,N':',
          (SELECT COUNT(*) FROM aioffice.RoleAssignments WHERE {member_scope} AND RoleKey COLLATE Latin1_General_100_BIN2=N'admin' AND DATALENGTH(RoleKey)=10))
          FROM aioffice.CompanyMemberships WHERE {member_scope};""")

    def version():
        return int(state().split(":")[1])

    def fingerprint(table, predicate, order, columns="*"):
        value = sql(f"""USE AIOfficeLocal; SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',
          (SELECT {columns} FROM aioffice.{table} WHERE {predicate} ORDER BY {order} FOR JSON PATH,INCLUDE_NULL_VALUES)),2);""")
        assert len(value) == 64
        return value

    preserved = [("Users", f"TenantId='{tenant}' AND Id IN ('{owner}','{target}')", "Id"),
        ("RoleAssignments", scope + f" AND (UserId='{owner}' OR (UserId='{target}' AND RoleKey COLLATE Latin1_General_100_BIN2<>N'admin'))", "UserId,RoleKey"),
        ("CompanyMemberships", member_scope, "UserId", "TenantId,CompanyId,UserId,IsActive,CreatedAtUtc"),
        ("Tasks", scope, "Id")]
    before = [fingerprint(*item) for item in preserved]
    second_auth = {"Authorization": "Bearer " + second_token, "X-AIOffice-Company-Id": company}

    def roles():
        status, _, result = http("/api/auth/context", base=api, headers=second_auth)
        assert status == 200
        return result["roles"]

    assert "admin" not in roles()
    grant = operation(1, True)
    granted = change(grant)
    assert granted["membershipVersion"] == "2" and state() == "1:2:1" and "admin" in roles()
    assert sql(f"""USE AIOfficeLocal; SELECT COUNT(*) FROM aioffice.CompanyAdministratorAudits WHERE {audit_scope}
      AND ActorUserId='{owner}' AND OperationId='{grant['operationId']}' AND BeforeAdministrator=0 AND AfterAdministrator=1
      AND BeforeVersion=1 AND AfterVersion=2 AND ISJSON(BeforeRolesJson)=1 AND ISJSON(AfterRolesJson)=1
      AND (SELECT COUNT(*) FROM OPENJSON(BeforeRolesJson))=2 AND (SELECT COUNT(*) FROM OPENJSON(AfterRolesJson))=3
      AND EXISTS(SELECT 1 FROM OPENJSON(AfterRolesJson) WHERE value COLLATE Latin1_General_100_BIN2=N'supplementary-😀');""") == "1"
    change(operation(1, False), 409)
    change({**grant, "isAdministrator": False}, 409)
    removed = change(operation(2, False))
    assert removed["membershipVersion"] == "3" and state() == "1:3:0" and "admin" not in roles()
    assert change(grant) == granted and state() == "1:3:0", "Historical role receipt reapplied a grant"
    assert http("/api/company/members", base=api, headers=second_auth)[0] == 403
    assert http("/api/company/members/" + target + "/administrator", grant, base=api, headers=second_auth)[0] == 403

    def parallel(requests):
        barrier = threading.Barrier(2)
        def invoke(request):
            request_path, body, request_headers, base = request
            barrier.wait(timeout=5)
            return http(request_path, body, headers=request_headers, base=base)
        with ThreadPoolExecutor(max_workers=2) as executor:
            futures = [executor.submit(invoke, request) for request in requests]
            return [future.result(timeout=20) for future in futures]

    same = operation(3, True)
    replies = parallel([(path, same, {"Origin": web}, web)] * 2)
    assert [reply[0] for reply in replies] == [200, 200] and replies[0][2] == replies[1][2] and state() == "1:4:1"
    noop = operation(4, True)
    assert change(noop)["membershipVersion"] == "4" and state() == "1:4:1"
    assert sql(f"USE AIOfficeLocal; SELECT COUNT(*) FROM aioffice.CompanyAdministratorAudits WHERE {audit_scope};") == "4"
    replies = parallel([(path, operation(4, False), {"Origin": web}, web) for _ in range(2)])
    assert sorted(reply[0] for reply in replies) == [200, 409] and state() == "1:5:0"
    for target_id, status in ((owner, 409), (foreign_member, 404), (foreign_target, 404), (str(uuid.uuid4()), 404)):
        assert http(f"/api/local/company/members/{target_id}/administrator?companyId={company}",
            operation(1, False), headers={"Origin": web})[0] == status
    assert change(operation(5, True))["membershipVersion"] == "6"
    status, _, _ = http(access_path, {"operationId": str(uuid.uuid4()), "expectedVersion": "6", "isActive": False}, headers={"Origin": web})
    assert status == 200 and state() == "0:7:1"
    assert change(operation(7, False), 409)["code"] == "inactive-membership"
    assert http(access_path, {"operationId": str(uuid.uuid4()), "expectedVersion": "7", "isActive": True}, headers={"Origin": web})[0] == 200
    assert change(operation(8, False))["membershipVersion"] == "9" and state() == "1:9:0"
    print("PASS real SQL administrator grant/remove/no-op/audit/version/historical replay, duplicate/stale, fresh authority and scoped/self/inactive denial")

    # A native SQL lock barrier proves both services use the same company lock,
    # and proves both opposite-admin requests wait before either can commit.
    def locked_parallel(lock_company, requests, after_wait=None):
        gate = "dbo.AdministratorGate_" + uuid.uuid4().hex
        resource = f"aioffice:membership:{tenant}:{lock_company}"
        sql(f"USE AIOfficeLocal; CREATE TABLE {gate}(Released bit NOT NULL); INSERT {gate} VALUES(0);")
        query = f"""SET NOCOUNT ON; USE AIOfficeLocal; SET XACT_ABORT ON; BEGIN TRANSACTION;
          DECLARE @result int; EXEC @result=sys.sp_getapplock @Resource=N'{resource}',@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=5000;
          IF @result<0 THROW 51031,'CI administrator barrier failed.',1;
          DECLARE @deadline datetime2=DATEADD(second,25,SYSUTCDATETIME());
          WHILE (SELECT Released FROM {gate} WITH(READUNCOMMITTED))=0 AND SYSUTCDATETIME()<@deadline WAITFOR DELAY '00:00:00.100';
          COMMIT TRANSACTION;"""
        locker = subprocess.Popen([*compose, "exec", "-T", "sql", "sh", "-c",
          'SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -I -b -m 1 -h -1 -W -Q "$1"',
          "sql", query], stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, env=environment)
        def locks(status, login):
            return int(sql(f"""SELECT COUNT(*) FROM sys.dm_tran_locks l JOIN sys.dm_exec_sessions s ON l.request_session_id=s.session_id
              WHERE l.resource_database_id=DB_ID(N'AIOfficeLocal') AND l.resource_type=N'APPLICATION'
              AND l.request_status=N'{status}' AND l.resource_description LIKE N'%aioffice:membership:%' AND s.login_name=N'{login}';"""))
        def await_sql(predicate, seconds):
            deadline = time.monotonic() + seconds
            while time.monotonic() < deadline:
                if predicate():
                    return
                time.sleep(.1)
            raise AssertionError("Real SQL administrator barrier was not reached")
        executor = None
        try:
            await_sql(lambda: locks("GRANT", "sa") == 1, 8)
            executor = ThreadPoolExecutor(max_workers=1)
            pending = executor.submit(parallel, requests)
            await_sql(lambda: locks("WAIT", "aioffice_runtime") == 2, 4)
            if after_wait is not None:
                after_wait()
            sql(f"USE AIOfficeLocal; UPDATE {gate} SET Released=1;")
            replies = pending.result(timeout=20)
        finally:
            sql(f"USE AIOfficeLocal; UPDATE {gate} SET Released=1;")
            try:
                locker.communicate(timeout=10)
            except subprocess.TimeoutExpired:
                locker.kill()
                locker.communicate(timeout=5)
            if executor is not None:
                executor.shutdown(wait=True, cancel_futures=True)
            sql(f"USE AIOfficeLocal; DROP TABLE {gate};")
        assert locker.returncode == 0
        return replies

    access = {"operationId": str(uuid.uuid4()), "expectedVersion": "9", "isActive": False}
    replies = locked_parallel(company, [(path, operation(9, True), {"Origin": web}, web), (access_path, access, {"Origin": web}, web)])
    assert sorted(reply[0] for reply in replies) == [200, 409] and version() == 10
    if state().startswith("0:"):
        assert state() == "0:10:0"
        assert http(access_path, {**access, "operationId": str(uuid.uuid4()), "expectedVersion": "10", "isActive": True}, headers={"Origin": web})[0] == 200
    else:
        assert state() == "1:10:1"
        change(operation(10, False))
    assert state() == "1:11:0"
    revocation_before = fingerprint("CompanyAdministratorAudits", audit_scope, "Id")
    owner_scope = scope + f" AND UserId='{owner}' AND RoleKey COLLATE Latin1_General_100_BIN2=N'admin' AND DATALENGTH(RoleKey)=10"
    backup = "tempdb.dbo.AdministratorRoleBackup_" + uuid.uuid4().hex
    sql(f"USE AIOfficeLocal; SELECT * INTO {backup} FROM aioffice.RoleAssignments WHERE {owner_scope};")
    try:
        replies = locked_parallel(company, [(path, operation(11, True), {"Origin": web}, web),
            (access_path, {**access, "operationId": str(uuid.uuid4()), "expectedVersion": "11"}, {"Origin": web}, web)],
            after_wait=lambda: sql(f"USE AIOfficeLocal; DELETE aioffice.RoleAssignments WHERE {owner_scope};"))
        assert [reply[0] for reply in replies] == [403, 403] and state() == "1:11:0"
        assert fingerprint("CompanyAdministratorAudits", audit_scope, "Id") == revocation_before
    finally:
        sql(f"""USE AIOfficeLocal; BEGIN TRANSACTION;
          INSERT aioffice.RoleAssignments(TenantId,CompanyId,UserId,RoleKey,CreatedAtUtc)
            SELECT b.TenantId,b.CompanyId,b.UserId,b.RoleKey,b.CreatedAtUtc FROM {backup} b
            WHERE NOT EXISTS(SELECT 1 FROM aioffice.RoleAssignments r WHERE r.TenantId=b.TenantId AND r.CompanyId=b.CompanyId
              AND r.UserId=b.UserId AND r.RoleKey=b.RoleKey); DROP TABLE {backup}; COMMIT TRANSACTION;""")
    owner_race_auth = {**auth, "X-AIOffice-Company-Id": race_company}
    target_race_auth = {**second_auth, "X-AIOffice-Company-Id": race_company}
    replies = locked_parallel(race_company, [
      (f"/api/company/members/{target}/administrator", operation(1, False), owner_race_auth, api),
      (f"/api/company/members/{owner}/administrator", operation(1, False), target_race_auth, api)])
    assert sorted(reply[0] for reply in replies) == [200, 403]
    race_scope = f"TenantId='{tenant}' AND CompanyId='{race_company}'"
    assert sql(f"USE AIOfficeLocal; SELECT COUNT(*) FROM aioffice.RoleAssignments WHERE {race_scope} AND RoleKey COLLATE Latin1_General_100_BIN2=N'admin';") == "1"
    assert sql(f"USE AIOfficeLocal; SELECT COUNT(*) FROM aioffice.CompanyMemberships WHERE {race_scope} AND IsActive=1;") == "3"
    assert sql(f"USE AIOfficeLocal; SELECT COUNT(*) FROM aioffice.CompanyAdministratorAudits WHERE {race_scope};") == "1"
    losing = next(index for index, reply in enumerate(replies) if reply[0] == 403)
    assert http("/api/company/members", base=api, headers=[owner_race_auth, target_race_auth][losing])[0] == 403
    surviving = [owner_race_auth, target_race_auth][1-losing]
    surviving_user = [owner, target][1-losing]
    # The sole remaining administrator cannot remove its own last role.
    assert http(f"/api/company/members/{surviving_user}/administrator", operation(1, False), base=api, headers=surviving)[0] == 409
    print("PASS real SQL shared access/role lock and version race, queued external actor revocation, opposite administrator race, fresh losing-actor/sole-admin denial")

    audit_before = fingerprint("CompanyAdministratorAudits", audit_scope, "Id")
    role_before = fingerprint("RoleAssignments", member_scope, "RoleKey")
    member_before = fingerprint("CompanyMemberships", member_scope, "UserId")
    def unchanged():
        assert fingerprint("CompanyAdministratorAudits", audit_scope, "Id") == audit_before
        assert fingerprint("RoleAssignments", member_scope, "RoleKey") == role_before
        assert fingerprint("CompanyMemberships", member_scope, "UserId") == member_before

    for statement in ("UPDATE aioffice.CompanyAdministratorAudits SET RequestHash=REPLICATE(N'0',64)",
        "DELETE FROM aioffice.CompanyAdministratorAudits", "TRUNCATE TABLE aioffice.CompanyAdministratorAudits",
        "ALTER TABLE aioffice.CompanyAdministratorAudits ADD Forbidden int NULL",
        "ALTER AUTHORIZATION ON OBJECT::aioffice.CompanyAdministratorAudits TO aioffice_runtime"):
        runtime_statement(statement)
    try:
        sql("""USE AIOfficeLocal; REVOKE UPDATE ON OBJECT::aioffice.CompanyAdministratorAudits (RequestHash) FROM aioffice_binding_runtime;
          GRANT UPDATE ON OBJECT::aioffice.CompanyAdministratorAudits (RequestHash) TO aioffice_runtime;""")
        runtime_statement("UPDATE aioffice.CompanyAdministratorAudits SET RequestHash=REPLICATE(N'0',64)", expected="ALLOWED")
        change(operation(11, True), 403)
        unchanged()
    finally:
        sql("""USE AIOfficeLocal; REVOKE UPDATE ON OBJECT::aioffice.CompanyAdministratorAudits (RequestHash) FROM aioffice_runtime;
          DENY UPDATE ON OBJECT::aioffice.CompanyAdministratorAudits (RequestHash) TO aioffice_binding_runtime;""")
    verifier = Path("src/Platform.Persistence/CompanyAdministratorAuditPermissionVerifier.cs").read_text(encoding="utf-8")
    direct_sql = verifier.split('internal const string VerificationSql = """', 1)[1].split('""";', 1)[0]
    def direct_append_only():
        assert sql("USE AIOfficeLocal; EXECUTE AS LOGIN=N'aioffice_runtime'; " + direct_sql + " REVERT;") == "1"
    module = "aioffice.AdministratorAuditModule_" + uuid.uuid4().hex
    definition = f"CREATE PROCEDURE {module} WITH EXECUTE AS OWNER AS UPDATE aioffice.CompanyAdministratorAudits SET RequestHash=REPLICATE(N'0',64) WHERE {audit_scope};"
    try:
        sql("USE AIOfficeLocal; EXEC(N'" + definition.replace("'", "''") + "'); " + f"GRANT EXECUTE ON OBJECT::{module} TO aioffice_runtime;")
        direct_append_only()
        runtime_statement("EXEC " + module, expected="ALLOWED")
        change(operation(11, True), 403)
        unchanged()
    finally:
        sql(f"USE AIOfficeLocal; DROP PROCEDURE IF EXISTS {module};")
    trigger = "aioffice.AdministratorAuditTrigger_" + uuid.uuid4().hex
    definition = f"CREATE TRIGGER {trigger} ON aioffice.RoleAssignments WITH EXECUTE AS OWNER AFTER INSERT,DELETE AS BEGIN SET NOCOUNT ON; UPDATE aioffice.CompanyAdministratorAudits SET RequestHash=REPLICATE(N'0',64) WHERE {audit_scope}; END;"
    try:
        sql("USE AIOfficeLocal; EXEC(N'" + definition.replace("'", "''") + "');")
        direct_append_only()
        runtime_statement(f"INSERT aioffice.RoleAssignments(TenantId,CompanyId,UserId,RoleKey) VALUES('{tenant}','{company}','{target}',N'ci-negative-trigger')", expected="ALLOWED")
        change(operation(11, True), 403)
        unchanged()
    finally:
        sql(f"USE AIOfficeLocal; DROP TRIGGER IF EXISTS {trigger};")
    try:
        sql("USE AIOfficeLocal; GRANT IMPERSONATE ON USER::aioffice_binding_operator_owner TO aioffice_runtime;")
        direct_append_only()
        runtime_statement("EXECUTE AS USER=N'aioffice_binding_operator_owner'; "
            f"UPDATE aioffice.CompanyAdministratorAudits SET RequestHash=REPLICATE(N'0',64) WHERE {audit_scope}; REVERT;", expected="ALLOWED")
        change(operation(11, True), 403)
        unchanged()
    finally:
        sql("USE AIOfficeLocal; REVOKE IMPERSONATE ON USER::aioffice_binding_operator_owner FROM aioffice_runtime;")
    failed = operation(11, True)
    constraint = "CK_AdministratorRollback_" + uuid.uuid4().hex
    try:
        sql(f"USE AIOfficeLocal; ALTER TABLE aioffice.CompanyAdministratorAudits ADD CONSTRAINT {constraint} CHECK(OperationId<>'{failed['operationId']}');")
        change(failed, 409)
        unchanged()
    finally:
        sql(f"USE AIOfficeLocal; ALTER TABLE aioffice.CompanyAdministratorAudits DROP CONSTRAINT {constraint};")
    # The exact same request succeeds after the external rejection is removed.
    assert change(failed)["membershipVersion"] == "12"
    assert change(operation(12, False))["membershipVersion"] == "13"
    print("PASS real SQL immutable administrator audit, effective column/module/role-trigger/impersonation refusal and atomic restored-positive rollback")

    inactive_audit = fingerprint("CompanyAdministratorAudits", audit_scope, "Id")
    try:
        sql(f"USE AIOfficeLocal; UPDATE aioffice.Users SET IsActive=0 WHERE TenantId='{tenant}' AND Id='{target}';")
        assert change(operation(13, True), 409)["code"] == "inactive-user" and state() == "1:13:0"
        assert fingerprint("CompanyAdministratorAudits", audit_scope, "Id") == inactive_audit
    finally:
        sql(f"USE AIOfficeLocal; UPDATE aioffice.Users SET IsActive=1 WHERE TenantId='{tenant}' AND Id='{target}';")
    for raw in ("CONVERT(nvarchar(100),0x00D8)", "CONVERT(nvarchar(100),0x00DC)", "N'bad'+NCHAR(1)", "N'ADMIN'", "N'admin '"):
        sql(f"USE AIOfficeLocal; INSERT aioffice.RoleAssignments(TenantId,CompanyId,UserId,RoleKey) VALUES('{tenant}','{company}','{target}',{raw});")
        try:
            before_role = fingerprint("RoleAssignments", member_scope, "RoleKey")
            before_audit = fingerprint("CompanyAdministratorAudits", audit_scope, "Id")
            assert change(operation(13, True), 409)["code"] == "invalid-role-state"
            assert state() == "1:13:0" and fingerprint("RoleAssignments", member_scope, "RoleKey") == before_role
            assert fingerprint("CompanyAdministratorAudits", audit_scope, "Id") == before_audit
        finally:
            # Match stored bytes, never a collation alias of the valid viewer.
            sql(f"USE AIOfficeLocal; DELETE aioffice.RoleAssignments WHERE {member_scope} AND CONVERT(varbinary(200),RoleKey)=CONVERT(varbinary(200),{raw});")
    assert change(operation(13, True))["membershipVersion"] == "14"
    assert change(operation(14, False))["membershipVersion"] == "15"
    assert state() == "1:15:0" and [fingerprint(*item) for item in preserved] == before
    print("PASS real SQL stored-byte malformed role/alias denial, valid supplementary role preservation and unchanged identities/other roles/membership state/tasks")
