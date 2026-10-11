"""Cold network grammar/ownership controls only; no actual Docker or SQL."""
import importlib.util
import json
import os
from pathlib import Path
from types import SimpleNamespace
import unittest
from unittest.mock import patch
import uuid

spec = importlib.util.spec_from_file_location('cursor_shadow_network_proof', Path(__file__).with_name('smoke-group-frontier-root.py'))
proof = importlib.util.module_from_spec(spec)
spec.loader.exec_module(proof)
reference = proof._load('cursor_shadow_network_reference', 'smoke-group-reference.py')


class CursorShadowNetworkProofTests(unittest.TestCase):
    def setUp(self):
        self.directory = Path(__file__).resolve().parent / 'owned-shadow-runner' / 'aioffice-local'
        self.installation = str(uuid.uuid4())
        self.environment = dict(CI='true', GITHUB_ACTIONS='true', RUNNER_TEMP=str(self.directory.parent))
        self.project = 'aioffice-' + uuid.UUID(self.installation).hex
        self.network = self.project + '_default'
        self.network_id = 'a' * 64; self.container_id = 'b' * 64

    def process(self, fault=None):
        calls = []
        def run(command, **kwargs):
            calls.append(command)
            self.assertTrue(kwargs['capture_output']); self.assertTrue(kwargs['text']); self.assertEqual(10, kwargs['timeout'])
            if command[:3] == ['docker', 'network', 'inspect']:
                self.assertEqual(self.network, command[3])
                value = self.network_id + '|' + self.project + '|default'
                if fault == 'network-id': value = 'a' * 63 + '|' + self.project + '|default'
                if fault == 'network-project': value = self.network_id + '|customer|default'
                if fault == 'network-kind': value = self.network_id + '|' + self.project + '|customer'
                if fault == 'network-extra': value += '|extra'
                return SimpleNamespace(returncode=1 if fault == 'network-exit' else 0, stdout=value + '\n', stderr='private' if fault == 'network-stderr' else '')
            if command[:2] == ['docker', 'ps']:
                self.assertIn('label=com.docker.compose.project=' + self.project, command)
                self.assertIn('label=com.docker.compose.service=sql', command)
                value = self.container_id
                if fault == 'container-multiple': value += '\n' + 'c' * 64
                if fault == 'container-empty': value = ''
                if fault == 'container-short': value = self.container_id[:12]
                return SimpleNamespace(returncode=1 if fault == 'ps-exit' else 0, stdout=value + '\n', stderr='private' if fault == 'ps-stderr' else '')
            if command[:2] == ['docker', 'inspect']:
                self.assertEqual(self.container_id, command[-1]); self.assertIn('container', command)
                self.assertEqual('{{.Id}}|{{.State.Running}}|{{index .Config.Labels "com.docker.compose.project"}}|'
                    '{{index .Config.Labels "com.docker.compose.service"}}|{{with index .NetworkSettings.Networks "' + self.network + '"}}'
                    '{{.NetworkID}}|{{json .Aliases}}{{end}}', command[command.index('--format') + 1])
                values = [self.container_id, 'true', self.project, 'sql', self.network_id,
                    json.dumps(['sql', self.project + '-sql-1'])]
                if fault == 'wrong-id': values[0] = 'c' * 64
                if fault == 'not-running': values[1] = 'false'
                if fault == 'wrong-project': values[2] = 'customer'
                if fault == 'wrong-service': values[3] = 'core'
                if fault == 'wrong-network': values[4] = 'c' * 64
                if fault == 'alias-missing': values[5] = '["customer"]'
                if fault == 'alias-duplicate': values[5] = '["sql","sql"]'
                if fault == 'alias-malformed': values[5] = '["sql","private;value"]'
                if fault == 'alias-overflow': values[5] = json.dumps(['sql'] + ['a' + str(x) for x in range(8)])
                if fault == 'alias-null': values[5] = 'null'
                if fault == 'alias-object': values[5] = '{"sql":true}'
                if fault == 'alias-string': values[5] = '"sql"'
                if fault == 'alias-empty': values[5] = '[]'
                if fault == 'alias-nonstring': values[5] = '["sql",true]'
                if fault == 'alias-nested': values[5] = '["sql",["nested"]]'
                if fault == 'alias-invalid-json': values[5] = '["sql",]'
                if fault == 'alias-trailing-json': values[5] = '["sql"][]'
                if fault == 'alias-old-csv': values[5] = 'sql,' + self.project + '-sql-1'
                if fault == 'alias-observation-overflow': values[5] = json.dumps(['sql', 'a' * 2048])
                if fault == 'alias-too-long': values[5] = json.dumps(['sql', 'a' * 129])
                if fault == 'alias-too-deep': values[5] = '[' * 1000 + '0' + ']' * 1000
                if fault == 'alias-eight': values[5] = json.dumps(['sql'] + ['a' + str(x) for x in range(7)])
                if fault == 'container-extra': values.append('extra')
                return SimpleNamespace(returncode=1 if fault == 'inspect-exit' else 0, stdout='|'.join(values) + '\n', stderr='private' if fault == 'inspect-stderr' else '')
            self.fail('Unexpected process crossed the closed inspection boundary')
        return run, calls

    def verify(self):
        proof.require_shadow_sql_network(directory=self.directory, api='http://127.0.0.1:8080',
            installation=self.installation, reference=reference)

    def test_owned_network_and_exact_unique_running_sql_container_require_all_three_closed_observations(self):
        process, calls = self.process()
        with patch.dict(os.environ, self.environment, clear=True), patch.object(proof.subprocess, 'run', process): self.verify()
        self.assertEqual(3, len(calls))
        self.assertTrue(all('.Config.Env' not in ' '.join(c) and '.Config' != ' '.join(c) for c in calls))

    def test_unowned_guard_precedes_installation_docker_and_operator_access(self):
        def forbidden(*args, **kwargs): self.fail('Unowned shadow inspected a resource')
        for missing in ('CI', 'GITHUB_ACTIONS', 'RUNNER_TEMP'):
            with self.subTest(missing=missing), patch.dict(os.environ, {**self.environment, missing: ''}, clear=True), \
                    patch.object(proof.subprocess, 'run', forbidden), self.assertRaises(RuntimeError): self.verify()

    def test_invalid_installation_refuses_before_any_process(self):
        def forbidden(*args, **kwargs): self.fail('Invalid installation inspected a resource')
        for invalid in ('', str(uuid.UUID(int=0)), self.installation.upper(), 'private;value'):
            with self.subTest(invalid=invalid), patch.dict(os.environ, self.environment, clear=True), patch.object(proof.subprocess, 'run', forbidden), \
                    self.assertRaises((AssertionError, ValueError)):
                proof.require_shadow_sql_network(directory=self.directory, api='http://127.0.0.1:8080', installation=invalid, reference=reference)

    def test_exact_eight_json_aliases_remain_valid_at_closed_capacity(self):
        process, calls = self.process('alias-eight')
        with patch.dict(os.environ, self.environment, clear=True), patch.object(proof.subprocess, 'run', process): self.verify()
        self.assertEqual(3, len(calls))

    def test_every_malformed_foreign_absent_partial_or_diagnostic_observation_refuses(self):
        faults = ('network-id','network-project','network-kind','network-extra','network-exit','network-stderr',
            'container-multiple','container-empty','container-short','ps-exit','ps-stderr','wrong-id','not-running',
            'wrong-project','wrong-service','wrong-network','alias-missing','alias-duplicate','alias-malformed',
            'alias-overflow','alias-null','alias-object','alias-string','alias-empty','alias-nonstring','alias-nested',
            'alias-invalid-json','alias-trailing-json','alias-old-csv','alias-observation-overflow','alias-too-long',
            'alias-too-deep','container-extra','inspect-exit','inspect-stderr')
        for fault in faults:
            process, _ = self.process(fault)
            with self.subTest(fault=fault), patch.dict(os.environ, self.environment, clear=True), \
                    patch.object(proof.subprocess, 'run', process), self.assertRaises(AssertionError): self.verify()


if __name__ == '__main__': unittest.main()
