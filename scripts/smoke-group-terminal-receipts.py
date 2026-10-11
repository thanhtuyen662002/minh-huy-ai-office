"""Owned terminal receipt SQL phase after the preserved original116 gates."""
import importlib.util
import json
import os
from pathlib import Path
import re
import subprocess
import uuid


SUCCESS = "PASS owned terminal receipt actual SQL insert readback immutable24 columns expiry postflush rollback witness only clock rollback original and later lease replay nonce collision original effects unchanged no cursor or model"
SUMMARY = "PASS actual terminal receipts SQL four original scopes insert readback immutable24 columns postflush expiry rollback witness only clock rollback later lease replay nonce collision full original graphs pending501 unchanged no cursor or model"


def _load(name, filename):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(filename))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def require_fixtures(text, tenant, company):
    assert isinstance(text, str) and 1 <= len(text.encode("utf-8")) <= 4096
    def unique(pairs):
        value = {}
        for key, item in pairs:
            assert key not in value
            value[key] = item
        return value
    rows = json.loads(text, object_pairs_hook=unique)
    assert isinstance(rows, list) and len(rows) == 4
    names = {"tenantId", "companyId", "serviceId", "sourceId", "eventId", "batchId"}
    for row in rows:
        assert isinstance(row, dict) and set(row) == names
        for value in row.values():
            assert isinstance(value, str) and str(uuid.UUID(value)) == value and uuid.UUID(value).int
        assert row["tenantId"] == tenant and row["companyId"] == company
    for name in ("sourceId", "eventId", "batchId"):
        assert len({row[name] for row in rows}) == 4
    return rows


