"""Owned SQL phase after terminal receipts; retained browser checks follow."""
import base64
import importlib.util
import json
import os
from pathlib import Path
import re
import secrets
import subprocess
import uuid

SUCCESS = "PASS owned frontier root SQL original history after expired acquisition atomic key advance cursor actual all fields readback postflush clock rollback all three effects empty immutable26 and14 column denials missing rebound key no effects"
REPLAY_SUCCESS = "PASS owned frontier root independent process replay original signed version time fingerprint all original graphs unchanged no extra advance key or cursor"
CURSOR_SUCCESS = "PASS owned signed frontier cursor SQL trusted seven field foreign key nonempty partial null closure three tuple attacks four permitted noop updates complete original cursor unchanged"
CURSOR_SUMMARY = "PASS actual signed frontier cursor SQL four original scopes trusted complete seven field foreign key empty null tuple attacks refused permitted four noop columns complete original and frontier graphs unchanged no latest chain DI or model"
SUMMARY = "PASS actual root frontiers SQL four original scopes historical expired acquisition atomic key advance cursor all fields readback postflush rollback immutable26 and14 columns missing rebound key independent process replay full original graphs pending501 unchanged no successor DI or model"


def _load(name, filename):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(filename))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def verify(*, directory, api, manifest, sql):
    reference = _load("frontier_reference_boundary", "smoke-group-reference.py")
    reference.require_owned(directory, api)  # Before config, SQL, key or process.
    automatic = _load("frontier_automatic_boundary", "smoke-group-automatic-notes.py")
    terminal = _load("frontier_terminal_boundary", "smoke-group-terminal-receipts.py")
    tenant, company = (automatic.canonical(manifest[name]) for name in ("AIOFFICE_TENANT_ID", "AIOFFICE_COMPANY_ID"))
    installation = automatic.canonical(manifest["AIOFFICE_INSTALLATION_ID"])
    password = manifest["AIOFFICE_RUNTIME_PASSWORD"]
    assert isinstance(password, str) and re.fullmatch(r"[A-Za-z0-9_-]{32,128}", password) and callable(sql)
    base = f"TenantId='{tenant}' AND CompanyId='{company}'"
    query = ("SELECT CONVERT(varchar(40),CONCAT(f.fixtureOrdinal,':',v.fieldOrdinal,':',LOWER(CONVERT(char(36),v.id)))) "
        "FROM (SELECT TOP(5) ROW_NUMBER() OVER(ORDER BY w.BindingId) AS fixtureOrdinal,"
        "w.TenantId AS tenantId,w.CompanyId AS companyId,a.ServiceId AS serviceId,w.BindingId AS sourceId,"
        "a.OperationId AS eventId,a.Id AS batchId FROM aioffice.GroupWorkCommitReceipts w "
        "JOIN aioffice.GroupBatchAllocations a ON a.TenantId=w.TenantId AND a.CompanyId=w.CompanyId AND a.BindingId=w.BindingId AND a.Id=w.BatchId "
        "JOIN aioffice.GroupBatchTerminalReceipts t ON t.TenantId=w.TenantId AND t.CompanyId=w.CompanyId AND t.BindingId=w.BindingId AND t.BatchId=w.BatchId "
        f"WHERE w.TenantId='{tenant}' AND w.CompanyId='{company}' AND w.DependencyManifestVersion=1 "
        "AND DATALENGTH(w.DependencyManifest)=274 AND w.SelectedMessageCount=2 AND SUBSTRING(w.DependencyManifest,161,2)=0x0200 "
        "ORDER BY w.BindingId) f CROSS APPLY (VALUES(1,f.tenantId),(2,f.companyId),(3,f.serviceId),(4,f.sourceId),(5,f.eventId),(6,f.batchId))"
        " v(fieldOrdinal,id) ORDER BY f.fixtureOrdinal,v.fieldOrdinal;")
    fixtures = terminal.require_fixture_frames(sql(query), tenant, company)
    sources = ','.join("'" + x["sourceId"] + "'" for x in fixtures)
    tables = list(automatic.SOURCE_TABLES) + [("GroupWorkRawDispositions", "BatchId,CommittedSequence"), ("GroupBatchTerminalReceipts", "BindingId,BatchId")]
    def original_snapshot():
        queries = [(table, ','.join(dict.fromkeys(("BindingId", *order.split(',')))), base + f" AND BindingId IN ({sources})") for table, order in tables]
        queries += [("GroupBindings", "Id", base), ("GroupConnectorAccounts", "Id", base), ("GroupServices", "Id", base),
            ("GroupListenerLeases", "ConnectorAccountId", base), ("GroupAccountCoverageGaps", "ConnectorAccountId,ListenerEpoch,Reason", base),
            ("GroupListenerCommandReceipts", "ConnectorAccountId,ServiceId,CredentialEpoch,Nonce", base),
            ("Users", "TenantId,Id", "1=1"), ("Tasks", "TenantId,CompanyId,Id", "1=1"),
            ("TaskDispatches", "TenantId,CompanyId,TaskId,StepId,MessageId", "1=1"), ("TaskCheckpoints", "TenantId,CompanyId,TaskId,StepId,Version", "1=1")]
        statements = ["SET NOCOUNT ON;"]
        for index, (table, order, predicate) in enumerate(queries):
            statements.append("SELECT CONCAT(N'" + str(index) + ":',CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),COALESCE("
                f"(SELECT * FROM aioffice.{table} WHERE {predicate} ORDER BY {order} FOR JSON PATH,INCLUDE_NULL_VALUES),N'[]'))),2));")
        lines = sql('\n'.join(statements)).splitlines()
        assert len(lines) == len(queries) and all(re.fullmatch(str(i) + r":[0-9A-F]{64}", x.strip()) for i, x in enumerate(lines))
        return tuple(x.strip() for x in lines)
    def frontier_snapshot(source):
        assert source in {x['sourceId'] for x in fixtures}
        statements = ["SET NOCOUNT ON;"]
        for index, (table, order) in enumerate((("GroupTerminalAdvances", "Version"), ("GroupTerminalAdvanceKeys", "KeyId"), ("GroupTerminalFrontierStates", "BindingId"))):
            statements.append("SELECT CONCAT(N'" + str(index) + ":',CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),COALESCE("
                f"(SELECT * FROM aioffice.{table} WHERE {base} AND BindingId='{source}' ORDER BY {order} FOR JSON PATH,INCLUDE_NULL_VALUES),N'[]'))),2));")
        lines = sql('\n'.join(statements)).splitlines()
        assert len(lines) == 3 and all(re.fullmatch(str(i) + r":[0-9A-F]{64}", x.strip()) for i, x in enumerate(lines))
        return tuple(x.strip() for x in lines)
    def counts(expected):
        for table in ("GroupTerminalAdvances", "GroupTerminalAdvanceKeys", "GroupTerminalFrontierStates"):
            assert sql(f"SELECT COUNT(*) FROM aioffice.{table} WHERE {base};") == str(expected)
    original = original_snapshot(); counts(0)
    suffix = uuid.uuid4().hex; image = "aioffice-automatic-note-proof-" + suffix
    environment = dict(os.environ)
    environment.update({"AIOFFICE_OWNED_GROUP_REFERENCE_PROOF": "true", "AIOFFICE_GROUP_REFERENCE_PROOF_CONNECTION":
        "Server=sql;Database=AIOfficeLocal;User ID=aioffice_runtime;Password=" + password + ";Encrypt=true;TrustServerCertificate=true",
        "AIOFFICE_GROUP_REFERENCE_PROOF_SOURCE_KEY": base64.b64encode(secrets.token_bytes(32)).decode('ascii')})
    command = ["docker", "run", "--init", "--rm", "--name", image, "--label", "aioffice.owned-proof=" + suffix,
        "--network", "aioffice-" + uuid.UUID(installation).hex + "_default", "--read-only", "--cap-drop", "ALL",
        "--security-opt", "no-new-privileges:true", "--tmpfs", "/tmp:rw,noexec,nosuid,size=16m", "-i",
        "-e", "CI", "-e", "GITHUB_ACTIONS", "-e", "AIOFFICE_OWNED_GROUP_REFERENCE_PROOF", "-e", "AIOFFICE_GROUP_REFERENCE_PROOF_CONNECTION",
        "-e", "AIOFFICE_GROUP_REFERENCE_PROOF_SOURCE_KEY", image]
    failure = None
    try:
        built = subprocess.run(["docker", "build", "-f", "tests/GroupReference.RuntimeProof/Dockerfile", "-t", image, "."], capture_output=True, text=True, timeout=300)
        assert built.returncode == 0, "Owned frontier executable build failed"
        completed = {}
        for index, fixture in enumerate(fixtures):
            configuration = {key: value for key, value in fixture.items() if key != "batchId"}
            result = subprocess.run(command + ["frontier-root"], input=json.dumps(configuration), env=environment, capture_output=True, text=True, timeout=170)
            assert not result.stderr, "Owned frontier executable emitted unexpected diagnostics"
            reference.require_reference_result(result, "frontier-root", [SUCCESS])
            counts(index + 1); assert original_snapshot() == original, "Root frontier changed original graphs"
            for source, written in completed.items():
                assert frontier_snapshot(source) == written, "Root frontier changed another original frontier"
            completed[fixture['sourceId']] = frontier_snapshot(fixture['sourceId'])
            result = subprocess.run(command + ["frontier-root-replay"], input=json.dumps(configuration), env=environment, capture_output=True, text=True, timeout=170)
            assert not result.stderr, "Owned frontier replay emitted unexpected diagnostics"
            reference.require_reference_result(result, "frontier-root-replay", [REPLAY_SUCCESS])
            counts(index + 1); assert original_snapshot() == original, "Frontier replay changed original graphs"
            for source, written in completed.items():
                assert frontier_snapshot(source) == written, "Independent process replay changed original frontier"
            result = subprocess.run(command + ["frontier-cursor-contract"], input=json.dumps(configuration), env=environment, capture_output=True, text=True, timeout=170)
            assert not result.stderr, "Owned cursor contract emitted unexpected diagnostics"
            reference.require_reference_result(result, "frontier-cursor-contract", [REPLAY_SUCCESS, CURSOR_SUCCESS])
            counts(index + 1); assert original_snapshot() == original, "Cursor contract changed original graphs"
            for source, written in completed.items():
                assert frontier_snapshot(source) == written, "Cursor contract changed an original frontier"
    except BaseException as error:
        failure = error
    finally:
        try:
            automatic.close_owned_container(directory=directory, api=api, name=image, suffix=suffix, reference=reference)
        except BaseException as error:
            if failure is None: failure = error
        try:
            removed = subprocess.run(["docker", "image", "rm", image], capture_output=True, text=True, timeout=30)
            if removed.returncode != 0 and failure is None: failure = AssertionError("Owned frontier image cleanup failed")
        except BaseException as error:
            if failure is None: failure = error
    if failure is not None: raise failure
    print(SUMMARY, flush=True)
    print(CURSOR_SUMMARY, flush=True)
