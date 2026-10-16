"""Model-free controls for the owned original-plan proof, never actual SQL evidence."""
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
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


reference = module('original_plan_reference', 'smoke-group-reference.py')
automatic = module('original_plan_automatic', 'smoke-group-automatic-notes.py')
proof = module('original_plan_proof', 'smoke-group-original-plan.py')


class OriginalPlanProofTests(unittest.TestCase):
    owned = Path('/owned/aioffice-local')
    environment = {'CI':'true','GITHUB_ACTIONS':'true','RUNNER_TEMP':'/owned'}

    def args(self, forbidden):
        ids = [str(uuid.uuid4()) for _ in range(4)]
        return dict(directory=self.owned,api='http://127.0.0.1:8080',manifest={
            'AIOFFICE_INSTALLATION_ID':ids[3],'AIOFFICE_RUNTIME_PASSWORD':'p'*32},
            tenant=ids[0],company=ids[1],service=ids[2],sql=forbidden,prepare_source=forbidden,
            reference=reference,automatic=automatic)

    def test_unowned_refuses_before_all_resources(self):
        def forbidden(*args,**kwargs): self.fail('Unowned original plan reached a resource')
        for invalid in ({'CI':'false'},{'GITHUB_ACTIONS':'false'},{'RUNNER_TEMP':'/foreign'}):
            with self.subTest(invalid=invalid),patch.dict(os.environ,{**self.environment,**invalid},clear=True), \
                    patch.object(proof.subprocess,'run',forbidden),self.assertRaises(RuntimeError):
                proof.verify(**self.args(forbidden))

    def test_invalid_identity_and_credential_refuse_before_sql(self):
        def forbidden(*args,**kwargs): self.fail('Invalid original plan reached a resource')
        args=self.args(forbidden)
        for key in ('tenant','company','service'):
            for value in ('PRIVATE_SQL',str(uuid.UUID(int=0)),args[key].upper()):
                with self.subTest(key=key,value=value),patch.dict(os.environ,self.environment,clear=True),self.assertRaises((ValueError,AssertionError)):
                    proof.verify(**{**args,key:value})
        for password in ('short','p'*129,'PRIVATE_SQL;'+'p'*32):
            with self.subTest(password=password),patch.dict(os.environ,self.environment,clear=True),self.assertRaises(AssertionError):
                proof.verify(**{**args,'manifest':{**args['manifest'],'AIOFFICE_RUNTIME_PASSWORD':password}})

    def oracle(self,fault=None):
        tenant,company,service,old_source,old_account,source1,source2,account1,account2,installation=[str(uuid.uuid4()) for _ in range(10)]
        sources=[source1,source2];accounts=[account1,account2]
        events=[[str(uuid.uuid4()),str(uuid.uuid4())] for _ in range(2)]
        fixture=-1;phase='before';calls=[];queries=[]
        def prepare():
            nonlocal fixture,phase
            fixture+=1;phase='before'
            return sources[fixture],events[fixture],base64.b64encode(b'k'*32).decode('ascii')
        def sql(query):
            queries.append(query)
            if query.startswith('SELECT LOWER(CONVERT(char(36),Id)) FROM aioffice.GroupBindings'):return old_source
            if query.startswith('SELECT LOWER(CONVERT(char(36),Id)) FROM aioffice.GroupConnectorAccounts'):return old_account
            if query.startswith('SELECT LOWER(CONVERT(char(36),ConnectorAccountId))'):
                return accounts[fixture]
            if 'HASHBYTES' in query and not query.startswith('SELECT COUNT(*)'):
                if query.startswith('SET NOCOUNT ON;'):return '\n'.join(f'{i:02d}|'+'A'*64 for i in range(34))
                if fault=='retained' and fixture>=0 and 'BindingId IN' in query:return 'B'*64
                if fault=='source' and phase=='original-plan-commit' and 'GroupMessages' in query:return 'B'*64
                if fault=='replaced-plan' and phase=='original-plan-resume' and 'GroupAutomaticInputPlans' in query and 'BindingId IN' not in query:return 'B'*64
                if fault=='prior-scope' and fixture==1 and source1 in query and 'BindingId IN' not in query:return 'B'*64
                if fault and fault.startswith('completed-registry:') and fixture==1:
                    _,table,point=fault.split(':')
                    identity=source1 if table=='GroupBindings' else account1
                    if ('FROM aioffice.'+table+' ') in query and identity in query and phase==point:return 'B'*64
                return 'A'*64
            if 'CONCAT' in query:
                values={'original-plan-prepare':'0|0','original-plan-commit':'1|1','original-plan-resume':'1|2','original-plan-expiry':'0|1'}
                return '2|2' if fault=='second-row' and phase=='original-plan-resume' else values[phase]
            if 'GroupSourceStates' in query:return '0' if fault=='suffix' and phase!='original-plan-prepare' else '1'
            if 'GroupBatchClaimStates' in query:return '0' if fault=='witness' and phase=='original-plan-expiry' else '1'
            if 'GroupAutomaticInputPlans' in query:return '0' if fault=='plan-hash' and phase=='original-plan-commit' else '1'
            if 'SELECT COUNT(*) FROM aioffice.' in query:return '1' if fault=='effect' and phase=='original-plan-commit' else '0'
            self.fail('Unexpected original plan SQL')
        def process(command,**kwargs):
            nonlocal phase
            calls.append((command,kwargs))
            if command[:2]==['docker','build'] or command[:3]==['docker','image','rm']:
                if command[:3]==['docker','image','rm']:phase='image-cleanup'
                return SimpleNamespace(returncode=0,stdout='',stderr='')
            if command[:2]==['docker','inspect']:
                phase='container-cleanup'
                return SimpleNamespace(returncode=1,stdout='',stderr='Error: No such object: '+command[-1])
            phase=command[-1]
            lines=proof.PREPARE_LINES if phase=='original-plan-prepare' else [proof.RUNTIME_LINES[phase]]
            if fault=='missing-prepare' and phase=='original-plan-prepare':lines=lines[:1]
            if fault=='duplicate-line' and phase=='original-plan-resume':lines=lines+lines
            return SimpleNamespace(returncode=1 if fault=='child-error' and phase=='original-plan-expiry' else 0,
                stdout='\n'.join(lines)+'\n'+('PRIVATE_SQL\n' if fault=='extra-output' and phase=='original-plan-expiry' else ''),
                stderr='PRIVATE_SQL' if fault=='stderr' and phase=='original-plan-commit' else '')
        args=dict(directory=self.owned,api='http://127.0.0.1:8080',manifest={
            'AIOFFICE_INSTALLATION_ID':installation,'AIOFFICE_RUNTIME_PASSWORD':'p'*32},tenant=tenant,company=company,service=service,
            sql=sql,prepare_source=prepare,reference=reference,automatic=automatic)
        return args,process,calls,queries

    def test_exact_oracle_keeps_five_processes_and_secrets_private(self):
        args,process,calls,queries=self.oracle()
        with patch.dict(os.environ,self.environment,clear=True),patch.object(proof.subprocess,'run',process),patch('sys.stdout',new_callable=io.StringIO) as output:
            proof.verify(**args)
        self.assertEqual([proof.SUMMARY],output.getvalue().splitlines())
        runs=[(cmd,kw) for cmd,kw in calls if cmd[:2]==['docker','run']]
        self.assertEqual(['original-plan-prepare','original-plan-commit','original-plan-resume','original-plan-prepare','original-plan-expiry'],[cmd[-1] for cmd,_ in runs])
        for cmd,kw in runs:
            self.assertEqual(170,kw['timeout']);self.assertIn('--read-only',cmd)
            self.assertNotIn(args['manifest']['AIOFFICE_RUNTIME_PASSWORD'],' '.join(cmd)+kw['input'])
            self.assertNotIn(kw['env']['AIOFFICE_GROUP_REFERENCE_PROOF_SOURCE_KEY'],' '.join(cmd)+kw['input'])
        self.assertTrue(any('HASHBYTES' in q and 'GroupAutomaticInputPlans' in q and 'BindingId IN' in q for q in queries))
        self.assertEqual(2,sum(cmd[:2]==['docker','inspect'] for cmd,_ in calls))
        self.assertEqual(1,sum(cmd[:3]==['docker','image','rm'] for cmd,_ in calls))

    def test_corruption_partial_output_and_failed_child_never_print_success(self):
        for fault in ('retained','source','replaced-plan','prior-scope','second-row','suffix','witness','plan-hash','effect',
                'missing-prepare','duplicate-line','child-error','extra-output','stderr'):
            args,process,calls,_=self.oracle(fault)
            with self.subTest(fault=fault),patch.dict(os.environ,self.environment,clear=True), \
                    patch.object(proof.subprocess,'run',process),patch('sys.stdout',new_callable=io.StringIO) as output,self.assertRaises(AssertionError) as error:
                proof.verify(**args)
            self.assertEqual('',output.getvalue());self.assertNotIn('PRIVATE_SQL',str(error.exception))
            self.assertEqual(2,sum(cmd[:2]==['docker','inspect'] for cmd,_ in calls))
            self.assertEqual(1,sum(cmd[:3]==['docker','image','rm'] for cmd,_ in calls))

    def test_completed_registry_mutation_is_refused_during_second_prepare_child_and_both_cleanup_gates(self):
        for table in ('GroupBindings','GroupConnectorAccounts','GroupListenerLeases'):
            for phase in ('before','original-plan-expiry','container-cleanup','image-cleanup'):
                args,process,calls,_=self.oracle('completed-registry:'+table+':'+phase)
                with self.subTest(table=table,phase=phase),patch.dict(os.environ,self.environment,clear=True), \
                        patch.object(proof.subprocess,'run',process),patch('sys.stdout',new_callable=io.StringIO) as output,self.assertRaises(AssertionError):
                    proof.verify(**args)
                self.assertEqual('',output.getvalue())
                self.assertEqual(2,sum(cmd[:2]==['docker','inspect'] for cmd,_ in calls))
                self.assertEqual(1,sum(cmd[:3]==['docker','image','rm'] for cmd,_ in calls))

    def test_runtime_marker_contract_and_owned_registration_are_explicit(self):
        text=(Path(__file__).parent.parent/'tests/GroupReference.RuntimeProof/GroupAutomaticInputPlanRuntimeProof.cs').read_text(encoding='utf-8')
        for line in [proof.PREPARE_LINES[1],*proof.RUNTIME_LINES.values()]:self.assertIn(line,text)
        self.assertIn('empty history',proof.SUMMARY)
        self.assertIn('no provider tokenizer automatic DI or model',proof.SUMMARY)


if __name__=='__main__':unittest.main()
