"""Cold adversarial checks; mocks qualify the coordinator only, never actual SQL."""
import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
from types import SimpleNamespace
import unittest
from unittest.mock import patch
import uuid

spec = importlib.util.spec_from_file_location('frontier_root_proof_test', Path(__file__).with_name('smoke-group-frontier-root.py'))
proof = importlib.util.module_from_spec(spec)
spec.loader.exec_module(proof)


class FrontierRootProofTests(unittest.TestCase):
    def setUp(self):
        self.tenant, self.company, self.service, self.installation = (str(uuid.uuid4()) for _ in range(4))
        self.rows = [dict(tenantId=self.tenant, companyId=self.company, serviceId=self.service,
            sourceId=str(uuid.uuid4()), eventId=str(uuid.uuid4()), batchId=str(uuid.uuid4())) for _ in range(4)]
        self.directory = Path(__file__).resolve().parent/'owned-private-runner'/'aioffice-local'
        self.environment = dict(CI='true', GITHUB_ACTIONS='true', RUNNER_TEMP=str(self.directory.parent))
        self.manifest = dict(AIOFFICE_TENANT_ID=self.tenant, AIOFFICE_COMPANY_ID=self.company,
            AIOFFICE_INSTALLATION_ID=self.installation, AIOFFICE_RUNTIME_PASSWORD='P'*40)

    def test_unowned_guard_precedes_all_config_sql_secret_and_process_access(self):
        def forbidden(*args, **kwargs): self.fail('Unowned proof crossed a private boundary')
        for invalid in ({'CI': 'false'}, {'GITHUB_ACTIONS': 'false'}, {'RUNNER_TEMP': ''}):
            with patch.dict(os.environ, {**self.environment, **invalid}, clear=True), \
                    patch.object(proof.subprocess, 'run', forbidden), patch.object(proof.secrets, 'token_bytes', forbidden), self.assertRaises(RuntimeError):
                proof.verify(directory=self.directory, api='http://127.0.0.1:8080', manifest=None, sql=forbidden)

    def oracle(self, fault=None):
        roots = replays = 0; calls = []; queries = []
        def sql(query):
            queries.append(query)
            if query.startswith('SELECT CONVERT(varchar(40)'):
                self.assertIn('SELECT TOP(5)', query); self.assertIn('JOIN aioffice.GroupBatchTerminalReceipts', query)
                names = ('tenantId', 'companyId', 'serviceId', 'sourceId', 'eventId', 'batchId')
                rows = self.rows + [self.rows[0]] if fault == 'fifth-fixture' else self.rows
                frames = '\n'.join(f'{i+1}:{j+1}:{row[name]}' for i, row in enumerate(rows) for j, name in enumerate(names))
                return frames[:-1] if fault == 'truncated-fixture' else frames
            if query.startswith('SELECT COUNT(*) FROM aioffice.'):
                if fault == 'preexisting': return '1'
                return str(roots + (1 if roots and fault == 'extra-frontier' else -1 if roots and fault == 'missing-frontier' else 0))
            if query.startswith('SET NOCOUNT ON;'):
                original = 'aioffice.GroupMessages ' in query
                count = query.count('SELECT CONCAT')
                self.assertEqual(35 if original else 3, count)
                marker = 'B' if ((original and roots and fault == 'original-mutation') or (not original and replays and fault == 'replay-mutation')) else 'A'
                if not original and roots > 1 and fault == 'prior-frontier-mutation' and self.rows[0]['sourceId'] in query: marker = 'B'
                lines = [str(i)+':'+marker*64 for i in range(count)]
                if fault == 'truncated-original' and original: lines.pop()
                if fault == 'truncated-frontier' and not original: lines.pop()
                if fault == 'duplicate-digest' and original: lines[1] = lines[0]
                return '\n'.join(lines)
            self.fail('Unexpected SQL shape')
        def process(command, **arguments):
            nonlocal roots, replays
            calls.append((command, arguments))
            if command[:2] == ['docker', 'build']:
                if fault == 'build-timeout': raise subprocess.TimeoutExpired(command, arguments['timeout'])
                return SimpleNamespace(returncode=1 if fault == 'build' else 0, stdout='', stderr='')
            if command[:2] == ['docker', 'run']:
                replay = command[-1] == 'frontier-root-replay'
                if fault == ('replay-timeout' if replay else 'root-timeout'): raise subprocess.TimeoutExpired(command, arguments['timeout'])
                if replay: replays += 1
                else: roots += 1
                output = (proof.REPLAY_SUCCESS if replay else proof.SUCCESS)+'\n'
                if fault == 'missing-success': output = ''
                if fault == 'duplicate-success': output += output
                if fault == 'wrong-success': output = 'PASS another mode\n'
                if fault == 'wrong-replay' and replay: output = proof.SUCCESS+'\n'
                return SimpleNamespace(returncode=1 if fault == 'exit' else 0, stdout=output,
                    stderr='private detail' if fault == 'stderr' else '')
            if command[:2] == ['docker', 'inspect']:
                if fault == 'foreign-container':
                    return SimpleNamespace(returncode=0, stdout='a'*64+'|'+'b'*32, stderr='')
                return SimpleNamespace(returncode=1, stdout='', stderr='Error: No such object: '+command[-1])
            if command[:3] == ['docker', 'image', 'rm']:
                return SimpleNamespace(returncode=1 if fault == 'cleanup' else 0, stdout='', stderr='')
            self.fail('Unexpected process or unowned container removal')
        return sql, process, calls, queries

    def run_proof(self, sql, process):
        with patch.dict(os.environ, self.environment, clear=True), patch.object(proof.subprocess, 'run', process), \
                patch('sys.stdout', new_callable=io.StringIO) as output:
            proof.verify(directory=self.directory, api='http://127.0.0.1:8080', manifest=self.manifest, sql=sql)
        return output.getvalue()

    def test_four_roots_eight_distinct_children_replay_and_all_original_full_row_snapshots_are_required(self):
        sql, process, calls, queries = self.oracle()
        self.assertEqual(proof.SUMMARY+'\n', self.run_proof(sql, process))
        children = [(command, arguments) for command, arguments in calls if command[:2] == ['docker', 'run']]
        self.assertEqual(8, len(children))
        for index, (command, arguments) in enumerate(children):
            self.assertEqual('frontier-root-replay' if index % 2 else 'frontier-root', command[-1])
            self.assertEqual(170, arguments['timeout']); self.assertTrue(arguments['capture_output'])
            for required in ('--init', '--rm', '--read-only', '--cap-drop', 'ALL', 'no-new-privileges:true', '/tmp:rw,noexec,nosuid,size=16m'):
                self.assertIn(required, command)
            configuration = json.loads(arguments['input'])
            self.assertEqual({k: v for k, v in self.rows[index//2].items() if k != 'batchId'}, configuration)
            visible = ' '.join(command)+arguments['input']
            self.assertNotIn(self.manifest['AIOFFICE_RUNTIME_PASSWORD'], visible)
            self.assertNotIn(arguments['env']['AIOFFICE_GROUP_REFERENCE_PROOF_SOURCE_KEY'], visible)
        self.assertEqual(1, len({arguments['env']['AIOFFICE_GROUP_REFERENCE_PROOF_SOURCE_KEY'] for _, arguments in children}))
        originals = [q for q in queries if q.startswith('SET NOCOUNT ON;') and 'aioffice.GroupMessages ' in q]
        frontiers = [q for q in queries if q.startswith('SET NOCOUNT ON;') and 'aioffice.GroupTerminalAdvances ' in q]
        self.assertEqual(9, len(originals)); self.assertEqual(20, len(frontiers))
        for table in ('GroupWorkRawDispositions', 'GroupBatchClaimReceipts', 'GroupBatchTerminalReceipts', 'TaskCheckpoints'):
            self.assertTrue(all(table in query for query in originals))
        self.assertTrue(all('INCLUDE_NULL_VALUES' in query and 'SELECT *' in query for query in originals+frontiers))

    def test_incomplete_closed_frames_or_preexisting_effects_refuse_before_any_process(self):
        for fault in ('fifth-fixture', 'truncated-fixture', 'truncated-original', 'duplicate-digest', 'preexisting'):
            sql, process, calls, _ = self.oracle(fault)
            with self.subTest(fault=fault), self.assertRaises(AssertionError), patch('sys.stdout', new_callable=io.StringIO) as output:
                self.run_proof(sql, process)
            self.assertEqual([], calls); self.assertEqual('', output.getvalue())

    def test_failure_timeout_changed_history_or_replay_never_emit_success_and_always_attempt_owned_cleanup(self):
        for fault in ('build', 'build-timeout', 'root-timeout', 'replay-timeout', 'missing-success', 'duplicate-success',
                'wrong-success', 'wrong-replay', 'exit', 'stderr', 'extra-frontier', 'missing-frontier', 'original-mutation',
                'truncated-frontier', 'replay-mutation', 'prior-frontier-mutation', 'foreign-container', 'cleanup'):
            sql, process, calls, _ = self.oracle(fault)
            with self.subTest(fault=fault), self.assertRaises((AssertionError, subprocess.TimeoutExpired)), patch('sys.stdout', new_callable=io.StringIO) as output:
                self.run_proof(sql, process)
            self.assertEqual('', output.getvalue())
            self.assertTrue(any(command[:2] == ['docker', 'inspect'] for command, _ in calls))
            self.assertTrue(any(command[:3] == ['docker', 'image', 'rm'] for command, _ in calls))
            self.assertFalse(any(command[:2] == ['docker', 'rm'] for command, _ in calls))


if __name__ == '__main__': unittest.main()
