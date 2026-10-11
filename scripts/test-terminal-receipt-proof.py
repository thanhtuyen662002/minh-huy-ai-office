"""Cold controls for the owned native receipt phase; no actual SQL/model claim."""
import copy
import importlib.util
import io
import json
import os
from pathlib import Path
from types import SimpleNamespace
import unittest
from unittest.mock import patch
import uuid

spec = importlib.util.spec_from_file_location("terminal_proof", Path(__file__).with_name("smoke-group-terminal-receipts.py"))
proof = importlib.util.module_from_spec(spec)
spec.loader.exec_module(proof)


class TerminalReceiptProofTests(unittest.TestCase):
    def setUp(self):
        self.tenant, self.company, self.service, self.installation = (str(uuid.uuid4()) for _ in range(4))
        self.rows = [{"tenantId": self.tenant, "companyId": self.company, "serviceId": self.service,
            "sourceId": str(uuid.uuid4()), "eventId": str(uuid.uuid4()), "batchId": str(uuid.uuid4())} for _ in range(4)]
        self.directory = (Path(__file__).resolve().parent/'owned-private-runner'/'aioffice-local')
        self.environment = {"CI": "true", "GITHUB_ACTIONS": "true", "RUNNER_TEMP": str(self.directory.parent)}
        self.manifest = {"AIOFFICE_TENANT_ID": self.tenant, "AIOFFICE_COMPANY_ID": self.company,
            "AIOFFICE_INSTALLATION_ID": self.installation, "AIOFFICE_RUNTIME_PASSWORD": "P"*40}

    def test_fixture_parser_requires_exact_four_closed_unique_scopes(self):
        self.assertEqual(self.rows, proof.require_fixtures(json.dumps(self.rows), self.tenant, self.company))
        cases = []
        for count in (0, 1, 3, 5): cases.append(self.rows[:count] if count < 4 else self.rows+[self.rows[0]])
        for field in ("sourceId", "eventId", "batchId"):
            value = copy.deepcopy(self.rows); value[1][field] = value[0][field]; cases.append(value)
        for fault in ("foreign", "empty", "extra", "uppercase", "number", "missing"):
            value = copy.deepcopy(self.rows)
            if fault == "foreign": value[0]["companyId"] = str(uuid.uuid4())
            elif fault == "empty": value[0]["serviceId"] = str(uuid.UUID(int=0))
            elif fault == "extra": value[0]["credential"] = "secret"
            elif fault == "uppercase": value[0]["sourceId"] = "abcdefab-1234-4321-abcd-abcdefabcdef".upper()
            elif fault == "number": value[0]["sourceId"] = 1
            else: del value[0]["batchId"]
            cases.append(value)
        for value in cases:
            with self.subTest(value=value), self.assertRaises(AssertionError):
                proof.require_fixtures(json.dumps(value), self.tenant, self.company)
        duplicated = json.dumps(self.rows).replace('"tenantId":', '"tenantId":"'+self.tenant+'","tenantId":', 1)
        with self.assertRaises(AssertionError): proof.require_fixtures(duplicated, self.tenant, self.company)
        with self.assertRaises(AssertionError): proof.require_fixtures(' '*4097, self.tenant, self.company)

    def test_guard_precedes_configuration_sql_and_process(self):
        def forbidden(*args, **kwargs): self.fail('Unowned proof touched SQL/process')
        for invalid in ({"CI": "false"}, {"GITHUB_ACTIONS": "false"}, {"RUNNER_TEMP": ""}):
            with patch.dict(os.environ, {**self.environment, **invalid}, clear=True), patch.object(proof.subprocess, 'run', forbidden), self.assertRaises(RuntimeError):
                proof.verify(directory=self.directory, api='http://127.0.0.1:8080', manifest=None, sql=forbidden)

    def oracle(self, fault=None):
        phase = 0; calls = []; queries = []
        def sql(query):
            queries.append(query)
            if query.startswith('SELECT CONVERT(varchar(40)'):
                self.assertIn('SELECT TOP(5)', query)
                self.assertNotIn('FOR JSON', query)
                text = self.frames(self.rows)
                return text[:-1] if fault == 'truncated-fixture' else text
            if query.startswith('SET NOCOUNT ON;'):
                count = query.count('SELECT CONCAT')
                return '\n'.join(str(i)+':'+('B' if fault == 'original-mutation' and phase else 'A')*64 for i in range(count))
            if query.startswith('SELECT COUNT(*) FROM aioffice.GroupBatchTerminalReceipts'):
                return str(phase + (1 if fault == 'extra-receipt' and phase else -1 if fault == 'missing-receipt' and phase else 0))
            self.fail('Unexpected SQL shape')
        def process(command, **kwargs):
            nonlocal phase
            calls.append((command, kwargs))
            if command[:2] == ['docker', 'build']:
                return SimpleNamespace(returncode=1 if fault == 'build' else 0, stdout='', stderr='')
            if command[:2] == ['docker', 'run']:
                phase += 1
                stdout = proof.SUCCESS+'\n'
                if fault == 'missing-success': stdout = ''
                if fault == 'duplicate-success': stdout += stdout
                if fault == 'wrong-success': stdout = 'PASS wrong original fixture\n'
                return SimpleNamespace(returncode=1 if fault == 'exit' else 0, stdout=stdout, stderr='private detail' if fault == 'stderr' else '')
            if command[:2] == ['docker', 'inspect']:
                return SimpleNamespace(returncode=1, stdout='', stderr='Error: No such object: '+command[-1])
            if command[:3] == ['docker', 'image', 'rm']:
                return SimpleNamespace(returncode=1 if fault == 'cleanup' else 0, stdout='', stderr='')
            self.fail('Unexpected process')
        return sql, process, calls, queries

    def test_success_requires_four_actual_phase_results_full_original_snapshots_and_closed_children(self):
        sql, process, calls, queries = self.oracle()
        with patch.dict(os.environ, self.environment, clear=True), patch.object(proof.subprocess, 'run', process), patch('sys.stdout', new_callable=io.StringIO) as output:
            proof.verify(directory=self.directory, api='http://127.0.0.1:8080', manifest=self.manifest, sql=sql)
        self.assertEqual(proof.SUMMARY+'\n', output.getvalue())
        children = [(command, arguments) for command, arguments in calls if command[:2] == ['docker', 'run']]
        self.assertEqual(4, len(children))
        for command, arguments in children:
            self.assertEqual('terminal-commit', command[-1]); self.assertEqual(170, arguments['timeout'])
            self.assertIn('--read-only', command); self.assertIn('--cap-drop', command); self.assertIn('no-new-privileges:true', command)
            self.assertNotIn(self.manifest['AIOFFICE_RUNTIME_PASSWORD'], ' '.join(command)+arguments['input'])
            self.assertEqual(set(self.rows[0])-{'batchId'}, set(json.loads(arguments['input'])))
        snapshots = [query for query in queries if query.startswith('SET NOCOUNT ON;')]
        self.assertEqual(5, len(snapshots))
        self.assertTrue(all('GroupWorkRawDispositions' in query and 'GroupTerminalFrontierStates' in query for query in snapshots))

    def test_terminal_failures_never_emit_success_and_always_attempt_owned_cleanup(self):
        for fault in ('build', 'missing-success', 'duplicate-success', 'wrong-success', 'exit', 'stderr', 'original-mutation', 'missing-receipt', 'extra-receipt', 'cleanup'):
            sql, process, calls, _ = self.oracle(fault)
            with self.subTest(fault=fault), patch.dict(os.environ, self.environment, clear=True), patch.object(proof.subprocess, 'run', process), patch('sys.stdout', new_callable=io.StringIO) as output, self.assertRaises(AssertionError):
                proof.verify(directory=self.directory, api='http://127.0.0.1:8080', manifest=self.manifest, sql=sql)
            self.assertEqual('', output.getvalue())
            self.assertTrue(any(command[:2] == ['docker', 'inspect'] for command, _ in calls))
            self.assertTrue(any(command[:3] == ['docker', 'image', 'rm'] for command, _ in calls))

    @staticmethod
    def frames(rows):
        names = ('tenantId', 'companyId', 'serviceId', 'sourceId', 'eventId', 'batchId')
        return '\n'.join(f'{row + 1}:{field + 1}:{values[name]}' for row, values in enumerate(rows)
            for field, name in enumerate(names))

    def test_fixed_frames_fit_both_default_sqlcmd_widths_and_preserve_all_fixture_values(self):
        text = self.frames(self.rows)
        self.assertEqual(24, len(text.splitlines()))
        self.assertTrue(all(len(line.encode('ascii')) == 40 for line in text.splitlines()))
        self.assertEqual(self.rows, proof.require_fixture_frames(text, self.tenant, self.company))
        self.assertEqual(self.rows, proof.require_fixture_frames(text.replace('\n', '\r\n'), self.tenant, self.company))
        # The old single large JSON cell truncates under documented default256.
        with self.assertRaises(json.JSONDecodeError): json.loads(json.dumps(self.rows)[:256])
        with self.assertRaises(AssertionError): proof.require_fixture_frames(json.dumps(self.rows)[:256], self.tenant, self.company)

    def test_missing_extra_reordered_duplicate_malformed_and_foreign_frames_fail_before_children(self):
        text = self.frames(self.rows); lines = text.splitlines()
        cases = [text[:-1], '\n'.join(lines[:-1]), '\n'.join([*lines, *lines[:6]]),
            '\n'.join(reversed(lines)), '\n'.join([lines[0], *lines[:-1]]),
            text.replace('1:1:', '1:2:', 1), text.replace('1:1:', '0:1:', 1), text.replace('1:1:', '1|1:', 1),
            ' '*4097, text.replace(self.tenant, str(uuid.uuid4()), 1)]
        for value in cases:
            with self.subTest(value=value[:50]), self.assertRaises(AssertionError):
                proof.require_fixture_frames(value, self.tenant, self.company)
        for field in ('sourceId', 'eventId', 'batchId'):
            value = copy.deepcopy(self.rows); value[1][field] = value[0][field]
            with self.subTest(field=field), self.assertRaises(AssertionError):
                proof.require_fixture_frames(self.frames(value), self.tenant, self.company)
        sql, process, calls, _ = self.oracle('truncated-fixture')
        with patch.dict(os.environ, self.environment, clear=True), patch.object(proof.subprocess, 'run', process), \
                patch('sys.stdout', new_callable=io.StringIO) as output, self.assertRaises(AssertionError):
            proof.verify(directory=self.directory, api='http://127.0.0.1:8080', manifest=self.manifest, sql=sql)
        self.assertEqual([], calls); self.assertEqual('', output.getvalue())


if __name__ == '__main__': unittest.main()