def verify(*, directory, api, manifest, sql):
    reference = _load("terminal_reference_boundary", "smoke-group-reference.py")
    reference.require_owned(directory, api)  # Before configuration/SQL/process.
    automatic = _load("terminal_automatic_boundary", "smoke-group-automatic-notes.py")
    tenant, company = (automatic.canonical(manifest[name]) for name in ("AIOFFICE_TENANT_ID", "AIOFFICE_COMPANY_ID"))
    installation = automatic.canonical(manifest["AIOFFICE_INSTALLATION_ID"])
    password = manifest["AIOFFICE_RUNTIME_PASSWORD"]
    assert isinstance(password, str) and re.fullmatch(r"[A-Za-z0-9_-]{32,128}", password) and callable(sql)
    base = f"TenantId='{tenant}' AND CompanyId='{company}'"
    query = ("SELECT TOP(5) LOWER(CONVERT(char(36),w.TenantId)) AS tenantId,LOWER(CONVERT(char(36),w.CompanyId)) AS companyId,"
        "LOWER(CONVERT(char(36),a.ServiceId)) AS serviceId,LOWER(CONVERT(char(36),w.BindingId)) AS sourceId,"
        "LOWER(CONVERT(char(36),a.OperationId)) AS eventId,LOWER(CONVERT(char(36),a.Id)) AS batchId "
        "FROM aioffice.GroupWorkCommitReceipts w JOIN aioffice.GroupBatchAllocations a ON a.TenantId=w.TenantId "
        "AND a.CompanyId=w.CompanyId AND a.BindingId=w.BindingId AND a.Id=w.BatchId "
        f"WHERE w.TenantId='{tenant}' AND w.CompanyId='{company}' AND w.DependencyManifestVersion=1 "
        "AND DATALENGTH(w.DependencyManifest)=274 AND w.SelectedMessageCount=2 AND SUBSTRING(w.DependencyManifest,161,2)=0x0200 "
        "ORDER BY w.BindingId FOR JSON PATH;")
    fixtures = require_fixtures(sql(query), tenant, company)
    source_list = ','.join("'" + value["sourceId"] + "'" for value in fixtures)
    original_tables = [(table, order) for table, order in automatic.SOURCE_TABLES if table not in ("GroupBatchClaimStates", "GroupBatchClaimReceipts")]
    original_tables += [("GroupWorkRawDispositions", "BatchId,CommittedSequence"), ("GroupTerminalFrontierStates", "BindingId")]
    def original_snapshot():
        statements = ["SET NOCOUNT ON;"]
        queries = []
        for table, order in original_tables:
            ordered = ','.join(dict.fromkeys(("BindingId", *order.split(','))))
            queries.append((table, ordered, base + f" AND BindingId IN ({source_list})"))
        queries += [("GroupBindings", "Id", base), ("GroupConnectorAccounts", "Id", base),
            ("GroupListenerLeases", "ConnectorAccountId", base), ("GroupAccountCoverageGaps", "ConnectorAccountId,ListenerEpoch,Reason", base),
            ("GroupListenerCommandReceipts", "ConnectorAccountId,ServiceId,CredentialEpoch,Nonce", base), ("GroupServices", "Id", base),
            ("Users", "TenantId,Id", "1=1"), ("Tasks", "TenantId,CompanyId,Id", "1=1"),
            ("TaskDispatches", "TenantId,CompanyId,TaskId,StepId,MessageId", "1=1"), ("TaskCheckpoints", "TenantId,CompanyId,TaskId,StepId,Version", "1=1")]
        for index, (table, ordered, predicate) in enumerate(queries):
            statements.append("SELECT CONCAT(N'" + str(index) + ":',CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),COALESCE("
                f"(SELECT * FROM aioffice.{table} WHERE {predicate} ORDER BY {ordered} "
                "FOR JSON PATH,INCLUDE_NULL_VALUES),N'[]'))),2));")
        lines = sql('\n'.join(statements)).splitlines()
        assert len(lines) == len(queries)
        assert all(re.fullmatch(str(index) + r":[0-9A-F]{64}", line.strip()) for index, line in enumerate(lines))
        return tuple(line.strip() for line in lines)
    original = original_snapshot()
    assert sql(f"SELECT COUNT(*) FROM aioffice.GroupBatchTerminalReceipts WHERE {base};") == "0"
    suffix = uuid.uuid4().hex
    image = "aioffice-automatic-note-proof-" + suffix
    environment = dict(os.environ)
    environment.update({"AIOFFICE_OWNED_GROUP_REFERENCE_PROOF": "true", "AIOFFICE_GROUP_REFERENCE_PROOF_CONNECTION":
        "Server=sql;Database=AIOfficeLocal;User ID=aioffice_runtime;Password=" + password + ";Encrypt=true;TrustServerCertificate=true"})
    command = ["docker", "run", "--init", "--rm", "--name", image, "--label", "aioffice.owned-proof=" + suffix,
        "--network", "aioffice-" + uuid.UUID(installation).hex + "_default", "--read-only", "--cap-drop", "ALL",
        "--security-opt", "no-new-privileges:true", "--tmpfs", "/tmp:rw,noexec,nosuid,size=16m", "-i",
        "-e", "CI", "-e", "GITHUB_ACTIONS", "-e", "AIOFFICE_OWNED_GROUP_REFERENCE_PROOF",
        "-e", "AIOFFICE_GROUP_REFERENCE_PROOF_CONNECTION", image, "terminal-commit"]
    failure = None
    try:
        built = subprocess.run(["docker", "build", "-f", "tests/GroupReference.RuntimeProof/Dockerfile", "-t", image, "."],
            capture_output=True, text=True, timeout=300)
        assert built.returncode == 0, "Owned terminal executable build failed"
        for index, fixture in enumerate(fixtures):
            configuration = {key: value for key, value in fixture.items() if key != "batchId"}
            result = subprocess.run(command, input=json.dumps(configuration), env=environment, capture_output=True, text=True, timeout=170)
            assert not result.stderr, "Owned terminal executable emitted unexpected diagnostics"
            reference.require_reference_result(result, "terminal-commit", [SUCCESS])
            assert original_snapshot() == original, "Terminal phase changed original source effects grants or frontier"
            assert sql(f"SELECT COUNT(*) FROM aioffice.GroupBatchTerminalReceipts WHERE {base};") == str(index + 1)
    except BaseException as error:
        failure = error
    finally:
        try:
            automatic.close_owned_container(directory=directory, api=api, name=image, suffix=suffix, reference=reference)
        except BaseException as error:
            if failure is None: failure = error
        try:
            removed = subprocess.run(["docker", "image", "rm", image], capture_output=True, text=True, timeout=30)
            if removed.returncode != 0 and failure is None: failure = AssertionError("Owned terminal image cleanup failed")
        except BaseException as error:
            if failure is None: failure = error
    if failure is not None: raise failure
    print(SUMMARY, flush=True)
