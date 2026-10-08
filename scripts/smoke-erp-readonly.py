"""Effective ERP read credential controls for the exact owned disposable CI stack."""
import json
import os
from pathlib import Path
import re
import subprocess
import uuid


def verify(*, directory, manifest, http, sql, run, wait_for, api, web, auth):
    assert os.environ.get("CI") == "true" and os.environ.get("GITHUB_ACTIONS") == "true"
    assert os.environ.get("RUNNER_TEMP") and directory.resolve() == (Path(os.environ["RUNNER_TEMP"]) / "aioffice-local").resolve()
    assert api == "http://127.0.0.1:8080" and web == "http://127.0.0.1:3000"
    tenant, company, source = (str(uuid.UUID(manifest[f"AIOFFICE_{key}_ID"])) for key in ("TENANT", "COMPANY", "DATA_SOURCE"))
    suffix = uuid.uuid4().hex
    table, module = "dbo.ReadCredential_" + suffix, "dbo.ReadModule_" + suffix
    inner, outer, impersonated = (prefix + suffix for prefix in ("ReadInner_", "ReadOuter_", "ReadUser_"))
    verifier = Path("src/Platform.Persistence/ErpReadOnlyConnectionVerifier.cs").read_text(encoding="utf-8")
    proof_sql = verifier.split('internal const string VerificationSql = """', 1)[1].split('""";', 1)[0]
    scope = f"TenantId='{tenant}' AND CompanyId='{company}'"

    def reader(query):
        return sql("USE AIOfficeSample; EXECUTE AS LOGIN=N'aioffice_reader'; " + query + "; REVERT;")

    def proof(expected):
        assert reader(proof_sql) == expected, "Effective ERP credential profile mismatch"

    def effect(statement, expected="DENIED"):
        # All successful adversarial DML/DDL is rolled back as the real reader
        # security context, including OWNER procedures and impersonation.
        result = reader(f"""BEGIN TRY BEGIN TRANSACTION; {statement};
            ROLLBACK TRANSACTION; SELECT N'ALLOWED'; END TRY
            BEGIN CATCH IF @@TRANCOUNT>0 ROLLBACK TRANSACTION;
              IF ERROR_NUMBER() IN (229,262,15151,15247,15517,15406,1088,4701)
                SELECT N'DENIED'; ELSE THROW;
            END CATCH""")
        assert result == expected, "Actual reader adversarial effect mismatch"
        assert sql(f"USE AIOfficeSample; SELECT COUNT(*) FROM {table} WHERE Id=1 AND Label=N'Original';") == "1"

    def connection(expected):
        for path, base, headers in (
            (f"/api/data-sources/{source}/connection-test", api, auth),
            (f"/api/local/data-sources/{source}/connection-test?companyId={company}", web, {"Origin": web})):
            status, response_headers, result = http(path, {}, base=base, headers=headers)
            assert status == 200 and "no-store" in response_headers.get("Cache-Control", "")
            assert result["succeeded"] is expected and result["code"] == ("success" if expected else "read_only_unqualified")
            assert "secretref" not in json.dumps(result).lower()
            assert all(value not in json.dumps(result) for key, value in manifest.items() if "PASSWORD" in key)

    def safe():
        proof("1")
        connection(True)

    sql(f"USE AIOfficeSample; CREATE TABLE {table}(Id int NOT NULL PRIMARY KEY, Label nvarchar(100) NOT NULL); INSERT {table} VALUES(1,N'Original');")
    try:
        safe()
        assert reader(f"SELECT COUNT(*) FROM {table} WHERE Id=1;") == "1"
        for statement in (f"UPDATE {table} SET Label=N'Mutated'", f"INSERT {table} VALUES(2,N'Mutated')",
                          f"DELETE FROM {table}", f"TRUNCATE TABLE {table}", f"ALTER TABLE {table} ADD Forbidden int NULL",
                          f"ALTER AUTHORIZATION ON OBJECT::{table} TO aioffice_reader"):
            effect(statement)
        print("PASS actual ERP safe credential profile, SELECT and persistent DML/DDL denial")

        for grant, revoke in (
            (f"GRANT UPDATE ON OBJECT::{table} TO aioffice_reader", f"REVOKE UPDATE ON OBJECT::{table} FROM aioffice_reader"),
            (f"DENY UPDATE ON OBJECT::{table} TO aioffice_reader; GRANT UPDATE ON OBJECT::{table}(Label) TO aioffice_reader",
             f"REVOKE UPDATE ON OBJECT::{table}(Label) FROM aioffice_reader; REVOKE UPDATE ON OBJECT::{table} FROM aioffice_reader"),
            ("ALTER ROLE db_datawriter ADD MEMBER aioffice_reader", "ALTER ROLE db_datawriter DROP MEMBER aioffice_reader"),
            ("ALTER ROLE db_owner ADD MEMBER aioffice_reader", "ALTER ROLE db_owner DROP MEMBER aioffice_reader")):
            try:
                sql("USE AIOfficeSample; " + grant + ";")
                effect(f"UPDATE {table} SET Label=N'Mutated'", "ALLOWED")
                proof("0")
                connection(False)
            finally:
                sql("USE AIOfficeSample; " + revoke + ";")
            safe()

        try:
            sql(f"""USE AIOfficeSample; CREATE ROLE {inner}; CREATE ROLE {outer};
                GRANT UPDATE ON OBJECT::{table} TO {inner}; ALTER ROLE {inner} ADD MEMBER {outer};
                ALTER ROLE {outer} ADD MEMBER aioffice_reader;""")
            effect(f"UPDATE {table} SET Label=N'Mutated'", "ALLOWED")
            proof("0")
            connection(False)
        finally:
            sql(f"""USE AIOfficeSample; ALTER ROLE {outer} DROP MEMBER aioffice_reader;
                ALTER ROLE {inner} DROP MEMBER {outer}; DROP ROLE {outer}; DROP ROLE {inner};""")
        safe()

        try:
            sql(f"USE AIOfficeSample; EXEC(N'CREATE PROCEDURE {module} WITH EXECUTE AS OWNER AS UPDATE {table} SET Label=N''Mutated'';'); GRANT EXECUTE ON OBJECT::{module} TO aioffice_reader;")
            effect(f"EXEC {module}", "ALLOWED")
            proof("0")
            connection(False)
        finally:
            sql(f"USE AIOfficeSample; DROP PROCEDURE {module};")
        safe()

        try:
            sql(f"USE AIOfficeSample; CREATE USER {impersonated} WITHOUT LOGIN; GRANT UPDATE ON OBJECT::{table} TO {impersonated}; GRANT IMPERSONATE ON USER::{impersonated} TO aioffice_reader;")
            effect(f"EXECUTE AS USER=N'{impersonated}'; UPDATE {table} SET Label=N'Mutated'; REVERT", "ALLOWED")
            proof("0")
            connection(False)
        finally:
            sql(f"USE AIOfficeSample; REVOKE IMPERSONATE ON USER::{impersonated} FROM aioffice_reader; DROP USER {impersonated};")
        safe()
        print("PASS actual ERP direct/column/nested-role/elevated/module/impersonation effects rolled back and API/BFF credential denial")

        # A successful API probe is never a lease for a later independent worker
        # connection. Publish while stopped, elevate, then consume that message.
        run("stop", "agent-worker")
        try:
            status, _, accepted = http("/api/tasks", {"dataSourceId": source, "question": "Owned ERP credential revalidation fixture"},
                base=api, headers={**auth, "Idempotency-Key": "erp-credential-" + suffix})
            assert status == 202
            task = str(uuid.UUID(accepted["taskId"]))
            task_scope = scope + f" AND TaskId='{task}'"
            wait_for(lambda: sql(f"USE AIOfficeLocal; SELECT COUNT(*) FROM aioffice.TaskDispatches WHERE {task_scope} AND State=N'Published';") == "1")
            sql(f"USE AIOfficeSample; GRANT UPDATE ON OBJECT::{table} TO aioffice_reader;")
            effect(f"UPDATE {table} SET Label=N'Mutated'", "ALLOWED")
            run("start", "agent-worker")
            wait_for(lambda: sql(f"USE AIOfficeLocal; SELECT COUNT(*) FROM aioffice.Tasks WHERE {scope} AND Id='{task}' AND Status=N'Failed';") == "1")
            assert sql(f"USE AIOfficeLocal; SELECT COUNT(*) FROM aioffice.TaskDispatches WHERE {task_scope} AND State=N'DeadLettered';") == "1"
            assert sql(f"USE AIOfficeLocal; SELECT COUNT(*) FROM aioffice.TaskStepExecutions WHERE {task_scope} AND LastFailureClass=N'Authorization';") == "1"
            assert sql(f"USE AIOfficeLocal; SELECT COUNT(*) FROM aioffice.TaskCheckpoints WHERE {task_scope};") == "0"
        finally:
            sql(f"USE AIOfficeSample; REVOKE UPDATE ON OBJECT::{table} FROM aioffice_reader;")
            run("start", "agent-worker")
        safe()
        print("PASS actual prior API probe followed by changed ERP rights denies queued worker without checkpoint")

        # Retained metadata permission revocation is not repaired by bootstrap.
        try:
            sql("USE master; REVOKE VIEW ANY DEFINITION FROM aioffice_reader;")
            proof("0")
            connection(False)
            run("run", "--rm", "bootstrap")
            proof("0")
            connection(False)
        finally:
            sql("USE master; GRANT VIEW ANY DEFINITION TO aioffice_reader;")
        safe()
        print("PASS actual ERP metadata revocation remains denied after repeat bootstrap and explicit owned restore")

        # A separate nonshipping executable calls the actual compiled readers and
        # proves final same-session qualification discards materialized evidence.
        installation = uuid.UUID(manifest["AIOFFICE_INSTALLATION_ID"]).hex
        image = "aioffice-erp-proof-" + suffix
        runtime_environment = dict(os.environ)
        for key, user in (("SQL", "sa"), ("READER", "aioffice_reader")):
            password = manifest[f"AIOFFICE_{key}_PASSWORD"]
            assert re.fullmatch(r"[A-Za-z0-9_-]{32,128}", password)
            runtime_environment["AIOFFICE_ERP_PROOF_" + ("OPERATOR" if key == "SQL" else "READER")] = (
                "Server=sql;Database=AIOfficeSample;User ID=" + user + ";Password=" + password
                + ";Encrypt=true;TrustServerCertificate=true;Connect Timeout=15")
        runtime_environment["AIOFFICE_OWNED_ERP_PROOF"] = "true"
        try:
            built = subprocess.run(["docker", "build", "-f", "tests/ErpReadOnly.RuntimeProof/Dockerfile", "-t", image, "."],
                capture_output=True, text=True, timeout=300)
            assert built.returncode == 0, "Owned ERP runtime proof build failed"
            result = subprocess.run(["docker", "run", "--rm", "--network", "aioffice-" + installation + "_default",
                "--read-only", "--cap-drop", "ALL", "--security-opt", "no-new-privileges:true", "--tmpfs", "/tmp:rw,noexec,nosuid,size=16m",
                "-e", "CI", "-e", "GITHUB_ACTIONS", "-e", "AIOFFICE_OWNED_ERP_PROOF",
                "-e", "AIOFFICE_ERP_PROOF_OPERATOR", "-e", "AIOFFICE_ERP_PROOF_READER", image],
                env=runtime_environment, capture_output=True, text=True, timeout=120)
            for line in (result.stdout + result.stderr).splitlines():
                if re.fullmatch(r"(?:PASS actual |FAIL owned ERP runtime proof)[A-Za-z0-9 /_-]*", line):
                    print(line, flush=True)
            assert result.returncode == 0, "Owned compiled ERP reader/mid-read proof failed"
        finally:
            subprocess.run(["docker", "image", "rm", image], capture_output=True, text=True, timeout=30)
    finally:
        sql(f"USE AIOfficeSample; DROP TABLE {table};")
