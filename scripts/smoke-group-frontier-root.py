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
MIGRATION_SUCCESS = "PASS owned signed cursor legacy migration shadow authentic original eleven table closure empty version through UTC unsigned refusals exact51048 full rows history schema security unchanged valid upgrade trusted seven tuple FK repeat unchanged owned database removed no all seven mismatch DI or model"
MIGRATION_SUMMARY = "PASS actual signed cursor migration one owned shadow authentic original eleven table legacy closure five exact51048 refusals full schema rows history security unchanged positive upgrade trusted tuple FK repeat unchanged original scopes unchanged owned database removed no all seven mismatch DI or model"


def _load(name, filename):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(filename))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def require_shadow_sql_network(*, directory, api, installation, reference):
    reference.require_owned(directory, api)  # Before Docker or operator configuration.
    assert isinstance(installation, str) and str(uuid.UUID(installation)) == installation and uuid.UUID(installation).int
    project = 'aioffice-' + uuid.UUID(installation).hex
    network = project + '_default'
    inspected = subprocess.run(['docker', 'network', 'inspect', network, '--format',
        '{{.Id}}|{{index .Labels "com.docker.compose.project"}}|{{index .Labels "com.docker.compose.network"}}'],
        capture_output=True, text=True, timeout=10)
    assert inspected.returncode == 0 and not inspected.stderr, 'Owned shadow network inspection failed'
    fields = inspected.stdout.strip().split('|')
    assert len(fields) == 3 and re.fullmatch('[0-9a-f]{64}', fields[0]) and fields[1:] == [project, 'default'], \
        'Owned shadow network identity mismatch'
    network_id = fields[0]
    listed = subprocess.run(['docker', 'ps', '--no-trunc', '--quiet', '--filter', 'status=running',
        '--filter', 'label=com.docker.compose.project=' + project, '--filter', 'label=com.docker.compose.service=sql'],
        capture_output=True, text=True, timeout=10)
    assert listed.returncode == 0 and not listed.stderr and re.fullmatch('[0-9a-f]{64}', listed.stdout.strip()), \
        'Owned shadow SQL container is not unique'
    identity = listed.stdout.strip()
    inspected = subprocess.run(['docker', 'inspect', '--type', 'container', '--format',
        '{{.Id}}|{{.State.Running}}|{{index .Config.Labels "com.docker.compose.project"}}|'
        '{{index .Config.Labels "com.docker.compose.service"}}|{{with index .NetworkSettings.Networks "' + network + '"}}'
        '{{.NetworkID}}|{{join .Aliases ","}}{{end}}', identity], capture_output=True, text=True, timeout=10)
    assert inspected.returncode == 0 and not inspected.stderr, 'Owned shadow SQL container inspection failed'
    fields = inspected.stdout.strip().split('|')
    assert len(fields) == 6 and fields[:5] == [identity, 'true', project, 'sql', network_id], \
        'Owned shadow SQL container identity mismatch'
    aliases = fields[5].split(',')
    assert 1 <= len(aliases) <= 8 and len(aliases) == len(set(aliases)) and 'sql' in aliases \
        and all(re.fullmatch('[A-Za-z0-9_.-]{1,128}', x) for x in aliases), 'Owned shadow SQL alias mismatch'


def require_shadow_migration_result(result, reference):
    lines = result.stdout.splitlines()
    phases = {'owned-guard', 'original-authentication', 'operator-configuration', 'shadow-create', 'shadow-ownership',
        'predecessor-migration', 'original-seed', 'legacy-version-check', 'legacy-utc-check', 'empty-preflight',
        'version-preflight', 'through-preflight', 'utc-preflight', 'unsigned-preflight', 'matching-positive-upgrade',
        'positive-repeat', 'original-source-unchanged', 'owned-shadow-cleanup', 'complete'}
    prefix = 'FAIL owned signed cursor migration '
    if (type(result.returncode) is int and result.returncode == 1 and len(lines) in (2, 3)
            and (len(lines) == 2 or lines[0] == REPLAY_SUCCESS)
            and lines[-1] == 'FAIL owned reference runtime frontier-cursor-migration' and lines[-2].startswith(prefix)):
        phase = lines[-2][len(prefix):]
        if phase in phases:
            raise AssertionError('Owned signed cursor migration failed; phase=' + phase)
    reference.require_reference_result(result, 'frontier-cursor-migration', [REPLAY_SUCCESS, MIGRATION_SUCCESS])


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
    environment.pop('AIOFFICE_GROUP_CURSOR_PROOF_OPERATOR_CONNECTION', None)
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
        require_shadow_sql_network(directory=directory, api=api, installation=installation, reference=reference)
        operator_password = manifest['AIOFFICE_SQL_PASSWORD']
        assert isinstance(operator_password, str) and re.fullmatch(r'[A-Za-z0-9_-]{32,128}', operator_password)
        shadow_environment = dict(environment)
        shadow_environment['AIOFFICE_GROUP_CURSOR_PROOF_OPERATOR_CONNECTION'] = (
            'Server=sql;Database=AIOfficeLocal;User ID=sa;Password=' + operator_password + ';Encrypt=true;TrustServerCertificate=true')
        # Generated SA is forwarded only to this guarded owned-shadow child;
        # the original twelve runtime children never receive this credential.
        shadow_command = command[:-1] + ['-e', 'AIOFFICE_GROUP_CURSOR_PROOF_OPERATOR_CONNECTION', command[-1]]
        configuration = {key: value for key, value in fixtures[0].items() if key != 'batchId'}
        result = subprocess.run(shadow_command + ['frontier-cursor-migration'], input=json.dumps(configuration),
            env=shadow_environment, capture_output=True, text=True, timeout=330)
        assert not result.stderr, 'Owned cursor migration emitted unexpected diagnostics'
        require_shadow_migration_result(result, reference)
        counts(4); assert original_snapshot() == original, 'Cursor migration changed original graphs'
        for source, written in completed.items():
            assert frontier_snapshot(source) == written, 'Cursor migration changed an original frontier'
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
    print(MIGRATION_SUMMARY, flush=True)
