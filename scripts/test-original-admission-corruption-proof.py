"""Inert cleanup/failure controls, not persisted SQL/model evidence."""
import hashlib
import importlib.util
from pathlib import Path
from types import SimpleNamespace
import unittest
import uuid

spec = importlib.util.spec_from_file_location('original_corruption_controls',Path(__file__).with_name('smoke-group-original-admission-corruption.py'))
proof = importlib.util.module_from_spec(spec); spec.loader.exec_module(proof)


class OriginalAdmissionCorruptionProofTests(unittest.TestCase):
    def exercise(self, fault=None):
        original = b'AIOGOAD1' + b'a'*232
        digest = hashlib.sha256(original).hexdigest().upper()
        operation = str(uuid.uuid4())
        lines = [f'L|{len(original)}','H|'+digest,'T|2026-10-15T09:00:00.1234567+00:00','C|'+operation+'|2']
        lines += [f'B|{i//64}|'+original[i:i+64].hex().upper() for i in range(0,len(original),64)]
        if fault=='truncated-export':lines[-1] = lines[-1][:-2]
        if fault=='wrong-export-hash':lines[1]='H|'+'0'*64
        if fault=='bad-time':lines[2]='T|2026-10-15T09:00:00+07:00'
        changed=False; mutations=[]; calls=[]; checks=[]
        def owned(directory,api):checks.append((directory,api))
        def sql(query):
            nonlocal changed
            if query.startswith('/* owned-admission-corruption:export */'):return '\n'.join(lines)
            name=query.split('/* owned-admission-corruption:',1)[1].split(' */',1)[0]
            mutations.append(name)
            self.assertIn('WHERE TenantId=',query);self.assertIn(' AND BatchId=',query)
            self.assertNotIn('DISABLE',query);self.assertNotIn('GRANT',query)
            self.assertNotIn('NOCHECK',query);self.assertNotIn('DELETE FROM',query)
            if name.startswith('physical-'):
                self.assertIn('BEGIN TRANSACTION;',query);self.assertIn('IF XACT_STATE()<>0 ROLLBACK TRANSACTION;',query)
                self.assertIn('ERROR_NUMBER()<>547 OR CHARINDEX',query)
                self.assertIn("CHARINDEX(N'\"FK_GroupModelAttemptAdmissions_ExactOriginalAdmission\"'",query)
                self.assertIn('COLLATE Latin1_General_100_BIN2)=0 THROW;',query)
                self.assertIn("IF XACT_STATE()<>0 OR @@TRANCOUNT<>0 THROW 51000,'Owned FK rollback incomplete',1;",query)
                self.assertIn("THROW 51000,'Owned referenced sidecar mutation was permitted',1;",query)
                if fault=='physical-generic547':raise RuntimeError('generic547 is not exact FK evidence')
                if fault=='physical-wrong-fk':return 'PASS another FK refused mutation'
                if fault=='physical-allowed':return ''
                result = proof.PHYSICAL_PREFIX + name[len('physical-'):]
                if fault=='physical-private-output':result += '\nPRIVATE_SQL_DETAIL'
                return result
            if name=='restore':
                if fault=='restore':raise RuntimeError('restore failed')
                self.assertIn('Sidecar=0x'+original.hex().upper(),query)
                self.assertIn("SidecarSha256='"+digest+"'",query);self.assertIn("OriginalClaimOperationId='"+operation+"'",query)
                self.assertIn('OriginalClaimEpoch=2',query);self.assertIn('2026-10-15T09:00:00.1234567+00:00',query)
                changed=False;return 'PRIVATE_RESTORE_DETAIL' if fault=='restore-private-output' else ''
            self.assertNotIn('SidecarSha256=',query)
            if fault=='persisted-blocked':raise RuntimeError('SQL547 on persisted branch is not runtime refusal evidence')
            changed=True
            if fault=='partial-update':raise RuntimeError('reply lost after mutation')
            if fault=='persisted-private-output':return 'PRIVATE_PERSISTED_DETAIL'
            return ''
        def child(mode):
            calls.append(mode)
            if mode=='admission-original-refuse':
                self.assertTrue(changed)
                if fault=='child':raise RuntimeError('child refused incorrectly')
            else:
                self.assertEqual('admission-original-resume',mode);self.assertFalse(changed)
                if fault=='healthy-after-restore' and mutations:raise RuntimeError('healthy reread failed')
        def unchanged():
            self.assertFalse(changed)
            if fault=='restored-graph' and mutations:raise RuntimeError('restore changed retained graph')
        args=dict(directory=Path('/owned/aioffice-local'),api='http://127.0.0.1:8080',**{k:str(uuid.uuid4()) for k in ('tenant','company','source','batch')},
            sql=sql,run_child=child,reference=SimpleNamespace(require_owned=owned),assert_unchanged=unchanged)
        if fault:
            with self.assertRaises((AssertionError,RuntimeError)):proof.verify(**args)
        else:proof.verify(**args)
        return mutations,calls,changed

    def test_four_physical_and_five_persisted_cases_keep_companion_and_require_healthy_reads(self):
        mutations,calls,changed=self.exercise()
        self.assertEqual(14,len(mutations));self.assertEqual(5,mutations.count('restore'));self.assertFalse(changed)
        self.assertEqual(4,sum(name.startswith('physical-') for name in mutations))
        self.assertEqual(['admission-original-resume']*5+['admission-original-refuse','admission-original-resume']*5,calls)

    def test_generic_constraint_error_wrong_fk_successful_update_and_private_output_never_qualify(self):
        for fault in ('physical-generic547','physical-wrong-fk','physical-allowed','physical-private-output'):
            with self.subTest(fault=fault):
                mutations,calls,changed=self.exercise(fault)
                self.assertEqual(1,len(mutations));self.assertTrue(mutations[0].startswith('physical-'))
                self.assertFalse(changed);self.assertEqual(['admission-original-resume']*2,calls)

    def test_persisted_sql_refusal_and_unexpected_mutation_or_restore_output_never_qualify(self):
        for fault in ('persisted-blocked','persisted-private-output','restore-private-output'):
            with self.subTest(fault=fault):
                _,_,changed=self.exercise(fault)
                self.assertFalse(changed)

    def test_partial_mutation_reply_loss_and_child_failure_still_restore(self):
        for fault in ('partial-update','child'):
            with self.subTest(fault=fault):
                mutations,calls,changed=self.exercise(fault)
                self.assertEqual('restore',mutations[-1]);self.assertFalse(changed)
                self.assertEqual('admission-original-resume',calls[-1])

    def test_restore_graph_and_positive_read_failures_never_qualify(self):
        for fault in ('restore','restored-graph','healthy-after-restore'):
            with self.subTest(fault=fault):self.exercise(fault)

    def test_invalid_export_refuses_before_any_mutation(self):
        for fault in ('truncated-export','wrong-export-hash','bad-time'):
            with self.subTest(fault=fault):
                mutations,calls,_=self.exercise(fault)
                self.assertEqual([],mutations);self.assertEqual([],calls)


if __name__ == '__main__':unittest.main()
