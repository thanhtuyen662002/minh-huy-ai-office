"""Cold driver controls. Synthetic process/SQL responses do not qualify a mutant run."""
import importlib.util
import os
from pathlib import Path
from types import SimpleNamespace
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('guard_counterfactual', Path(__file__).with_name('smoke-group-accepted-guard-counterfactual.py'))
proof = importlib.util.module_from_spec(spec); spec.loader.exec_module(proof)


class Controls(unittest.TestCase):
    def run_control(self, fault=None):
        calls = []; unchanged = []; cleaned = []
        source = '00000000-0000-0000-0000-000000000005'
        ids = [f'00000000-0000-0000-0000-{n:012d}' for n in range(1, 8)]
        production = Path('src/Platform.Persistence/GroupModelAttemptJournalStore.Execution.cs').read_bytes()
        def process(command, **kwargs):
            calls.append(command)
            if command[:2] == ['docker', 'build']:
                text = Path(command[3]).read_text(encoding='utf-8')
                self.assertEqual(1, text.count("RUN sed -i 's/if (accepted) throw Unavailable();/if (accepted) { }/'"))
                return SimpleNamespace(returncode=1 if fault == 'build' else 0, stdout='', stderr='')
            if command[:3] == ['docker', 'image', 'rm']:
                return SimpleNamespace(returncode=1 if fault == 'image-cleanup' else 0, stdout='', stderr='')
            self.assertEqual(['docker', 'run'], command[:2])
            mode = command[-1]
            if mode != 'admission-original-commit': return SimpleNamespace(returncode=0, stdout=mode, stderr='')
            lines = [proof.FAILURE, 'FAIL owned reference runtime admission-original-commit']
            if fault == 'generic-refusal': lines[0] = lines[0].replace('accepted-preflight-committed', 'invariant')
            if fault == 'missing-query': lines[0] = lines[0].replace('accepted-preflight-committed', 'other')
            if fault == 'wrong-phase': lines[0] = lines[0].replace('accepted-transaction', 'current')
            if fault == 'extra-output': lines.append('PRIVATE_CANARY')
            return SimpleNamespace(returncode=0 if fault == 'mutant-succeeded' else 1, stdout='\n'.join(lines), stderr='PRIVATE_CANARY' if fault == 'stderr' else '')
        def sql(query):
            if 'HASHBYTES' in query: return 'A' * 64
            if 'GroupModelAttemptFailures' in query or 'GroupCustomerRequests' in query: return '0'
            if fault == 'missing-result' and 'GroupModelAttemptResults' in query: return '0'
            return '1'
        def cleanup(**kwargs):
            cleaned.append('container')
            if fault == 'container-cleanup': raise AssertionError('cleanup')
        reference = SimpleNamespace(require_owned=lambda *args: None, require_source_key=lambda key: None,
            require_reference_result=lambda result, mode, expected: self.assertEqual(0, result.returncode))
        automatic = SimpleNamespace(canonical=lambda value: value, SOURCE_TABLES=(('GroupMessages', 'Id'),), close_owned_container=cleanup)
        args = dict(directory='owned', api='http://127.0.0.1', installation=ids[0], tenant=ids[1], company=ids[2], service=ids[3],
            password='A' * 40, sql=sql, prepare_source=lambda: (source, ids[5:7], 'synthetic-key'), reference=reference,
            automatic=automatic, runtime_lines={m: m for m in ('admission-original-prepare', 'admission-original-expiry')},
            assert_unchanged=lambda: unchanged.append(True))
        with patch.object(proof.subprocess, 'run', process), patch.dict(os.environ, {}, clear=True):
            if fault:
                with self.assertRaises(AssertionError): proof.verify(**args)
            else: self.assertEqual(proof.SUMMARY, proof.verify(**args))
        self.assertEqual(production, Path('src/Platform.Persistence/GroupModelAttemptJournalStore.Execution.cs').read_bytes())
        self.assertEqual(['container'], cleaned); self.assertGreaterEqual(len(unchanged), 2)
        self.assertEqual(['docker', 'image', 'rm'], calls[-1][:3])
        return calls

    def test_exact_committed_preflight_failure_and_two_cleanups_required(self):
        calls = self.run_control()
        self.assertEqual(['admission-original-prepare', 'admission-original-expiry', 'admission-original-commit'],
            [c[-1] for c in calls if c[:2] == ['docker', 'run']])

    def test_generic_missing_query_wrong_phase_extra_or_success_cannot_qualify(self):
        for fault in ('generic-refusal', 'missing-query', 'wrong-phase', 'extra-output', 'mutant-succeeded', 'stderr', 'missing-result'):
            with self.subTest(fault=fault): self.run_control(fault)

    def test_build_and_cleanup_failures_never_qualify(self):
        for fault in ('build', 'container-cleanup', 'image-cleanup'):
            with self.subTest(fault=fault): self.run_control(fault)


if __name__ == '__main__': unittest.main()
