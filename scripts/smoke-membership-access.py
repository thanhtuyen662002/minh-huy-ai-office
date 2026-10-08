"""Required real SQL/OIDC member access proof, restricted to the owned CI stack."""
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
    # This proof introduces users, permission negative controls and a SQL lock
    # barrier. Refuse every operator/customer configuration before any mutation.
    assert os.environ.get("CI") == "true" and os.environ.get("GITHUB_ACTIONS") == "true"
    assert os.environ.get("RUNNER_TEMP") and directory.resolve() == (Path(os.environ["RUNNER_TEMP"]) / "aioffice-local").resolve()
    tenant, company, owner = (str(uuid.UUID(manifest[f"AIOFFICE_{key}_ID"])) for key in ("TENANT", "COMPANY", "USER"))
    assert api == "http://127.0.0.1:8080" and web == "http://127.0.0.1:3000"
    target, race_company = str(uuid.uuid4()), str(uuid.uuid4())
    username = "member-proof-" + uuid.uuid4().hex
    identity_admin("users", {"username": username, "enabled": True, "emailVerified": True,
        "firstName": "Disposable", "lastName": "Member", "email": username + "@example.invalid",
        "credentials": [{"type": "password", "value": manifest["AIOFFICE_OWNER_PASSWORD"], "temporary": False}]}, method="POST")
    identities = identity_admin("users?username=" + username + "&exact=true")
    assert len(identities) == 1 and identities[0]["username"] == username
    subject = str(uuid.UUID(identities[0]["id"]))
    form = urllib.parse.urlencode({"client_id": "aioffice-local", "grant_type": "password",
        "username": username, "password": manifest["AIOFFICE_OWNER_PASSWORD"]}).encode()
    with urllib.request.urlopen(urllib.request.Request(identity + "/realms/aioffice-local/protocol/openid-connect/token",
        data=form, headers={"Content-Type": "application/x-www-form-urlencoded"}), timeout=15) as response:
        second_token = json.loads(response.read(65537))["access_token"]
    # Keep audit FK targets and history until the owned stack volume is removed.
    # No deletion of existing users, memberships or audit records is a cleanup.
    sql(f"""USE AIOfficeLocal; SET XACT_ABORT ON; BEGIN TRANSACTION;
        INSERT aioffice.Users(TenantId,Id,IdentityProvider,Subject,DisplayName)
            VALUES('{tenant}','{target}',N'local-keycloak',N'{subject}',N'Disposable member SQL proof');
        INSERT aioffice.CompanyMemberships(TenantId,CompanyId,UserId) VALUES('{tenant}','{company}','{target}');
        INSERT aioffice.RoleAssignments(TenantId,CompanyId,UserId,RoleKey) VALUES('{tenant}','{company}','{target}',N'viewer');
        INSERT aioffice.Companies(TenantId,Id,Code,Name) VALUES('{tenant}','{race_company}',N'{race_company}',N'Disposable member race proof');
        INSERT aioffice.CompanyMemberships(TenantId,CompanyId,UserId) VALUES
            ('{tenant}','{race_company}','{owner}'),('{tenant}','{race_company}','{target}');
        INSERT aioffice.RoleAssignments(TenantId,CompanyId,UserId,RoleKey) VALUES
            ('{tenant}','{race_company}','{owner}',N'admin'),('{tenant}','{race_company}','{target}',N'admin');
        COMMIT TRANSACTION;""")
    scope = f"TenantId='{tenant}' AND CompanyId='{company}'"
    member_scope = scope + f" AND UserId='{target}'"
    audit_scope = scope + f" AND TargetUserId='{target}'"
    path = f"/api/local/company/members/{target}/access?companyId={company}"
    def operation(version, active):
        return {"operationId": str(uuid.uuid4()), "expectedVersion": str(version), "isActive": active}
    def change(body, expected=200):
        status, headers, result = http(path, body, headers={"Origin": web})
        assert status == expected, f"Membership operation expected {expected}, got {status}"
        assert "no-store" in headers.get("Cache-Control", "") and not headers.get_all("Set-Cookie")
        if expected == 200:
            assert set(result) == {"companyId", "userId", "membershipActive", "membershipVersion", "operationId"}
            assert result["companyId"] == company and result["userId"] == target and result["operationId"] == body["operationId"]
            assert result["membershipActive"] is body["isActive"]
        return result
    def state():
        return sql(f"USE AIOfficeLocal; SELECT CONCAT(CONVERT(int,IsActive),N':',Version) FROM aioffice.CompanyMemberships WHERE {member_scope};")
    def fingerprint(table, predicate, order):
        value = sql(f"""USE AIOfficeLocal; SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',
            (SELECT * FROM aioffice.{table} WHERE {predicate} ORDER BY {order} FOR JSON PATH,INCLUDE_NULL_VALUES)),2);""")
        assert len(value) == 64
        return value
    preserved = [("Users", f"TenantId='{tenant}' AND Id IN ('{owner}','{target}')", "Id"),
                 ("RoleAssignments", f"{scope} AND UserId IN ('{owner}','{target}')", "UserId,RoleKey"),
                 ("Tasks", scope, "Id")]
    before = [fingerprint(*item) for item in preserved]
    second_main_auth = {"Authorization": "Bearer " + second_token, "X-AIOffice-Company-Id": company}
    assert http("/api/auth/context", base=api, headers=second_main_auth)[0] == 200
    first = operation(1, False)
    first_result = change(first)
    assert first_result["membershipVersion"] == "2" and state() == "0:2"
    assert http("/api/auth/context", base=api, headers=second_main_auth)[0] == 403
    assert change(first) == first_result
    change(operation(1, True), 409)
    change({**first, "isActive": True}, 409)
    assert change(operation(2, True))["membershipVersion"] == "3"
    assert http("/api/auth/context", base=api, headers=second_main_auth)[0] == 200
    assert change(first) == first_result and state() == "1:3", "Replay reapplied a historical operation"
    assert sql(f"USE AIOfficeLocal; SELECT COUNT(*) FROM aioffice.CompanyMembershipAccessAudits WHERE {audit_scope};") == "2"

    def parallel(bodies, requests=None):
        barrier = threading.Barrier(2)
        def invoke(index):
            barrier.wait(timeout=5)
            if requests is None:
                return http(path, bodies[index], headers={"Origin": web})
            request_path, request_headers = requests[index]
            return http(request_path, bodies[index], base=api, headers=request_headers)
        with ThreadPoolExecutor(max_workers=2) as executor:
            futures = [executor.submit(invoke, index) for index in range(2)]
            return [future.result(timeout=20) for future in futures]
    same = operation(3, False)
    replies = parallel([same, same])
    assert [reply[0] for reply in replies] == [200, 200] and replies[0][2] == replies[1][2]
    assert state() == "0:4" and sql(f"USE AIOfficeLocal; SELECT COUNT(*) FROM aioffice.CompanyMembershipAccessAudits WHERE {audit_scope};") == "3"
    change(operation(4, True))
    replies = parallel([operation(5, False), operation(5, False)])
    assert sorted(reply[0] for reply in replies) == [200, 409] and state() == "0:6"
    change(operation(6, True))
    print("PASS real SQL/BFF membership versions, historical replay, concurrent duplicate and stale operations")

    audit_before = fingerprint("CompanyMembershipAccessAudits", audit_scope, "Id")
    for statement in ("UPDATE aioffice.CompanyMembershipAccessAudits SET RequestHash=REPLICATE(N'0',64)",
                      "DELETE FROM aioffice.CompanyMembershipAccessAudits", "TRUNCATE TABLE aioffice.CompanyMembershipAccessAudits",
                      "ALTER TABLE aioffice.CompanyMembershipAccessAudits ADD Forbidden int NULL",
                      "ALTER AUTHORIZATION ON OBJECT::aioffice.CompanyMembershipAccessAudits TO aioffice_runtime"):
        runtime_statement(statement)
    def unchanged():
        assert state() == "1:7" and fingerprint("CompanyMembershipAccessAudits", audit_scope, "Id") == audit_before
    unchanged()
    # A column GRANT bypasses a table DENY on SQL Server. Prove it actually
    # allows mutation in a rolled-back negative control, then require API denial.
    try:
        sql("""USE AIOfficeLocal;
            REVOKE UPDATE ON OBJECT::aioffice.CompanyMembershipAccessAudits (RequestHash) FROM aioffice_binding_runtime;
            GRANT UPDATE ON OBJECT::aioffice.CompanyMembershipAccessAudits (RequestHash) TO aioffice_runtime;""")
        runtime_statement("UPDATE aioffice.CompanyMembershipAccessAudits SET RequestHash=REPLICATE(N'0',64)", expected="ALLOWED")
        change(operation(7, False), 403)
        unchanged()
    finally:
        sql("""USE AIOfficeLocal;
            REVOKE UPDATE ON OBJECT::aioffice.CompanyMembershipAccessAudits (RequestHash) FROM aioffice_runtime;
            DENY UPDATE ON OBJECT::aioffice.CompanyMembershipAccessAudits (RequestHash) TO aioffice_binding_runtime;""")
    audit_verifier = Path("src/Platform.Persistence/CompanyMembershipAuditPermissionVerifier.cs").read_text(encoding="utf-8")
    direct_sql = audit_verifier.split('internal const string VerificationSql = """', 1)[1].split('""";', 1)[0]
    def direct_append_only():
        assert sql("USE AIOfficeLocal; EXECUTE AS LOGIN=N'aioffice_runtime'; " + direct_sql + " REVERT;") == "1"
    # Deliberately introduce each indirect bypass after Core startup. The old
    # direct-table-only verifier would pass even though audit rewrites are real.
    module = "aioffice.MemberAuditModule_" + uuid.uuid4().hex
    definition = f"CREATE PROCEDURE {module} WITH EXECUTE AS OWNER AS UPDATE aioffice.CompanyMembershipAccessAudits SET RequestHash=REPLICATE(N'0',64) WHERE {audit_scope};"
    try:
        sql("USE AIOfficeLocal; EXEC(N'" + definition.replace("'", "''") + "'); " + f"GRANT EXECUTE ON OBJECT::{module} TO aioffice_runtime;")
        direct_append_only()
        runtime_statement("EXEC " + module, expected="ALLOWED")
        change(operation(7, False), 403)
        unchanged()
    finally:
        sql(f"USE AIOfficeLocal; DROP PROCEDURE IF EXISTS {module};")
    trigger = "aioffice.MemberAuditTrigger_" + uuid.uuid4().hex
    definition = f"CREATE TRIGGER {trigger} ON aioffice.CompanyMemberships WITH EXECUTE AS OWNER AFTER UPDATE AS BEGIN SET NOCOUNT ON; UPDATE aioffice.CompanyMembershipAccessAudits SET RequestHash=REPLICATE(N'0',64) WHERE {audit_scope}; END;"
    try:
        sql("USE AIOfficeLocal; EXEC(N'" + definition.replace("'", "''") + "');")
        direct_append_only()
        runtime_statement(f"UPDATE aioffice.CompanyMemberships SET IsActive=IsActive WHERE {member_scope}", expected="ALLOWED")
        change(operation(7, False), 403)
        unchanged()
    finally:
        sql(f"USE AIOfficeLocal; DROP TRIGGER IF EXISTS {trigger};")
    try:
        sql("USE AIOfficeLocal; GRANT IMPERSONATE ON USER::aioffice_binding_operator_owner TO aioffice_runtime;")
        direct_append_only()
        runtime_statement("EXECUTE AS USER=N'aioffice_binding_operator_owner'; "
            f"UPDATE aioffice.CompanyMembershipAccessAudits SET RequestHash=REPLICATE(N'0',64) WHERE {audit_scope}; REVERT;", expected="ALLOWED")
        change(operation(7, False), 403)
        unchanged()
    finally:
        sql("USE AIOfficeLocal; REVOKE IMPERSONATE ON USER::aioffice_binding_operator_owner FROM aioffice_runtime;")
    failed = operation(7, False)
    constraint = "CK_MemberRollback_" + uuid.uuid4().hex
    try:
        sql(f"USE AIOfficeLocal; ALTER TABLE aioffice.CompanyMembershipAccessAudits ADD CONSTRAINT {constraint} CHECK(OperationId<>'{failed['operationId']}');")
        change(failed, 409)
        unchanged()
    finally:
        sql(f"USE AIOfficeLocal; ALTER TABLE aioffice.CompanyMembershipAccessAudits DROP CONSTRAINT {constraint};")
    print("PASS real SQL immutable member audit, column/owner-module/trigger/impersonation controls and atomic rollback")

    # Hold the shipping application lock until both real requests are visible
    # waiting on SQL Server. A thread start/sleep alone cannot establish a race.
    gate = "dbo.MemberAccessGate_" + uuid.uuid4().hex
    resource = f"aioffice:membership:{tenant}:{race_company}"
    sql(f"USE AIOfficeLocal; CREATE TABLE {gate}(Released bit NOT NULL); INSERT {gate} VALUES(0);")
    lock_query = f"""SET NOCOUNT ON; USE AIOfficeLocal; SET XACT_ABORT ON; BEGIN TRANSACTION;
        DECLARE @result int; EXEC @result=sys.sp_getapplock @Resource=N'{resource}',@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=5000;
        IF @result<0 THROW 51031,'CI lock barrier failed.',1;
        DECLARE @deadline datetime2=DATEADD(second,25,SYSUTCDATETIME());
        WHILE (SELECT Released FROM {gate} WITH(READUNCOMMITTED))=0 AND SYSUTCDATETIME()<@deadline
          WAITFOR DELAY '00:00:00.100';
        COMMIT TRANSACTION;"""
    locker = subprocess.Popen([*compose, "exec", "-T", "sql", "sh", "-c",
        'SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -I -b -m 1 -h -1 -W -Q "$1"',
        "sql", lock_query], stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, env=environment)
    def locks(status, login):
        return int(sql(f"""SELECT COUNT(*) FROM sys.dm_tran_locks l JOIN sys.dm_exec_sessions s ON l.request_session_id=s.session_id
            WHERE l.resource_database_id=DB_ID(N'AIOfficeLocal') AND l.resource_type=N'APPLICATION'
              AND l.request_status=N'{status}' AND l.resource_description LIKE N'%aioffice:membership:%'
              AND s.login_name=N'{login}';"""))
    def await_sql(predicate, seconds):
        deadline = time.monotonic() + seconds
        while time.monotonic() < deadline:
            if predicate():
                return
            time.sleep(.1)
        raise AssertionError("Real SQL membership barrier was not reached")
    executor = None
    try:
        await_sql(lambda: locks("GRANT", "sa") == 1, 8)
        owner_auth = {**auth, "X-AIOffice-Company-Id": race_company}
        second_auth = {"Authorization": "Bearer " + second_token, "X-AIOffice-Company-Id": race_company}
        assert http("/api/auth/context", base=api, headers=second_auth)[0] == 200
        executor = ThreadPoolExecutor(max_workers=1)
        race = executor.submit(parallel, [operation(1, False), operation(1, False)],
            [(f"/api/company/members/{target}/access", owner_auth), (f"/api/company/members/{owner}/access", second_auth)])
        await_sql(lambda: locks("WAIT", "aioffice_runtime") == 2, 4)
        sql(f"USE AIOfficeLocal; UPDATE {gate} SET Released=1;")
        replies = race.result(timeout=20)
        assert sorted(reply[0] for reply in replies) == [200, 403], "Opposite admin race must deny the revoked actor"
        race_scope = f"TenantId='{tenant}' AND CompanyId='{race_company}'"
        assert sql(f"USE AIOfficeLocal; SELECT COUNT(*) FROM aioffice.CompanyMemberships WHERE {race_scope} AND IsActive=1;") == "1"
        assert sql(f"USE AIOfficeLocal; SELECT COUNT(*) FROM aioffice.CompanyMembershipAccessAudits WHERE {race_scope};") == "1"
        losing_index = next(index for index, reply in enumerate(replies) if reply[0] == 403)
        assert http("/api/company/members", base=api, headers=[owner_auth, second_auth][losing_index])[0] == 403
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
    assert locker.returncode == 0, "SQL membership barrier did not complete cleanly"
    assert [fingerprint(*item) for item in preserved] == before, "Membership operations changed global identities, roles or tasks"
    print("PASS real SQL two-admin opposite-target race, fresh revoked-actor denial and unchanged identities/roles/tasks")
