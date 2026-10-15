"""Owned SQL mutation control. Build-only guard deletion; shipped source untouched."""
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
import uuid

FAILURE = 'FAIL owned original admission runtime phase-admitted-execution-accepted-transaction kind-accepted-preflight-committed'
SUMMARY = 'PASS owned accepted result guard deletion actual SQL preflight committed rejected by fresh exact transaction observer consumed permit cannot mask guard both cleanup gates unchanged retained scopes zero HTTP'



def require_committed_preflight_refusal(result):
    """Exact negative evidence; diagnostics use only a closed public vocabulary."""
    kinds = ('accepted-preflight-committed', 'sql-constraint', 'sql-permission', 'sql-timeout',
        'sql-deadlock', 'sql-other', 'cancelled', 'denied', 'invariant', 'other')
    phases = ('setup', 'fresh-unsafe-permission-refusal', 'allocation', 'private-input', 'original', 'expiry-after-both-flushed', 'expiry-rewind-refusal', 'physical-constraint-boundaries', 'atomic-rollback', 'committed-reply-loss', 'fresh-read-replay', 'physical-column-boundaries', 'accepted-result', 'corrupted-sidecar-refusal', 'constraint-valid-pair', 'constraint-missing-plan', 'constraint-wrong-plan-hash', 'constraint-wrong-claim-operation', 'constraint-wrong-claim-epoch', 'constraint-foreign-tenant', 'constraint-foreign-company', 'constraint-foreign-binding', 'constraint-format-version', 'constraint-short-payload', 'constraint-oversized-payload', 'constraint-empty-operation', 'constraint-zero-epoch', 'constraint-negative-epoch', 'constraint-lowercase-plan-hash', 'constraint-nonhex-sidecar-hash', 'constraint-lowercase-sidecar-hash', 'constraint-non-UTC-time', 'sql-read-original', 'sql-read-predicates', 'admitted-journal-prepare', 'admitted-journal-rollback', 'admitted-journal-commit', 'admitted-journal-replay', 'admitted-execution-current', 'admitted-execution-replay', 'admitted-execution-accepted-refusal', 'admitted-journal-retained-before-issuance', 'admitted-journal-retained-old-interval', 'admitted-journal-retained-replacement', 'admitted-execution-accepted-transaction')
    code = result.returncode if type(result.returncode) is int and -255 <= result.returncode <= 255 else 'other'
    text = result.stdout
    bounded = type(text) is str and len(text) <= 1024
    lines = text.splitlines() if bounded else []
    if (not result.stderr and type(result.returncode) is int and result.returncode == 1
        and lines == [FAILURE, 'FAIL owned reference runtime admission-original-commit']): return
    category = 'unknown-output' if bounded else 'stdout-over-limit-or-invalid'
    if result.stderr: category = 'stderr-present'
    elif bounded and len(lines) == 2 and lines[1] == 'FAIL owned reference runtime admission-original-commit':
        for phase in phases:
            for kind in kinds:
                if lines[0] == 'FAIL owned original admission runtime phase-' + phase + ' kind-' + kind:
                    category = 'phase-' + phase + '-kind-' + kind
    count = min(len(lines), 9) if bounded else 'over-limit'
    # Never format child stdout/stderr, error text, identifiers or credentials.
    raise AssertionError(f'Owned guard deletion did not discriminate committed preflight (exit={code};lines={count};category={category})')


