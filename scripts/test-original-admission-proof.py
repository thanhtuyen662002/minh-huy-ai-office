"""Inert orchestrator fault injection; never SQL/provider qualification."""
import base64
import importlib.util
import io
import os
from pathlib import Path
from types import SimpleNamespace
import unittest
from unittest.mock import patch
import uuid


def load(name, filename):
    spec = importlib.util.spec_from_file_location(name,Path(__file__).with_name(filename))
    module = importlib.util.module_from_spec(spec); spec.loader.exec_module(module); return module


proof = load('admission_proof_controls','smoke-group-original-admission.py')
reference = load('admission_reference_controls','smoke-group-reference.py')
automatic = load('admission_automatic_controls','smoke-group-automatic-notes.py')


class OriginalAdmissionProofTests(unittest.TestCase):
    environment = {'CI':'true','GITHUB_ACTIONS':'true','RUNNER_TEMP':'/owned'}

    def oracle(self, fault=None):
        tenant,company,service,source,account,old_source,old_account,installation = (str(uuid.uuid4()) for _ in range(8))
        events = [str(uuid.uuid4()),str(uuid.uuid4())]; batch = str(uuid.uuid4())
        phase = 'before'; denied = False; permissions_touched = []; calls = []

        def sql(query):
            nonlocal denied
            if query.startswith('/* owned-original-admission-digest-batch */'):
                statements=query.splitlines()[1:]
                rows=[f'{i:02d}|'+sql(statement) for i,statement in enumerate(statements)]
                if phase != 'before':
                    if fault == 'digest-count':rows.pop()
                    if fault == 'digest-index':rows[0]='99'+rows[0][2:]
                    if fault == 'digest-hash':rows[0]=rows[0].lower()
                return '\n'.join(rows)
            if query.startswith('DENY SELECT ON OBJECT::'):
                denied = True; permissions_touched.append('deny'); return ''
            if query.startswith('REVOKE SELECT ON OBJECT::'):
                permissions_touched.append('restore')
                if fault == 'restore': raise AssertionError('owned SELECT restoration failed')
                denied = False; return ''
            if 'FROM sys.database_permissions' in query: return ('B' if denied else 'A')*64
            if query.startswith('SELECT LOWER(CONVERT(char(36),Id)) FROM aioffice.GroupBindings'): return old_source
            if query.startswith('SELECT LOWER(CONVERT(char(36),Id)) FROM aioffice.GroupConnectorAccounts'): return old_account
            if query.startswith('SELECT LOWER(CONVERT(char(36),ConnectorAccountId))'): return account
            if query.startswith('SELECT LOWER(CONVERT(char(36),BatchId)) FROM aioffice.GroupOriginalAdmissionSidecars'): return batch
            if 'HASHBYTES' in query and not query.startswith('SELECT COUNT(*)'):
                if query.startswith('SET NOCOUNT ON;'):
                    return '\n'.join(f'{i:02d}|'+('B' if fault == 'retained' and phase != 'before' else 'A')*64 for i in range(34))
                table = query.split('FROM aioffice.',1)[1].split()[0]
                own = source in query and 'BindingId IN' not in query
                if own and fault == 'batch-original-changed' and phase == 'restored-batch-commit' and table == 'GroupAutomaticInputPlans': return 'B'*64
                if own and fault == 'batch-replay-changed' and phase == 'restored-batch-recover' and table == 'GroupTerminalAdvances': return 'B'*64
                if own and fault == 'sql-read-graph' and phase == 'admission-original-sql-read' and table == 'GroupOriginalAdmissionSidecars': return 'B'*64
                if own and fault in ('plan','sidecar','start','result','companion') and phase == 'admission-original-resume':
                    target = {'plan':'GroupAutomaticInputPlans','sidecar':'GroupOriginalAdmissionSidecars','start':'GroupModelAttemptStarts','result':'GroupModelAttemptResults','companion':'GroupModelAttemptAdmissions'}[fault]
                    if table == target: return 'B'*64
                if own and fault == 'source' and phase == 'admission-original-commit' and table == 'GroupMessages': return 'B'*64
                if own and fault == 'claim' and phase == 'admission-original-recover' and 'AND Epoch=1' in query: return 'B'*64
                if own and fault and fault.startswith('cleanup:') and phase == fault.split(':')[1] and table == 'GroupOriginalAdmissionSidecars': return 'B'*64
                return 'A'*64
            if 'SELECT CONCAT' in query:
                if phase == 'restored-batch-commit': return '0|1|1|1|2|2|1|1|1|1|1|1' if fault == 'batch-counts' else '1|1|1|1|2|2|1|1|1|1|1|1'
                return {'admission-original-prepare':'0|0|0|0|0','admission-original-expiry':'0|0|0|0|1','admission-original-commit':'1|1|1|1|2','admission-original-recover':'1|1|1|1|3','admission-original-resume':'1|1|1|1|3'}[phase]
            if query.startswith('SELECT COUNT(*) FROM aioffice.GroupCustomerRequests') or query.startswith('SELECT COUNT(*) FROM aioffice.GroupModelAttemptFailures'): return '0'
            if query.startswith('SELECT COUNT(*) FROM aioffice.GroupModelAttemptAdmissions'):
                if fault == 'companion-count' and phase == 'admission-original-commit': return '0'
                return '0' if phase in ('admission-original-prepare','admission-original-expiry') else '1'
            if query.startswith('SELECT COUNT(*) FROM aioffice.GroupBatchClaimStates'): return '0' if fault == 'expiry-witness' else '1'
            if query.startswith('SELECT COUNT(*) FROM aioffice.GroupBatchClaimReceipts'): return '4'
            if query.startswith('SELECT COUNT(*) FROM aioffice.GroupOriginalAdmissionSidecars s INNER JOIN'): return '0' if fault == 'row-binding' else '1'
            self.fail('Unexpected SQL shape')

        def process(command, **kwargs):
            nonlocal phase
            calls.append((command,kwargs))
            if command[:2] == ['docker','build']: return SimpleNamespace(returncode=0,stdout='',stderr='')
            if command[:2] == ['docker','inspect']:
                phase = 'container-cleanup'
                return SimpleNamespace(returncode=1,stdout='',stderr='Error: No such object: '+command[-1])
            if command[:3] == ['docker','image','rm']:
                phase = 'image-cleanup'; return SimpleNamespace(returncode=1 if fault == 'image-error' else 0,stdout='',stderr='')
            self.assertEqual(['docker','run'],command[:2]); phase = command[-1]
            if phase in ('admission-original-recover','restored-batch-recover'): self.assertTrue(denied)
            line = proof.BATCH_COMMIT_LINE if phase == 'restored-batch-commit' else proof.BATCH_RECOVER_LINE if phase == 'restored-batch-recover' else proof.JOURNAL_EXPIRY_RUNTIME_LINE if phase == 'admission-original-journal-expiry' else proof.READ_RUNTIME_LINE if phase == 'admission-original-sql-read' else proof.RUNTIME_LINES[phase]
            if fault in ('batch-commit-child','batch-recover-child') and phase == ('restored-batch-commit' if fault == 'batch-commit-child' else 'restored-batch-recover'): return SimpleNamespace(returncode=1,stdout='',stderr='')
            if fault == 'batch-private-output' and phase == 'restored-batch-commit': line += '\nPRIVATE_BATCH_CONTEXT'
            if phase == 'admission-original-commit': line += '\n'+proof.JOURNAL_RUNTIME_LINE
            if fault == 'journal-missing' and phase == 'admission-original-commit': line = proof.RUNTIME_LINES[phase]
            if fault == 'sql-read-child' and phase == 'admission-original-sql-read': return SimpleNamespace(returncode=1,stdout='',stderr='')
            if fault == 'sql-read-output' and phase == 'admission-original-sql-read': line += '\nPRIVATE_READ_BODY'
            if fault == 'child-error' and phase == 'admission-original-recover': return SimpleNamespace(returncode=1,stdout='',stderr='')
            if fault == 'duplicate' and phase == 'admission-original-resume': line += '\n'+line
            if fault == 'private-output' and phase == 'admission-original-commit': line += '\nPRIVATE_DIAGNOSTIC'
            return SimpleNamespace(returncode=0,stdout=line+'\n',stderr='')

        args = dict(directory=Path('/owned/aioffice-local'),api='http://127.0.0.1:8080',manifest=dict(AIOFFICE_INSTALLATION_ID=installation,AIOFFICE_RUNTIME_PASSWORD='r'*40),
            tenant=tenant,company=company,service=service,sql=sql,prepare_source=lambda:(source,events,base64.b64encode(b'k'*32).decode('ascii')),prepare_counterfactual_source=lambda:(str(uuid.uuid4()),[str(uuid.uuid4()),str(uuid.uuid4())],base64.b64encode(b'm'*32).decode('ascii')),
            reference=reference,automatic=automatic)
        return args,process,calls,permissions_touched

    def run_control(self, fault=None):
        args,process,calls,mutations = self.oracle(fault)
        def corruption_control(**kwargs):
            self.assertIn('batch',kwargs);self.assertTrue(callable(kwargs['run_child']));self.assertTrue(callable(kwargs['assert_unchanged']))
            if fault == 'corruption':raise AssertionError('corruption suite failed')
        def permission_control(**kwargs):
            self.assertIn('batch',kwargs);self.assertTrue(callable(kwargs['run_child']));self.assertTrue(callable(kwargs['assert_unchanged']))
            if fault == 'permission-suite':raise AssertionError('permission suite failed')
        def guard_control(**kwargs):
            self.assertIs(kwargs['prepare_source'],args['prepare_counterfactual_source'])
            self.assertIsNot(kwargs['prepare_source'],args['prepare_source'])
            return proof.guard_counterfactual.SUMMARY
        with patch.dict(os.environ,self.environment,clear=True),patch.object(proof.subprocess,'run',process),patch.object(proof.corruption,'verify',side_effect=corruption_control),patch.object(proof.permission_proof,'verify',side_effect=permission_control),patch.object(proof.guard_counterfactual,'verify',side_effect=guard_control),patch('sys.stdout',new_callable=io.StringIO) as output:
            if fault:
                with self.assertRaises(AssertionError): proof.verify(**args)
                self.assertEqual('',output.getvalue())
            else:
                proof.verify(**args)
                self.assertEqual([*proof.RUNTIME_LINES.values(),proof.SUMMARY,proof.corruption.SUMMARY,proof.permission_proof.SUMMARY,proof.READ_SUMMARY,proof.JOURNAL_SUMMARY,proof.JOURNAL_EXPIRY_RUNTIME_LINE,proof.guard_counterfactual.SUMMARY,proof.BATCH_SUMMARY],output.getvalue().splitlines())
        return args,calls,mutations

    def test_success_restores_select_and_qualifies_only_after_two_cleanup_checks(self):
        args,calls,mutations = self.run_control()
        self.assertEqual(['deny','restore','deny','restore'],mutations)
        self.assertEqual([*proof.RUNTIME_LINES,'admission-original-sql-read','admission-original-resume','admission-original-journal-expiry','restored-batch-commit','restored-batch-recover'],[cmd[-1] for cmd,_ in calls if cmd[:2] == ['docker','run']])
        self.assertEqual(2,sum(cmd[:2] == ['docker','inspect'] for cmd,_ in calls))
        for cmd,kwargs in calls:
            if cmd[:2] != ['docker','run']: continue
            self.assertEqual(170,kwargs['timeout']); self.assertIn('--read-only',cmd)
            self.assertNotIn(args['manifest']['AIOFFICE_RUNTIME_PASSWORD'],' '.join(cmd)+kwargs['input'])
            self.assertNotIn(kwargs['env']['AIOFFICE_GROUP_REFERENCE_PROOF_SOURCE_KEY'],' '.join(cmd)+kwargs['input'])

    def test_restored_completion_refuses_missing_effects_changed_originals_private_output_and_failed_child(self):
        for fault in ('batch-counts','batch-original-changed','batch-private-output','batch-commit-child'):
            with self.subTest(fault=fault):
                _,calls,_ = self.run_control(fault)
                self.assertEqual(2,sum(cmd[:2] == ['docker','inspect'] for cmd,_ in calls))

    def test_terminal_recovery_restores_permission_on_child_failure_and_changed_signed_history(self):
        for fault in ('batch-recover-child','batch-replay-changed'):
            with self.subTest(fault=fault):
                _,calls,mutations = self.run_control(fault)
                self.assertEqual(['deny','restore','deny','restore'],mutations)
                self.assertEqual(2,sum(cmd[:2] == ['docker','inspect'] for cmd,_ in calls))

    def test_child_failure_always_restores_select_and_never_publishes_partial_markers(self):
        _,calls,mutations = self.run_control('child-error');self.assertEqual(['deny','restore'],mutations)
        self.assertFalse(any(cmd[-1] == 'admission-original-resume' for cmd,_ in calls))

    def test_changed_originals_and_scope_or_claim_never_qualify(self):
        for fault in ('plan','sidecar','start','result','companion','companion-count','source','claim','retained','row-binding','expiry-witness'):
            with self.subTest(fault=fault): self.run_control(fault)

    def test_bad_child_output_or_restore_and_cleanup_failures_never_qualify(self):
        for fault in ('duplicate','private-output','journal-missing','restore','image-error','cleanup:container-cleanup','cleanup:image-cleanup'):
            with self.subTest(fault=fault): self.run_control(fault)

    def test_corruption_suite_failure_suppresses_all_markers_and_still_cleans_up(self):
        _,calls,_=self.run_control('corruption')
        self.assertEqual(['docker','image','rm'],calls[-1][0][:3])

    def test_digest_batch_refuses_missing_reordered_or_noncanonical_rows(self):
        for fault in ('digest-count','digest-index','digest-hash'):
            with self.subTest(fault=fault):self.run_control(fault)

    def test_permission_suite_failure_suppresses_all_markers_and_still_cleans_up(self):
        _,calls,_=self.run_control('permission-suite')
        self.assertEqual(['docker','image','rm'],calls[-1][0][:3])

    def test_sql_read_child_and_extra_output_never_publish_any_partial_marker(self):
        for fault in ('sql-read-child','sql-read-output','sql-read-graph'):
            with self.subTest(fault=fault):
                _,calls,_=self.run_control(fault)
                self.assertEqual(['docker','image','rm'],calls[-1][0][:3])


if __name__ == '__main__': unittest.main()
