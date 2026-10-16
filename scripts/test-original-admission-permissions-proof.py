"""Inert permission restoration controls; never actual SQL evidence."""
import importlib.util
from pathlib import Path
from types import SimpleNamespace
import unittest
import uuid

spec=importlib.util.spec_from_file_location('admission_permission_controls',Path(__file__).with_name('smoke-group-original-admission-permissions.py'))
proof=importlib.util.module_from_spec(spec);spec.loader.exec_module(proof)


class AdmissionPermissionProofTests(unittest.TestCase):
    def exercise(self,fault=None):
        changed=False;mutations=[];children=[];inventory_reads=0
        def sql(query):
            nonlocal changed,inventory_reads
            if query.startswith('/* owned-admission-permission:absent */'):return '1' if fault=='preexisting' else '0'
            if query.startswith('/* owned-admission-permission:inventory */'):
                inventory_reads+=1
                return ('B' if changed or fault=='inventory' and inventory_reads>1 else 'A')*64
            if query.startswith('/* owned-admission-permission:effective */'):return '0' if fault=='not-effective' else '1'
            if query.startswith('/* owned-admission-permission:physical-chain */'):
                self.assertTrue(changed);self.assertIn('ROLLBACK TRANSACTION;',query)
                return '0' if fault=='chain-zero' else '1'
            name=query.split('/* owned-admission-permission:',1)[1].split(' */',1)[0];mutations.append(name)
            if name.startswith('restore-'):
                if fault=='restore':raise RuntimeError('restore failed')
                changed=False;return ''
            self.assertIn(name,('column-update','table-control','owner-chain'));changed=True
            if fault=='mutation-reply':raise RuntimeError('mutation reply lost')
            return ''
        def child(mode):
            children.append(mode)
            if mode=='admission-original-permission':
                self.assertTrue(changed)
                if fault=='child':raise RuntimeError('child failed')
            else:self.assertEqual('admission-original-resume',mode);self.assertFalse(changed)
        def unchanged():
            self.assertFalse(changed)
            if fault=='graph' and mutations:raise RuntimeError('retained graph changed')
        args=dict(directory=Path('/owned/aioffice-local'),api='http://127.0.0.1:8080',**{k:str(uuid.uuid4()) for k in ('tenant','company','source','batch')},
            sql=sql,run_child=child,reference=SimpleNamespace(require_owned=lambda *_:None),assert_unchanged=unchanged)
        if fault:
            with self.assertRaises((RuntimeError,AssertionError)):proof.verify(**args)
        else:proof.verify(**args)
        return mutations,children,changed

    def test_three_privilege_cases_restore_and_require_healthy_reads(self):
        mutations,children,changed=self.exercise();self.assertFalse(changed);self.assertEqual(6,len(mutations))
        self.assertEqual(['admission-original-resume']+['admission-original-permission','admission-original-resume']*3,children)

    def test_partial_mutation_effective_or_child_failure_always_restores(self):
        for fault in ('mutation-reply','not-effective','child'):
            with self.subTest(fault=fault):
                mutations,children,changed=self.exercise(fault);self.assertFalse(changed);self.assertTrue(mutations[-1].startswith('restore-'))
                self.assertEqual('admission-original-resume',children[-1])

    def test_restore_inventory_graph_or_physical_chain_failure_never_qualifies(self):
        for fault in ('restore','inventory','graph','chain-zero'):
            with self.subTest(fault=fault):self.exercise(fault)

    def test_preexisting_procedure_is_never_mutated_or_removed(self):
        mutations,children,_=self.exercise('preexisting');self.assertEqual([],mutations);self.assertEqual([],children)


if __name__=='__main__':unittest.main()
