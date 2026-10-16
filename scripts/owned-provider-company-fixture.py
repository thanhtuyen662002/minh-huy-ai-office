"""Enrollment additions for the owned disposable Code/S256 company discovery proof."""
import json
import os
from pathlib import Path
import uuid


def prepare(*, directory, manifest, sql, identity_admin, api):
    # Refuse before touching configuration, SQL or provider callbacks elsewhere.
    if not (os.environ.get("CI") == "true" and os.environ.get("GITHUB_ACTIONS") == "true"
            and os.environ.get("RUNNER_TEMP") and directory.resolve() == (Path(os.environ["RUNNER_TEMP"]) / "aioffice-local").resolve()
            and api == "http://127.0.0.1:8080"):
        raise RuntimeError("Company discovery fixture requires the owned disposable CI installation.")
    target = directory / "company-signin-fixture.json"
    if target.exists():
        raise RuntimeError("Owned company fixture already exists.")
    tenant, default, original_source = (str(uuid.UUID(manifest["AIOFFICE_" + key + "_ID"])) for key in ("TENANT", "COMPANY", "DATA_SOURCE"))
    companies = [str(uuid.uuid4()), str(uuid.uuid4())]
    sources = [str(uuid.uuid4()), str(uuid.uuid4())]
    identities = []
    for index in range(3):
        username = "company-proof-" + uuid.uuid4().hex
        identity_admin("users", {"username": username, "enabled": True, "emailVerified": True,
            "firstName": "Disposable", "lastName": "Company", "email": username + "@example.invalid",
            "credentials": [{"type": "password", "value": manifest["AIOFFICE_OWNER_PASSWORD"], "temporary": False}]}, method="POST")
        users = identity_admin("users?username=" + username + "&exact=true")
        assert len(users) == 1 and users[0]["username"] == username
        identities.append({"userId": str(uuid.uuid4()), "username": username,
            "subject": str(uuid.UUID(users[0]["id"])), "companies": companies[:1] if index == 0 else companies[:] if index == 1 else []})
    assert len({item["subject"] for item in identities}) == 3, "Owned provider identities must be distinct."
    statements = ["USE AIOfficeLocal; SET XACT_ABORT ON; BEGIN TRANSACTION;"]
    for index, company in enumerate(companies):
        statements.append(f"INSERT aioffice.Companies(TenantId,Id,Code,Name) VALUES('{tenant}','{company}',N'{company}',N'Disposable sign-in company {index + 1}');")
        statements.append(f"""INSERT aioffice.DataSources(TenantId,CompanyId,Id,LogicalName,Kind,Environment,Purpose,ConnectionSecretReference,AllowRead,AllowWrite,MaxConcurrency,IsEnabled)
          SELECT TenantId,'{company}','{sources[index]}',N'Disposable sign-in source {index + 1}',Kind,Environment,Purpose,ConnectionSecretReference,
          AllowRead,AllowWrite,MaxConcurrency,IsEnabled FROM aioffice.DataSources
          WHERE TenantId='{tenant}' AND CompanyId='{default}' AND Id='{original_source}'; IF @@ROWCOUNT<>1 THROW 51000,'Owned fixture source unavailable',1;""")
    for item in identities:
        statements.append(f"INSERT aioffice.Users(TenantId,Id,IdentityProvider,Subject,DisplayName) VALUES('{tenant}','{item['userId']}',N'local-keycloak',N'{item['subject']}',N'Disposable company selection user');")
        for company in item["companies"]:
            statements.append(f"INSERT aioffice.CompanyMemberships(TenantId,CompanyId,UserId) VALUES('{tenant}','{company}','{item['userId']}');")
            statements.append(f"INSERT aioffice.RoleAssignments(TenantId,CompanyId,UserId,RoleKey) VALUES('{tenant}','{company}','{item['userId']}',N'viewer');")
    statements.append("COMMIT TRANSACTION;")
    sql("\n".join(statements))
    for item in identities:
        assert sql(f"USE AIOfficeLocal; SELECT COUNT(*) FROM aioffice.CompanyMemberships WHERE TenantId='{tenant}' AND CompanyId='{default}' AND UserId='{item['userId']}';") == "0"
        del item["subject"]  # Browser proof does not receive provider subjects.
    target.write_text(json.dumps({"companies": companies, "sources": sources, "identities": identities}, separators=(",", ":")), encoding="utf-8")
    target.chmod(0o600)
    print("PASS owned two nondefault provider identities plus no-choice identity with zero default-company memberships")
