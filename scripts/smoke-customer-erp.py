"""Customer ERP API and worker proof, restricted to the owned disposable CI stack."""
import json
import os
from pathlib import Path
import uuid


def verify(*, directory, manifest, http, sql, wait_for, api, auth):
    assert os.environ.get("CI") == "true" and os.environ.get("GITHUB_ACTIONS") == "true"
    assert os.environ.get("RUNNER_TEMP") and directory.resolve() == (Path(os.environ["RUNNER_TEMP"]) / "aioffice-local").resolve()
    assert api == "http://127.0.0.1:8080"
    tenant, company = (str(uuid.UUID(manifest[f"AIOFFICE_{key}_ID"])) for key in ("TENANT", "COMPANY"))
    suffix = uuid.uuid4().hex
    login = "CustomerErpReader_" + suffix
    password, rotated_password = "Aa1_" + uuid.uuid4().hex + uuid.uuid4().hex, "Aa1_" + uuid.uuid4().hex + uuid.uuid4().hex
    fields = {"host": "sql", "port": 1433, "database": "AIOfficeSample", "userName": login, "password": password,
              "encrypt": True, "trustServerCertificate": True}
    source_id = None

    def call(path, payload=None, method=None, expected=200):
        request_headers = {**auth, **({"Idempotency-Key": "customer-erp-" + suffix} if path == "/api/tasks" else {})}
        status, headers, result = http(path, payload, method=method, base=api, headers=request_headers)
        assert status == expected, "Customer ERP API status mismatch"
        assert "no-store" in headers.get("Cache-Control", "")
        serialized = json.dumps(result)
        assert password not in serialized and rotated_password not in serialized
        assert all(value not in serialized for key, value in manifest.items() if "PASSWORD" in key)
        assert "secretref" not in serialized.lower()
        return result

    def test(payload, code):
        result = call("/api/data-sources/connection-test", payload)
        assert result["code"] == code, "Customer ERP verification code mismatch"
        return result

    try:
        sql(f"USE master; CREATE LOGIN [{login}] WITH PASSWORD=N'{password}'; USE AIOfficeSample; CREATE USER [{login}] FOR LOGIN [{login}]; GRANT SELECT TO [{login}];")
        test({**fields, "password": "Wrong_" + uuid.uuid4().hex}, "login_failed")
        test({**fields, "database": "Missing_" + suffix}, "database_not_found")
        test({**fields, "userName": "sa", "password": manifest["AIOFFICE_SQL_PASSWORD"]}, "not_read_only")
        test(fields, "missing_view_definition")
        sql(f"USE master; GRANT VIEW ANY DEFINITION TO [{login}]; USE AIOfficeSample; GRANT VIEW DEFINITION TO [{login}];")
        positive = test(fields, "ok")
        assert positive["tableCount"] > 0 and positive["serverVersion"].startswith("16.")
        print("PASS customer ERP actual wrong-password missing-database elevated missing-metadata and safe-reader codes")
        create = {**fields, "operationId": str(uuid.uuid4()), "logicalName": "Owned customer ERP " + suffix}
        created = call("/api/data-sources/customer-erp", create)
        source_id = str(uuid.UUID(created["id"]))
        assert created["bindingVersion"] == "1" and call("/api/data-sources/customer-erp", create) == created
        scope = f"TenantId='{tenant}' AND CompanyId='{company}' AND Id='{source_id}'"
        assert sql(f"USE AIOfficeLocal; SELECT COUNT(*) FROM aioffice.ProtectedDataSourceSecrets WHERE {scope} AND Version=1 AND DATALENGTH(Nonce)=12 AND DATALENGTH(Tag)=16;") == "1"
        assert sql(f"USE AIOfficeLocal; SELECT COUNT(*) FROM aioffice.DataSources WHERE {scope} AND AllowRead=1 AND AllowWrite=0;") == "1"
        sql(f"USE master; ALTER LOGIN [{login}] WITH PASSWORD=N'{rotated_password}';")
        rotate = {**fields, "password": rotated_password, "operationId": str(uuid.uuid4()), "logicalName": create["logicalName"], "bindingVersion": "1"}
        rotated = call(f"/api/data-sources/{source_id}/credentials", rotate, "PUT")
        assert rotated["bindingVersion"] == "2" and call(f"/api/data-sources/{source_id}/credentials", rotate, "PUT") == rotated
        stale = {**rotate, "operationId": str(uuid.uuid4())}
        call(f"/api/data-sources/{source_id}/credentials", stale, "PUT", expected=409)
        # Ten attempts exhausted. No outbound SQL on this refusal.
        call("/api/data-sources/connection-test", fields, expected=429)
        assert sql(f"USE AIOfficeLocal; SELECT COUNT(*) FROM aioffice.ProtectedDataSourceSecrets WHERE {scope} AND Version=2;") == "1"
        print("PASS customer ERP actual atomic create rotation exact replay version conflict encrypted row and company rate limit")
        # Shipping worker uses the protected binding, not a per-customer env var.
        # The existing connection-test operation does not make a model request.
        task = call("/api/tasks", {"dataSourceId": source_id, "question": "Owned customer ERP connection qualification"}, expected=202)
        task_id = str(uuid.UUID(task["taskId"]))

        def completed():
            snapshot = call(f"/api/tasks/{task_id}")
            return snapshot if snapshot["taskStatus"] == 6 else None

        wait_for(completed, seconds=90)
        assert sql(f"USE AIOfficeLocal; SELECT COUNT(*) FROM aioffice.DataSourceRegistrationAudits WHERE TenantId='{tenant}' AND CompanyId='{company}' AND DataSourceId='{source_id}';") == "2"
        print("PASS customer ERP shipping queued worker resolves rotated protected credential without customer environment binding")
    finally:
        if source_id:
            # Preserve immutable operation history and task/audit evidence; the
            # enclosing owned disposable stack is removed by the mandatory gate.
            sql(f"USE AIOfficeLocal; UPDATE aioffice.DataSources SET IsEnabled=0 WHERE TenantId='{tenant}' AND CompanyId='{company}' AND Id='{source_id}';")
        sql(f"USE AIOfficeSample; DROP USER IF EXISTS [{login}]; USE master; DROP LOGIN [{login}];")
