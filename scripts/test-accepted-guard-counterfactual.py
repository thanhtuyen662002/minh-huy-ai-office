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


    def test_diagnostic_preserves_exact_negative_acceptance(self):
        valid = SimpleNamespace(returncode=1, stdout=proof.FAILURE+'\nFAIL owned reference runtime admission-original-commit\n', stderr='')
        self.assertIsNone(proof.require_committed_preflight_refusal(valid))
        for code in (0, 2, True, None):
            with self.subTest(code=code):
                with self.assertRaisesRegex(AssertionError, 'did not discriminate'):
                    proof.require_committed_preflight_refusal(SimpleNamespace(returncode=code, stdout=valid.stdout, stderr=''))

    def test_known_child_phase_kind_is_bounded_public_diagnostic_only(self):
        text = 'FAIL owned original admission runtime phase-admitted-execution-accepted-transaction kind-invariant\nFAIL owned reference runtime admission-original-commit'
        with self.assertRaises(AssertionError) as caught:
            proof.require_committed_preflight_refusal(SimpleNamespace(returncode=1, stdout=text, stderr=''))
        self.assertIn('phase-admitted-execution-accepted-transaction-kind-invariant', str(caught.exception))
        self.assertIn('exit=1;lines=2', str(caught.exception))

    def test_private_or_unrecognized_child_output_is_never_echoed(self):
        private = 'PRIVATE_CANARY_TOKEN_DO_NOT_ECHO'
        for text, error in ((private, ''), (proof.FAILURE+'\n'+private, ''), (private*1000, ''), ('', private),
                ('FAIL owned original admission runtime phase-'+private+' kind-invariant\nFAIL owned reference runtime admission-original-commit', '')):
            with self.subTest(size=len(text), stderr=bool(error)):
                with self.assertRaises(AssertionError) as caught:
                    proof.require_committed_preflight_refusal(SimpleNamespace(returncode=1, stdout=text, stderr=error))
                self.assertNotIn(private, str(caught.exception)); self.assertLess(len(str(caught.exception)), 300)

    def test_duplicate_missing_or_wrong_failure_line_is_still_refused(self):
        exact = proof.FAILURE+'\nFAIL owned reference runtime admission-original-commit'
        for text in ('', exact+'\n'+proof.FAILURE, proof.FAILURE, exact.replace('runtime admission-original-commit', 'runtime unknown')):
            with self.subTest(text_length=len(text)):
                with self.assertRaises(AssertionError):
                    proof.require_committed_preflight_refusal(SimpleNamespace(returncode=1, stdout=text, stderr=''))


if __name__ == '__main__': unittest.main()
