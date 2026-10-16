"""Cold adversarial controls only; no actual SQL, provider or model qualification."""
import base64
import importlib.util
import io
import os
from pathlib import Path
from types import SimpleNamespace
import unittest
from unittest.mock import patch
import uuid


def module(name, file):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(file))
    value = importlib.util.module_from_spec(spec); spec.loader.exec_module(value)
    return value


reference = module('first_expiry_reference', 'smoke-group-reference.py')
automatic = module('first_expiry_automatic', 'smoke-group-automatic-notes.py')
proof = module('first_expiry_proof', 'smoke-group-result-expiry.py')


class FirstSharedExpiryProofTests(unittest.TestCase):
    owned = Path('/owned/aioffice-local')
    environment = {'CI':'true', 'GITHUB_ACTIONS':'true', 'RUNNER_TEMP':'/owned'}

    def oracle(self, fault=None):
        tenant, company, service, old1, old2, account1, account2, source, account, installation = [str(uuid.uuid4()) for _ in range(10)]
        phase = 'before'; calls = []; queries = []
        events = [str(uuid.uuid4()), str(uuid.uuid4())]

        def prepare():
            return (old1 if fault == 'source-collision' else source), events, base64.b64encode(b'k'*32).decode('ascii')

        def sql(query):
            queries.append(query)
            if query.startswith('SELECT LOWER(CONVERT(char(36),Id)) FROM aioffice.GroupBindings'): return old1+'\n'+old2
            if query.startswith('SELECT LOWER(CONVERT(char(36),Id)) FROM aioffice.GroupConnectorAccounts'): return account1+'\n'+account2
            if query.startswith('SELECT LOWER(CONVERT(char(36),ConnectorAccountId))'): return account1 if fault == 'account-collision' else account
            if query.startswith('SET NOCOUNT ON;'):
                drift = fault == 'retained' and phase != 'before' or fault == 'retained:'+phase
                return '\n'.join(f'{index:02d}|'+('B' if drift and index == 0 else 'A')*64 for index in range(34))
            if query.startswith('SELECT CONVERT(varchar(64),HASHBYTES'):
                if 'BindingId IN' in query:
                    if fault == 'old-result:'+phase and 'FROM aioffice.GroupModelAttemptResults ' in query: return 'B'*64
                    return 'A'*64
                if fault == 'new-result' and phase == 'attempt-result-shared-expiry' and 'FROM aioffice.GroupModelAttemptResults ' in query: return 'B'*64
                if fault == 'other-claim-field' and phase == 'attempt-result-shared-expiry' and 'FROM aioffice.GroupBatchClaimStates ' in query and 'SELECT * ' not in query: return 'B'*64
                if fault == 'registry:'+phase and 'FROM aioffice.GroupListenerLeases ' in query: return 'B'*64
                if fault == 'new-cleanup:'+phase and 'FROM aioffice.GroupModelAttemptStarts ' in query: return 'B'*64
                # Model only the authorized witness delta in the complete hash.
                if 'FROM aioffice.GroupBatchClaimStates ' in query and 'SELECT * ' in query and phase in ('attempt-result-shared-expiry','container-cleanup','image-cleanup'): return 'B'*64
                return 'A'*64
            if 'SELECT CONCAT' in query: return '0|0|0|0' if phase == 'attempt-result-prepare' else ('1|1|1|2' if fault == 'replacement' else '1|1|1|1')
            if 'FROM aioffice.GroupSourceStates ' in query: return '1'
            if 'FROM aioffice.GroupBatchClaimStates ' in query:
                return '0' if fault == 'pre-retired' and phase == 'attempt-result-commit' or fault == 'missing-witness' and phase == 'attempt-result-shared-expiry' else '1'
            if 'FROM aioffice.GroupAutomaticInputPlans ' in query: return '0' if fault == 'plan-hash' else '1'
            if 'FROM aioffice.GroupModelAttemptResults ' in query: return '0' if fault == 'result-hash' else '1'
            if query.startswith('SELECT COUNT(*)'): return '1' if fault == 'effect' and phase == 'attempt-result-shared-expiry' else '0'
            self.fail('Unexpected owned expiry SQL')

        def process(command, **kwargs):
            nonlocal phase
            calls.append((command, kwargs))
            if command[:2] == ['docker','build']: return SimpleNamespace(returncode=0, stdout='', stderr='')
            if command[:2] == ['docker','inspect']:
                phase = 'container-cleanup'
                if fault == 'container-error': raise RuntimeError('owned cleanup control')
                return SimpleNamespace(returncode=1, stdout='', stderr='Error: No such object: '+command[-1])
            if command[:3] == ['docker','image','rm']:
                phase = 'image-cleanup'
                return SimpleNamespace(returncode=1 if fault == 'image-error' else 0, stdout='', stderr='')
            phase = command[-1]
            lines = proof.PREPARE_LINES if phase == 'attempt-result-prepare' else [proof.RUNTIME_LINES[phase]]
            if fault == 'duplicate-marker' and phase == 'attempt-result-shared-expiry': lines = lines+lines
            if fault == 'missing-prepare' and phase == 'attempt-result-prepare': lines = lines[:1]
            return SimpleNamespace(returncode=1 if fault == 'child-error' and phase == 'attempt-result-shared-expiry' else 0,
                stdout='\n'.join(lines)+'\n'+('PRIVATE_SQL\n' if fault == 'extra-output' else ''),
                stderr='PRIVATE_SQL' if fault == 'stderr' else '')

        args = dict(directory=self.owned, api='http://127.0.0.1:8080', manifest={
            'AIOFFICE_INSTALLATION_ID':installation, 'AIOFFICE_RUNTIME_PASSWORD':'p'*32}, tenant=tenant, company=company,
            service=service, sql=sql, prepare_source=prepare, reference=reference, automatic=automatic)
        return args, process, calls, queries

    def run_oracle(self, fault=None):
        args, process, calls, queries = self.oracle(fault)
        with patch.dict(os.environ, self.environment, clear=True), patch.object(proof.subprocess, 'run', process), \
                patch('sys.stdout', new_callable=io.StringIO) as output:
            if fault:
                with self.assertRaises((AssertionError, RuntimeError)) as error: proof.verify(**args)
                self.assertNotIn('PRIVATE_SQL', str(error.exception)); self.assertEqual('', output.getvalue())
            else:
                proof.verify(**args); self.assertEqual([proof.SUMMARY], output.getvalue().splitlines())
        self.assertEqual(2, sum(command[:2] == ['docker','inspect'] for command, _ in calls))
        self.assertEqual(1, sum(command[:3] == ['docker','image','rm'] for command, _ in calls))
        return args, calls, queries

    def test_exact_oracle_keeps_three_processes_and_no_command_or_input_secrets(self):
        args, calls, queries = self.run_oracle()
        runs = [(command, kwargs) for command, kwargs in calls if command[:2] == ['docker','run']]
        self.assertEqual(['attempt-result-prepare','attempt-result-commit','attempt-result-shared-expiry'], [command[-1] for command,_ in runs])
        for command, kwargs in runs:
            self.assertEqual(170, kwargs['timeout']); self.assertIn('--read-only', command)
            self.assertNotIn(args['manifest']['AIOFFICE_RUNTIME_PASSWORD'], ' '.join(command)+kwargs['input'])
            self.assertNotIn(kwargs['env']['AIOFFICE_GROUP_REFERENCE_PROOF_SOURCE_KEY'], ' '.join(command)+kwargs['input'])
        self.assertFalse(any(query.startswith(('UPDATE','DELETE','INSERT')) for query in queries))

    def test_prior_result_scopes_stay_frozen_through_children_and_both_cleanup_gates(self):
        for phase in ('attempt-result-prepare','attempt-result-commit','attempt-result-shared-expiry','container-cleanup','image-cleanup'):
            with self.subTest(phase=phase): self.run_oracle('old-result:'+phase)
        for phase in ('container-cleanup','image-cleanup'):
            with self.subTest(phase=phase): self.run_oracle('retained:'+phase)

    def test_new_registry_and_completed_graph_stay_frozen_through_cleanup(self):
        for phase in ('attempt-result-prepare','attempt-result-shared-expiry','container-cleanup','image-cleanup'):
            with self.subTest(phase=phase): self.run_oracle('registry:'+phase)
        for phase in ('container-cleanup','image-cleanup'):
            with self.subTest(phase=phase): self.run_oracle('new-cleanup:'+phase)

    def test_prior_witness_or_replacement_cannot_qualify_the_first_witness(self):
        _, calls, _ = self.run_oracle('pre-retired')
        self.assertNotIn('attempt-result-shared-expiry', [command[-1] for command,_ in calls if command[:2] == ['docker','run']])
        self.run_oracle('replacement'); self.run_oracle('missing-witness')

    def test_collisions_or_changed_original_graph_never_print_summary(self):
        for fault in ('source-collision','account-collision','retained','new-result','other-claim-field','plan-hash','result-hash','effect'):
            with self.subTest(fault=fault): self.run_oracle(fault)

    def test_failed_or_partial_child_and_cleanup_preserve_failure(self):
        for fault in ('child-error','duplicate-marker','missing-prepare','extra-output','stderr','container-error','image-error'):
            with self.subTest(fault=fault): self.run_oracle(fault)

    def test_unowned_or_invalid_identity_is_refused_before_resources(self):
        args, _, _, _ = self.oracle()
        def forbidden(*args, **kwargs): self.fail('Unowned expiry reached a resource')
        args.update(sql=forbidden, prepare_source=forbidden)
        for invalid in ({'CI':'false'}, {'RUNNER_TEMP':'/foreign'}):
            with patch.dict(os.environ, {**self.environment,**invalid}, clear=True), patch.object(proof.subprocess,'run',forbidden), self.assertRaises(RuntimeError):
                proof.verify(**args)
        with patch.dict(os.environ,self.environment,clear=True), self.assertRaises(ValueError): proof.verify(**{**args,'tenant':'PRIVATE_SQL'})

    def test_closed_diagnostics_hide_exception_details(self):
        for phase in ('shared-expiry-replay','shared-expiry-preparation','shared-expiry-read','shared-expiry-evidence','shared-expiry-readback','shared-expiry-rewind'):
            child = SimpleNamespace(returncode=1, stdout='FAIL owned attempt result runtime phase-'+phase+' kind-other\nFAIL owned reference runtime attempt-result-shared-expiry\n', stderr='')
            with self.assertRaisesRegex(AssertionError, 'phase='+phase+'; kind=other'):
                reference.require_reference_result(child,'attempt-result-shared-expiry',[])
        child.stdout = 'PRIVATE_SQL\nFAIL owned reference runtime attempt-result-shared-expiry\n'
        with self.assertRaises(AssertionError) as error: reference.require_reference_result(child,'attempt-result-shared-expiry',[])
        self.assertNotIn('PRIVATE_SQL',str(error.exception))

    def test_runtime_marker_and_slot16_registration_are_explicit(self):
        root = Path(__file__).parent.parent
        text = (root/'tests/GroupReference.RuntimeProof/GroupModelAttemptResultSharedExpiryRuntimeProof.cs').read_text(encoding='utf-8')
        self.assertIn(proof.RUNTIME_LINES['attempt-result-shared-expiry'],text)
        program = (root/'tests/GroupReference.RuntimeProof/Program.cs').read_text(encoding='utf-8')
        self.assertEqual(2,program.count('"attempt-result-shared-expiry"'))
        ingress = (root/'scripts/smoke-group-ingress.py').read_text(encoding='utf-8')
        self.assertIn('enroll_owned_source(source_id, 16)',ingress)
        self.assertIn('16: "OWNED_FIRST_SHARED_EXPIRY_GROUP_CONTENT_KEY"',ingress)
        self.assertIn('assert result_slot in (12, 13)',ingress)


if __name__ == '__main__': unittest.main()
