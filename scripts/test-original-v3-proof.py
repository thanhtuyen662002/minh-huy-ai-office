"""Model-free driver failure controls; synthetic callbacks are not SQL evidence."""
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
    result = importlib.util.module_from_spec(spec); spec.loader.exec_module(result); return result


proof = module('original_v3_proof','smoke-group-original-v3.py')
reference = module('original_v3_reference','smoke-group-reference.py')
automatic = module('original_v3_automatic','smoke-group-automatic-notes.py')


class OriginalV3ProofTests(unittest.TestCase):
    environment = {'CI':'true','GITHUB_ACTIONS':'true','RUNNER_TEMP':'/owned'}

    def oracle(self, fault=None):
        tenant,company,service,source,account,old_source,old_account,installation = [str(uuid.uuid4()) for _ in range(8)]
        events = [str(uuid.uuid4()) for _ in range(3)]
        first_batch = str(uuid.uuid4()); original_batch = str(uuid.uuid4()); mutations = []
        phase = 'before'; calls = []; queries = []; continued = []
        def prepare():
            return source, events[:2], base64.b64encode(b'k'*32).decode('ascii')
        def followup(value):
            nonlocal phase
            assert value == source and not continued
            continued.append(value); phase = 'followup'
            return events[0] if fault == 'reused-event' else events[2]
        def sql(query):
            queries.append(query)
            if query.startswith('SELECT COUNT(*) FROM aioffice.GroupAutomaticInputPlans') and 'FormatVersion=2;' in query:
                return '0' if fault == 'legacy-missing' else '1'
            if query.startswith('SELECT LOWER(CONVERT(char(36),BatchId)) FROM aioffice.GroupAutomaticInputPlans'):
                return original_batch
            if query.startswith('SET NOCOUNT ON; UPDATE aioffice.GroupAutomaticInputPlans SET FormatVersion='):
                assert source in query and original_batch in query and "AND BatchId='" in query
                changed = '2' if 'SET FormatVersion=2' in query else '3'; mutations.append(changed)
                return '0' if fault == ('mismatch-update' if changed == '2' else 'mismatch-restore') else '1'
            if query.startswith('SELECT LOWER(CONVERT(char(36),Id)) FROM aioffice.GroupBindings'): return old_source
            if query.startswith('SELECT LOWER(CONVERT(char(36),Id)) FROM aioffice.GroupConnectorAccounts'): return old_account
            if query.startswith('SELECT LOWER(CONVERT(char(36),ConnectorAccountId))'): return account
            if query.startswith('SELECT LOWER(CONVERT(char(36),Id)) FROM aioffice.GroupBatchAllocations'):
                assert f"OperationId='{events[0]}'" in query and source in query
                return first_batch
            if 'HASHBYTES' in query and not query.startswith('SELECT COUNT(*)'):
                if query.startswith('SET NOCOUNT ON;'):
                    changed = fault == 'retained' and phase != 'before' or fault == 'retained-container-cleanup' and phase == 'container-cleanup'
                    return '\n'.join(f'{i:02d}|'+('B' if changed else 'A')*64 for i in range(34))
                table = query.split('FROM aioffice.',1)[1].split()[0]
                owned = (source in query or account in query) and 'BindingId IN' not in query
                if owned and fault == 'source-prefix' and phase == 'followup' and table == 'GroupMessages' and 'Id IN (SELECT' in query: return 'B'*64
                if owned and fault == 'notes' and phase == 'followup' and table == 'GroupRequestRevisions': return 'B'*64
                if owned and fault == 'authority' and phase == 'followup' and table == 'GroupServiceGrants': return 'B'*64
                if owned and fault == 'binding' and phase == 'followup' and table == 'GroupBindings': return 'B'*64
                if owned and fault in ('plan','start','result') and phase == 'v3-history-resume':
                    target = dict(plan='GroupAutomaticInputPlans',start='GroupModelAttemptStarts',result='GroupModelAttemptResults')[fault]
                    if table == target: return 'B'*64
                if owned and fault and fault.startswith('original-batch:'):
                    _,point,target = fault.split(':')
                    phases = ('followup','v3-history-commit','v3-history-resume','container-cleanup','image-cleanup')
                    if phase in phases and phases.index(phase) >= phases.index(point) and table == target:
                        return 'B'*64
                if owned and fault and fault.startswith('cleanup:'):
                    _,point,target = fault.split(':')
                    if phase == point and table == target: return 'B'*64
                return 'A'*64
            if 'SELECT CONCAT' in query:
                return '4|2|1|1|1|2|3' if fault == 'extra-effect' and phase == 'v3-history-resume' else {
                    'v3-history-prepare':'4|1|0|0|0|1|1','v3-history-commit':'4|1|1|1|1|2|2',
                    'v3-history-mismatch':'4|1|1|1|1|2|2','v3-history-resume':'4|1|1|1|1|2|3'}[phase]
            if 'GroupSourceStates' in query: return '0' if fault == 'suffix' and phase == 'v3-history-commit' else '1'
            if 'GroupModelAttemptFailures' in query: return '1' if fault == 'failure-row' and phase == 'v3-history-commit' else '0'
            if 'GroupAutomaticInputPlans' in query: return '0' if fault == 'plan-hash' and phase == 'v3-history-commit' else '1'
            if 'GroupModelAttemptResults' in query: return '0' if fault == 'result-hash' and phase == 'v3-history-commit' else '1'
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
            phase = command[-1]; line = proof.RUNTIME_LINES[phase]
            if fault == 'mismatch-output' and phase == 'v3-history-mismatch': line = 'PRIVATE_SQL'
            if fault == 'mismatch-child' and phase == 'v3-history-mismatch': return SimpleNamespace(returncode=1,stdout='',stderr='')
            if fault == 'mismatch-child-exception' and phase == 'v3-history-mismatch': raise AssertionError('primary owned mismatch child failure')
            if fault == 'missing-line' and phase == 'v3-history-commit': line = ''
            if fault == 'duplicate-line' and phase == 'v3-history-resume': line += '\n'+line
            if fault == 'extra-output' and phase == 'v3-history-resume': line += '\nPRIVATE_SQL'
            return SimpleNamespace(returncode=1 if fault == 'child-error' and phase == 'v3-history-commit' else 0,
                stdout=line+'\n',stderr='PRIVATE_SQL' if fault == 'stderr' and phase == 'v3-history-prepare' else '')
        args = dict(directory=Path('/owned/aioffice-local'),api='http://127.0.0.1:8080',manifest={
            'AIOFFICE_INSTALLATION_ID':installation,'AIOFFICE_RUNTIME_PASSWORD':'p'*32},tenant=tenant,company=company,service=service,
            sql=sql,prepare_source=prepare,append_followup=followup,reference=reference,automatic=automatic)
        args["_mutations"] = mutations
        return args,process,calls,queries,continued

    def run_proof(self, fault=None):
        args,process,calls,queries,continued = self.oracle(fault)
        output = io.StringIO()
        with patch.dict(os.environ,self.environment,clear=True),patch.object(proof.subprocess,'run',process),patch('sys.stdout',output):
            proof.verify(**{key:value for key,value in args.items() if key != "_mutations"})
        return args,calls,queries,continued,output.getvalue()

    def test_unowned_denies_before_configuration_callbacks_or_process(self):
        def forbidden(*args,**kwargs): self.fail('Unowned history touched a resource')
        args,_,_,_,_ = self.oracle()
        for invalid in ({'CI':'false'},{'GITHUB_ACTIONS':'false'},{'RUNNER_TEMP':'/foreign'}):
            with self.subTest(invalid=invalid),patch.dict(os.environ,{**self.environment,**invalid},clear=True), \
                    patch.object(proof.subprocess,'run',forbidden),self.assertRaises(RuntimeError):
                proof.verify(**{**{key:value for key,value in args.items() if key != "_mutations"},'manifest':None,'sql':forbidden,'prepare_source':forbidden,'append_followup':forbidden})

    def test_invalid_scope_and_credentials_deny_before_sql(self):
        def forbidden(*args,**kwargs): self.fail('Invalid history touched a resource')
        args,_,_,_,_ = self.oracle()
        for key in ('tenant','company','service'):
            with self.subTest(key=key),patch.dict(os.environ,self.environment,clear=True),self.assertRaises((ValueError,AssertionError)):
                proof.verify(**{**{key:value for key,value in args.items() if key != "_mutations"},key:'PRIVATE_SQL','sql':forbidden})
        with patch.dict(os.environ,self.environment,clear=True),self.assertRaises(AssertionError):
            proof.verify(**{**{key:value for key,value in args.items() if key != "_mutations"},'manifest':{**args['manifest'],'AIOFFICE_RUNTIME_PASSWORD':'invalid'},'sql':forbidden})

    def test_four_separate_children_restore_one_added_field_and_keep_secrets_private(self):
        args,calls,queries,continued,output = self.run_proof()
        self.assertEqual([*proof.RUNTIME_LINES.values(),proof.SUMMARY],output.splitlines()); self.assertEqual(1,len(continued))
        runs = [(cmd,kw) for cmd,kw in calls if cmd[:2] == ['docker','run']]
        self.assertEqual(list(proof.RUNTIME_LINES),[cmd[-1] for cmd,_ in runs])
        for cmd,kw in runs:
            self.assertEqual(170,kw['timeout']); self.assertIn('--read-only',cmd)
            self.assertNotIn(args['manifest']['AIOFFICE_RUNTIME_PASSWORD'],' '.join(cmd)+kw['input'])
            self.assertNotIn(kw['env']['AIOFFICE_GROUP_REFERENCE_PROOF_SOURCE_KEY'],' '.join(cmd)+kw['input'])
        self.assertEqual(['2','3'],args['_mutations'])
        self.assertTrue(any('MessageId IN (SELECT' in query for query in queries))
        self.assertEqual(2,sum(cmd[:2] == ['docker','inspect'] for cmd,_ in calls))

    def test_failed_child_and_partial_duplicate_or_private_output_never_print_summary(self):
        for fault in ('missing-line','duplicate-line','extra-output','child-error','stderr'):
            self.refused(fault)

    def test_changed_source_notes_authority_binding_or_reused_event_never_qualifies_continuation(self):
        for fault in ('source-prefix','notes','authority','binding','reused-event','retained'):
            self.refused(fault)

    def test_replacement_cannot_overwrite_plan_start_or_result_or_add_effects_failures(self):
        for fault in ('plan','start','result','extra-effect','failure-row','suffix','plan-hash','result-hash'):
            self.refused(fault)

    def test_both_cleanup_gates_recheck_completed_private_and_retained_graphs(self):
        for point in ('container-cleanup','image-cleanup'):
            for table in ('GroupModelAttemptResults','GroupAutomaticInputPlans','GroupRequestRevisions','GroupBindings','GroupListenerLeases'):
                self.refused('cleanup:'+point+':'+table)
        self.refused('retained-container-cleanup'); self.refused('image-error')

    def test_original_first_batch_rows_cannot_drift_during_later_children_or_cleanup(self):
        # Persistent changed hashes previously passed after growing-scope
        # rebaselining. Each old table must remain independently unchanged.
        for point in ('followup','v3-history-commit','v3-history-resume','container-cleanup','image-cleanup'):
            for table in ('GroupBatchAllocations','GroupBatchAllocatedRevisions','GroupBatchClaimStates','GroupBatchClaimReceipts','GroupIngressInbox'):
                self.refused('original-batch:'+point+':'+table)

    def test_first_child_error_remains_primary_when_cleanup_also_mutates(self):
        args,process,_,_,_ = self.oracle('child-error')
        def failing_cleanup(command,**kwargs):
            if command[:3] == ['docker','image','rm']: raise AssertionError('secondary cleanup failure')
            return process(command,**kwargs)
        with patch.dict(os.environ,self.environment,clear=True),patch.object(proof.subprocess,'run',failing_cleanup), \
                self.assertRaises(AssertionError) as error:
            proof.verify(**{key:value for key,value in args.items() if key != "_mutations"})
        self.assertNotIn('secondary',str(error.exception))

    def test_missing_legacy_rows_never_qualifies_mixed_version_proof(self):
        args,process,calls,_,_ = self.oracle('legacy-missing')
        with patch.dict(os.environ,self.environment,clear=True),patch.object(proof.subprocess,'run',process),self.assertRaises(AssertionError):
            proof.verify(**{key:value for key,value in args.items() if key != '_mutations'})
        self.assertEqual([],calls)

    def test_mismatch_failure_always_attempts_restore_and_never_runs_resume(self):
        for fault in ('mismatch-update','mismatch-output','mismatch-child','mismatch-child-exception','mismatch-restore'):
            args,process,calls,_,_ = self.oracle(fault)
            with self.subTest(fault=fault),patch.dict(os.environ,self.environment,clear=True),patch.object(proof.subprocess,'run',process), \
                    patch('sys.stdout',new_callable=io.StringIO) as output,self.assertRaises(AssertionError) as error:
                proof.verify(**{key:value for key,value in args.items() if key != '_mutations'})
            self.assertEqual(['2','3'],args['_mutations']);self.assertEqual('',output.getvalue())
            self.assertNotIn('PRIVATE_SQL',str(error.exception))
            self.assertFalse(any(cmd[-1]=='v3-history-resume' for cmd,_ in calls))

    def test_primary_mismatch_exception_survives_secondary_restore_failure(self):
        args,process,_,_,_ = self.oracle('mismatch-child-exception')
        sql=args['sql']
        def restore_fail(query):
            value=sql(query)
            if query.startswith('SET NOCOUNT ON; UPDATE aioffice.GroupAutomaticInputPlans SET FormatVersion=3'):raise AssertionError('secondary owned restore failure')
            return value
        with patch.dict(os.environ,self.environment,clear=True),patch.object(proof.subprocess,'run',process),self.assertRaises(AssertionError) as error:
            proof.verify(**{**{key:value for key,value in args.items() if key != '_mutations'},'sql':restore_fail})
        self.assertEqual('primary owned mismatch child failure',str(error.exception));self.assertEqual(['2','3'],args['_mutations'])

    def refused(self,fault):
        args,process,calls,_,_ = self.oracle(fault)
        with self.subTest(fault=fault),patch.dict(os.environ,self.environment,clear=True),patch.object(proof.subprocess,'run',process), \
                patch('sys.stdout',new_callable=io.StringIO) as output,self.assertRaises(AssertionError) as error:
            proof.verify(**{key:value for key,value in args.items() if key != "_mutations"})
        self.assertEqual('',output.getvalue()); self.assertNotIn('PRIVATE_SQL',str(error.exception))
        self.assertTrue(any(cmd[:3] == ['docker','image','rm'] for cmd,_ in calls))


if __name__ == '__main__': unittest.main()
