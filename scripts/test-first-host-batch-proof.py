"""Inert first-host orchestration controls; no SQL/model qualification."""
import base64
import importlib.util
import io
import os
from pathlib import Path
from types import SimpleNamespace
import unittest
from unittest.mock import patch
import uuid


def load(name,filename):
    spec=importlib.util.spec_from_file_location(name,Path(__file__).with_name(filename))
    module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module);return module


proof=load('first_host_controls','smoke-group-first-host-batch.py')
reference=load('first_host_reference','smoke-group-reference.py')
automatic=load('first_host_automatic','smoke-group-automatic-notes.py')


class FirstHostBatchProofTests(unittest.TestCase):
    def run_control(self,fault=None):
        tenant,company,service,source,account,old_source,old_account,installation=(str(uuid.uuid4()) for _ in range(8))
        events=[str(uuid.uuid4()),str(uuid.uuid4())];phase='before';denied=False;calls=[];mutations=[]
        def sql(query):
            nonlocal denied
            if query.startswith('/* owned-first-host-digests */'):
                rows=[]
                for i,line in enumerate(query.splitlines()[1:]):
                    own=source in line and 'BindingId IN' not in line
                    bad=own and ((fault=='recovery-graph' and phase=='first-host-recover')
                        or (fault=='cleanup-container' and phase=='container-cleanup') or (fault=='cleanup-image' and phase=='image-cleanup'))
                    rows.append(f'{i:02d}|'+('B' if bad else 'A')*64)
                if fault=='digest-index' and phase!='before':rows[0]='99'+rows[0][2:]
                return '\n'.join(rows)
            if query.startswith('/* owned-first-host-permissions */'):
                return ('B' if denied else 'A')*64
            if query.startswith('/* owned-first-host-deny */'):
                denied=True;mutations.append('deny');return 'PRIVATE' if fault=='deny-output' else ''
            if query.startswith('/* owned-first-host-restore */'):
                mutations.append('restore')
                if fault=='restore':raise AssertionError('owned restoration failed')
                denied=False;return 'PRIVATE' if fault=='restore-output' else ''
            if query.startswith('/* owned-first-host-grants */'):return '2' if fault=='grants' else '3'
            if query.startswith('/* owned-first-host-zero */'):return '1|0|0|0' if fault=='preseeded' else '0|0|0|0'
            if query.startswith('/* owned-first-host-counts */'):return '0|1|1|2|2|1|1|1|1' if fault=='counts' else '1|1|1|2|2|1|1|1|1'
            if query.startswith('SELECT LOWER(CONVERT(char(36),Id)) FROM aioffice.GroupBindings'):return old_source
            if query.startswith('SELECT LOWER(CONVERT(char(36),Id)) FROM aioffice.GroupConnectorAccounts'):return old_account
            if query.startswith('SELECT LOWER(CONVERT(char(36),ConnectorAccountId))'):return account
            if query.startswith('SET NOCOUNT ON;'):
                return '\n'.join(f'{i:02d}|'+('B' if fault=='retained' and phase!='before' else 'A')*64 for i in range(34))
            self.fail('Unexpected fixed SQL shape')

        def process(command,**kwargs):
            nonlocal phase
            calls.append((command,kwargs))
            if command[:2]==['docker','build']:return SimpleNamespace(returncode=0,stdout='',stderr='')
            if command[:2]==['docker','inspect']:
                phase='container-cleanup';return SimpleNamespace(returncode=1,stdout='',stderr='Error: No such object: '+command[-1])
            if command[:3]==['docker','image','rm']:
                phase='image-cleanup';return SimpleNamespace(returncode=1 if fault=='image-error' else 0,stdout='',stderr='')
            self.assertEqual(['docker','run'],command[:2]);phase=command[-1]
            if phase=='first-host-recover':self.assertTrue(denied)
            line={'automatic-host-prepare':proof.PREPARE_LINE,'first-host-commit':proof.COMMIT_LINE,'first-host-recover':proof.RECOVER_LINE}[phase]
            if fault in ('commit-child','recover-child') and phase==('first-host-commit' if fault=='commit-child' else 'first-host-recover'):
                return SimpleNamespace(returncode=1,stdout='',stderr='')
            if fault=='private-output' and phase=='first-host-commit':line+='\nPRIVATE_CONTEXT'
            if fault=='duplicate' and phase=='first-host-recover':line+='\n'+line
            return SimpleNamespace(returncode=0,stdout=line+'\n',stderr='')

        args=dict(directory=Path('/owned/aioffice-local'),api='http://127.0.0.1:8080',
            manifest=dict(AIOFFICE_INSTALLATION_ID=installation,AIOFFICE_RUNTIME_PASSWORD='r'*40),tenant=tenant,company=company,service=service,
            sql=sql,prepare_source=lambda:(source,events,base64.b64encode(b'k'*32).decode()),reference=reference,automatic=automatic)
        with patch.dict(os.environ,dict(CI='true',GITHUB_ACTIONS='true',RUNNER_TEMP='/owned'),clear=True),patch.object(proof.subprocess,'run',process),patch('sys.stdout',new_callable=io.StringIO) as output:
            if fault:
                with self.assertRaises(AssertionError):proof.verify(**args)
                self.assertEqual('',output.getvalue())
            else:
                proof.verify(**args);self.assertEqual([proof.SUMMARY],output.getvalue().splitlines())
        return calls,mutations,args

    def test_success_only_after_fresh_creation_recovery_and_both_cleanups(self):
        calls,mutations,args=self.run_control()
        self.assertEqual(['deny','restore'],mutations)
        self.assertEqual(['automatic-host-prepare','first-host-commit','first-host-recover'],[cmd[-1] for cmd,_ in calls if cmd[:2]==['docker','run']])
        self.assertEqual(2,sum(cmd[:2]==['docker','inspect'] for cmd,_ in calls))
        for cmd,kwargs in calls:
            if cmd[:2]!=['docker','run']:continue
            self.assertIn('--read-only',cmd);self.assertEqual(170,kwargs['timeout'])
            self.assertNotIn(args['manifest']['AIOFFICE_RUNTIME_PASSWORD'],' '.join(cmd)+kwargs['input'])
            self.assertNotIn(kwargs['env']['AIOFFICE_GROUP_REFERENCE_PROOF_SOURCE_KEY'],' '.join(cmd)+kwargs['input'])

    def test_preseeded_or_incomplete_effects_and_wrong_permission_baseline_never_qualify(self):
        for fault in ('preseeded','counts','grants','digest-index','retained'):
            with self.subTest(fault=fault):self.run_control(fault)

    def test_private_or_failed_child_and_duplicate_output_never_qualify(self):
        for fault in ('commit-child','recover-child','private-output','duplicate'):
            with self.subTest(fault=fault):self.run_control(fault)

    def test_failed_denial_recovery_or_restoration_still_attempts_exact_restore(self):
        for fault in ('deny-output','recover-child','recovery-graph','restore','restore-output'):
            with self.subTest(fault=fault):
                _,mutations,_=self.run_control(fault);self.assertEqual(['deny','restore'],mutations)

    def test_cleanup_or_changed_final_graph_suppresses_aggregate(self):
        for fault in ('image-error','cleanup-container','cleanup-image'):
            with self.subTest(fault=fault):self.run_control(fault)


if __name__=='__main__':unittest.main()
