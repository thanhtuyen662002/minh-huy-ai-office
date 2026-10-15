"""Real nested factory/enroller regression with only external ports inert."""
import ast
import base64
import copy
import importlib.util
import json
from pathlib import Path
from types import SimpleNamespace
import unittest
import uuid

SOURCE = Path(__file__).with_name('smoke-group-ingress.py')
DRIVER = Path(__file__).with_name('smoke-group-original-admission.py')


class AcceptedGuardSourceEnrollmentTests(unittest.TestCase):
    def harness(self):
        tree = ast.parse(SOURCE.read_text(encoding='utf-8-sig'))
        names = ['enroll_owned_source', 'prepare_admission_source', 'prepare_accepted_guard_source',
            'prepare_first_empty_source', 'prepare_no_work_source']
        nodes = []
        for name in names:
            found = [n for n in ast.walk(tree) if isinstance(n, ast.FunctionDef) and n.name == name]
            self.assertEqual(1, len(found), name)
            nodes.append(found[0])
        state = {}; generated = []; roles = []; writes = []; compose = []; ready = []
        tenant, company, service = (str(uuid.uuid4()) for _ in range(3))
        def tokens(count):
            self.assertEqual(32, count)
            raw = bytes([len(generated) + 1]) * count; generated.append(raw); return raw
        def prepare(**kwargs):
            self.assertEqual((tenant, company, service), (kwargs['tenant'], kwargs['company'], kwargs['service']))
            source = str(uuid.uuid4()); kwargs['enroll_source'](source)
            return source, [str(uuid.uuid4()), str(uuid.uuid4())]
        def spec(name, path):
            if Path(path).name == 'owned-group-effect-fixture.py':
                install = lambda module: (setattr(module, 'prepare_automatic', prepare), setattr(module, 'prepare_no_work', prepare))
            elif Path(path).name == 'owned-original-group-fixtures.py':
                install = lambda module: setattr(module, 'enroll', lambda **kwargs: roles.append(kwargs))
            else: self.fail('Unexpected fixture import')
            return SimpleNamespace(loader=SimpleNamespace(exec_module=install))
        def write(text, **kwargs):
            self.assertEqual({'encoding': 'utf-8'}, kwargs); writes.append(json.loads(text))
        def forbidden(*args, **kwargs): self.fail('No SQL, HTTP or identity lookup permitted')
        scope = dict(__file__=str(SOURCE), Path=Path, uuid=uuid, base64=base64, json=json,
            secrets=SimpleNamespace(token_bytes=tokens), private_environment=state,
            importlib=SimpleNamespace(util=SimpleNamespace(spec_from_file_location=spec,
                module_from_spec=lambda value: SimpleNamespace())),
            tenant=tenant, company=company, service=service, directory=Path('/owned/aioffice-local'),
            api='http://127.0.0.1:8080', sql=forbidden, identity_index=forbidden, call=forbidden,
            override=SimpleNamespace(write_text=write, chmod=lambda mode: self.assertEqual(0o600, mode)),
            compose_run=lambda *args, **kwargs: compose.append((args, kwargs)), ready=lambda: ready.append(True))
        exec(compile(ast.fix_missing_locations(ast.Module(body=nodes, type_ignores=[])), str(SOURCE), 'exec'), scope)
        return SimpleNamespace(scope=scope, state=state, generated=generated, roles=roles,
            writes=writes, compose=compose, ready=ready, tree=tree)

    def test_hosted_collision_reproduces_second_slot_eighteen_refusal_before_secret_or_override_change(self):
        h = self.harness(); original = h.scope['prepare_admission_source'](); baseline = copy.deepcopy(h.state)
        with self.assertRaises(AssertionError): h.scope['prepare_admission_source']()
        self.assertEqual(baseline, h.state); self.assertEqual(1, len(h.generated)); self.assertEqual(1, len(h.writes))
        self.assertEqual(original[0], h.state['AIOffice__GroupIntake__SourceKeys__18__SourceBindingId'])

    def test_distinct_main_mutant_empty_and_legacy_factories_preserve_all_existing_keys_and_roles(self):
        h = self.harness(); bindings = []
        for name, slot, secret in [('prepare_no_work_source', 5, 'OWNED_NO_WORK_GROUP_CONTENT_KEY'),
                ('prepare_admission_source', 18, 'OWNED_ORIGINAL_ADMISSION_GROUP_CONTENT_KEY'),
                ('prepare_accepted_guard_source', 20, 'OWNED_ACCEPTED_GUARD_GROUP_CONTENT_KEY'),
                ('prepare_first_empty_source', 19, 'OWNED_FIRST_EMPTY_GROUP_CONTENT_KEY')]:
            baseline = copy.deepcopy(h.state); source, events, key = h.scope[name](); bindings.append(source)
            for k, v in baseline.items(): self.assertEqual(v, h.state[k])
            self.assertEqual(source, h.state[f'AIOffice__GroupIntake__SourceKeys__{slot}__SourceBindingId'])
            self.assertEqual('secretref://env/' + secret, h.state[f'AIOffice__GroupIntake__SourceKeys__{slot}__SecretRef'])
            self.assertEqual(key, h.state[secret]); self.assertEqual(32, len(base64.b64decode(key)))
            self.assertEqual(2, len(set(events))); self.assertNotIn(key, str(h.compose))
        self.assertEqual(4, len(set(bindings))); self.assertEqual(4, len(set(h.generated)))
        self.assertEqual([5], [x['slot'] for x in h.roles])
        self.assertEqual(4, len(h.writes)); self.assertEqual(4, len(h.ready))
        self.assertTrue(all(x == (('up', '-d', '--no-deps', '--force-recreate', 'core-api'), {'overridden': True}) for x in h.compose))

    def test_actual_ingress_and_parent_dispatch_route_mutant_factory_independently(self):
        h = self.harness(); original = h.scope['prepare_admission_source']()
        calls = [n for n in ast.walk(h.tree) if isinstance(n, ast.Call) and isinstance(n.func, ast.Attribute)
            and isinstance(n.func.value, ast.Name) and n.func.value.id == 'admission_proof' and n.func.attr == 'verify']
        self.assertEqual(1, len(calls))
        routes = {k.arg: k.value.id for k in calls[0].keywords if isinstance(k.value, ast.Name)}
        self.assertEqual('prepare_admission_source', routes['prepare_source'])
        self.assertEqual('prepare_accepted_guard_source', routes['prepare_counterfactual_source'])
        driver = ast.parse(DRIVER.read_text(encoding='utf-8-sig'))
        dispatch = [n for n in ast.walk(driver) if isinstance(n, ast.Assign) and isinstance(n.value, ast.Call)
            and isinstance(n.value.func, ast.Attribute) and isinstance(n.value.func.value, ast.Name)
            and n.value.func.value.id == 'guard_counterfactual' and n.value.func.attr == 'verify']
        self.assertEqual(1, len(dispatch)); result = []
        def child(**kwargs):
            self.assertIs(kwargs['prepare_source'], h.scope['prepare_accepted_guard_source'])
            result.append(kwargs['prepare_source']()); return 'inert-child-result'
        scope = dict(h.scope, installation=str(uuid.uuid4()), password='p' * 40,
            prepare_source=h.scope['prepare_admission_source'],
            prepare_counterfactual_source=h.scope['prepare_accepted_guard_source'],
            reference=object(), automatic=object(), RUNTIME_LINES={}, unchanged=lambda: None,
            guard_counterfactual=SimpleNamespace(verify=child))
        exec(compile(ast.fix_missing_locations(ast.Module(body=dispatch, type_ignores=[])), str(DRIVER), 'exec'), scope)
        self.assertEqual('inert-child-result', scope['counterfactual_summary'])
        self.assertEqual(1, len(result)); self.assertNotEqual(original[0], result[0][0])
        self.assertEqual(result[0][0], h.state['AIOffice__GroupIntake__SourceKeys__20__SourceBindingId'])

    def test_duplicate_slot_or_reused_binding_refused_without_new_secret_override_or_restart(self):
        for duplicate in (True, False):
            with self.subTest(duplicate=duplicate):
                h = self.harness(); original = h.scope['prepare_admission_source']()
                if duplicate: h.scope['prepare_accepted_guard_source']()
                baseline = copy.deepcopy(h.state); count = len(h.generated)
                with self.assertRaises(AssertionError):
                    if duplicate: h.scope['prepare_accepted_guard_source']()
                    else: h.scope['enroll_owned_source'](original[0], 20)
                self.assertEqual(baseline, h.state); self.assertEqual(count, len(h.generated))
                self.assertEqual(count, len(h.writes)); self.assertEqual(count, len(h.compose))

    def test_reserved_unknown_and_boolean_slots_refused_before_mutation(self):
        for slot in (15, 21, True):
            with self.subTest(slot=slot):
                h = self.harness()
                with self.assertRaises(AssertionError): h.scope['enroll_owned_source'](str(uuid.uuid4()), slot)
                self.assertEqual({}, h.state); self.assertEqual([], h.generated); self.assertEqual([], h.writes)

    def test_parent_refuses_same_or_noncallable_factory_before_sql_or_process(self):
        spec = importlib.util.spec_from_file_location('guard_enrollment_parent', DRIVER)
        proof = importlib.util.module_from_spec(spec); spec.loader.exec_module(proof)
        def forbidden(*args, **kwargs): self.fail('No external port permitted')
        factory = lambda: self.fail('No source should be prepared')
        for other in (factory, None):
            with self.subTest(callable=other is not None):
                with self.assertRaises(AssertionError):
                    proof.verify(directory='owned', api='owned', manifest={}, tenant='', company='', service='',
                        sql=forbidden, prepare_source=factory, prepare_counterfactual_source=other,
                        reference=SimpleNamespace(require_owned=lambda *args: None), automatic=object())


if __name__ == '__main__': unittest.main()