def verify(*, directory, api, installation, tenant, company, service, password, sql, prepare_source, reference, automatic,
           runtime_lines, assert_unchanged):
    reference.require_owned(directory, api)
    for value in (installation, tenant, company, service): automatic.canonical(value)
    assert re.fullmatch(r'[A-Za-z0-9_-]{32,128}', password)
    source_file = Path('src/Platform.Persistence/GroupModelAttemptJournalStore.Execution.cs')
    guard = 'if (accepted) throw Unavailable();'
    assert source_file.read_text(encoding='utf-8').count(guard) == 1
    dockerfile = Path('tests/GroupReference.RuntimeProof/Dockerfile').read_text(encoding='utf-8')
    anchor = 'RUN dotnet publish tests/GroupReference.RuntimeProof/GroupReference.RuntimeProof.csproj'
    assert dockerfile.count(anchor) == 1
    # Mutation happens inside this isolated test image only. No bypass flag,
    # alternate provider/config or source write is added to the application.
    mutation = "RUN sed -i 's/if (accepted) throw Unavailable();/if (accepted) { }/' src/Platform.Persistence/GroupModelAttemptJournalStore.Execution.cs\n"
    suffix = uuid.uuid4().hex; image = 'aioffice-automatic-note-proof-' + suffix
    command = ['docker', 'run', '--init', '--rm', '--name', image, '--label', 'aioffice.owned-proof=' + suffix,
        '--network', 'aioffice-' + uuid.UUID(installation).hex + '_default', '--read-only', '--cap-drop', 'ALL',
        '--security-opt', 'no-new-privileges:true', '--tmpfs', '/tmp:rw,noexec,nosuid,size=16m', '-i',
        '-e', 'CI', '-e', 'GITHUB_ACTIONS', '-e', 'AIOFFICE_OWNED_GROUP_REFERENCE_PROOF',
        '-e', 'AIOFFICE_GROUP_REFERENCE_PROOF_CONNECTION', '-e', 'AIOFFICE_GROUP_REFERENCE_PROOF_SOURCE_KEY', image]
    target = graph = None; failure = None

    def target_graph():
        assert target is not None
        tables = automatic.SOURCE_TABLES + (('GroupAutomaticInputPlans', 'BatchId'), ('GroupOriginalAdmissionSidecars', 'BatchId'),
            ('GroupModelAttemptStarts', 'RequestId'), ('GroupModelAttemptAdmissions', 'RequestId'), ('GroupModelAttemptResults', 'RequestId'),
            ('GroupModelAttemptFailures', 'RequestId'), ('GroupWorkRawDispositions', 'BatchId,CommittedSequence'))
        return tuple(sql("SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),COALESCE("
            f"(SELECT * FROM aioffice.{table} WHERE {target} ORDER BY {order} FOR JSON PATH,INCLUDE_NULL_VALUES),N'[]'))),2);") for table, order in tables)

    try:
        with tempfile.TemporaryDirectory(prefix='aioffice-owned-guard-') as folder:
            mutant = Path(folder) / 'Dockerfile'; mutant.write_text(dockerfile.replace(anchor, mutation + anchor), encoding='utf-8')
            built = subprocess.run(['docker', 'build', '-f', str(mutant), '-t', image, '.'], capture_output=True, text=True, timeout=300)
            assert built.returncode == 0, 'Owned guard counterfactual image build failed'
        source, events, key = prepare_source(); source = automatic.canonical(source)
        events = tuple(automatic.canonical(x) for x in events); assert len(events) == len(set(events)) == 2
        reference.require_source_key(key)
        target = f"TenantId='{tenant}' AND CompanyId='{company}' AND BindingId='{source}'"
        env = dict(os.environ, AIOFFICE_OWNED_GROUP_REFERENCE_PROOF='true',
            AIOFFICE_GROUP_REFERENCE_PROOF_CONNECTION='Server=sql;Database=AIOfficeLocal;User ID=aioffice_runtime;Password=' + password + ';Encrypt=true;TrustServerCertificate=true',
            AIOFFICE_GROUP_REFERENCE_PROOF_SOURCE_KEY=key)
        config = json.dumps(dict(tenantId=tenant, companyId=company, serviceId=service, sourceId=source, eventId=events[0]))
        for mode in ('admission-original-prepare', 'admission-original-expiry', 'admission-original-commit'):
            reference.require_owned(directory, api); assert_unchanged()
            result = subprocess.run([*command, mode], input=config, env=env, capture_output=True, text=True, timeout=170)
            if mode != 'admission-original-commit':
                assert not result.stderr
                reference.require_reference_result(result, mode, [runtime_lines[mode]])
            else:
                # Only successful preflight commit at the exact accepted query
                # qualifies this NEGATIVE control. Generic denial/missing query,
                # missing fixture or successful mutant execution must fail.
                require_committed_preflight_refusal(result)
        assert sql(f"SELECT COUNT(*) FROM aioffice.GroupModelAttemptResults WHERE {target};") == '1'
        assert sql(f"SELECT COUNT(*) FROM aioffice.GroupModelAttemptStarts WHERE {target};") == '1'
        assert sql(f"SELECT COUNT(*) FROM aioffice.GroupModelAttemptAdmissions WHERE {target};") == '1'
        assert sql(f"SELECT COUNT(*) FROM aioffice.GroupModelAttemptFailures WHERE {target};") == '0'
        assert sql(f"SELECT COUNT(*) FROM aioffice.GroupCustomerRequests WHERE {target};") == '0'
        graph = target_graph(); assert all(re.fullmatch(r'[0-9A-F]{64}', value) for value in graph); assert_unchanged()
    except BaseException as error: failure = error
    finally:
        try: automatic.close_owned_container(directory=directory, api=api, name=image, suffix=suffix, reference=reference)
        except BaseException as error:
            if failure is None: failure = error
        try:
            assert_unchanged()
            if graph is not None: assert target_graph() == graph
        except BaseException as error:
            if failure is None: failure = error
        try:
            removed = subprocess.run(['docker', 'image', 'rm', image], capture_output=True, text=True, timeout=30)
            assert removed.returncode == 0, 'Owned guard counterfactual image cleanup failed'
        except BaseException as error:
            if failure is None: failure = error
        try:
            assert_unchanged()
            if graph is not None: assert target_graph() == graph
        except BaseException as error:
            if failure is None: failure = error
    if failure is not None: raise failure
    return SUMMARY
