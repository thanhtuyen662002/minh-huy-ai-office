"""Execute real nested fixture enrollment with inert external ports only."""
import ast
import base64
import copy
import json
from pathlib import Path
from types import SimpleNamespace
import unittest
import uuid


SOURCE=Path(__file__).with_name('smoke-group-ingress.py')


class FirstEmptySourceEnrollmentTests(unittest.TestCase):
    def harness(self):
        tree=ast.parse(SOURCE.read_text(encoding='utf-8-sig'))
        names=['enroll_owned_source','prepare_no_work_source','prepare_first_empty_source']
        nodes=[]
        for name in names:
            found=[n for n in ast.walk(tree) if isinstance(n,ast.FunctionDef) and n.name==name]
            self.assertEqual(1,len(found),name);nodes.append(found[0])
        state={};role_calls=[];writes=[];compose=[];ready=[];generated=[];fixtures=[]
        tenant,company,service=(str(uuid.uuid4()) for _ in range(3))
        def tokens(count):
            self.assertEqual(32,count);raw=bytes([len(generated)+1])*count;generated.append(raw);return raw
        def prepare(**kwargs):
            self.assertEqual((tenant,company,service),(kwargs['tenant'],kwargs['company'],kwargs['service']))
            source=str(uuid.uuid4());fixtures.append(source)
            kwargs['enroll_source'](source)
            return source,[str(uuid.uuid4()),str(uuid.uuid4())]
        def spec_from_file_location(name,path):
            filename=Path(path).name
            if filename=='owned-group-effect-fixture.py':
                install=lambda module:setattr(module,'prepare_no_work',prepare)
            elif filename=='owned-original-group-fixtures.py':
                install=lambda module:setattr(module,'enroll',lambda **kwargs:role_calls.append(kwargs))
            else:self.fail('Unexpected fixture import')
            return SimpleNamespace(loader=SimpleNamespace(exec_module=install))
        def write(text,**kwargs):
            self.assertEqual({'encoding':'utf-8'},kwargs);writes.append(json.loads(text))
        scope=dict(__file__=str(SOURCE),Path=Path,uuid=uuid,base64=base64,json=json,
            secrets=SimpleNamespace(token_bytes=tokens),private_environment=state,
            importlib=SimpleNamespace(util=SimpleNamespace(spec_from_file_location=spec_from_file_location,module_from_spec=lambda spec:SimpleNamespace())),
            tenant=tenant,company=company,service=service,directory=Path('/owned/aioffice-local'),api='http://127.0.0.1:8080',
            sql=lambda *args:self.fail('No actual SQL allowed'),identity_index=lambda *args:self.fail('No actual identity source allowed'),
            call=lambda *args:self.fail('No actual HTTP allowed'),
            override=SimpleNamespace(write_text=write,chmod=lambda mode:self.assertEqual(0o600,mode)),
            compose_run=lambda *args,**kwargs:compose.append((args,kwargs)),ready=lambda:ready.append(True))
        exec(compile(ast.fix_missing_locations(ast.Module(body=nodes,type_ignores=[])),str(SOURCE),'exec'),scope)
        return SimpleNamespace(scope=scope,state=state,roles=role_calls,writes=writes,compose=compose,ready=ready,generated=generated,fixtures=fixtures,tree=tree)

    def test_original_reused_slot_five_route_reproduces_refusal_without_overwriting_original(self):
        h=self.harness();original=h.scope['prepare_no_work_source']();before=copy.deepcopy(h.state)
        with self.assertRaises(AssertionError):h.scope['prepare_no_work_source']()
        self.assertEqual(before,h.state);self.assertEqual(1,len(h.generated))
        self.assertEqual([5],[x['slot'] for x in h.roles]);self.assertEqual(1,len(h.writes))
        self.assertEqual(original[0],h.state['AIOffice__GroupIntake__SourceKeys__5__SourceBindingId'])

    def test_actual_old_then_new_factory_uses_distinct_slots_and_preserves_original_role_and_key(self):
        h=self.harness();old=h.scope['prepare_no_work_source']();baseline=copy.deepcopy(h.state)
        new=h.scope['prepare_first_empty_source']()
        self.assertNotEqual(old[0],new[0]);self.assertEqual(2,len(h.generated))
        for key,value in baseline.items():self.assertEqual(value,h.state[key])
        prefix='AIOffice__GroupIntake__SourceKeys__19__'
        self.assertEqual(new[0],h.state[prefix+'SourceBindingId'])
        self.assertEqual(new[2],h.state['OWNED_FIRST_EMPTY_GROUP_CONTENT_KEY'])
        self.assertEqual('secretref://env/OWNED_FIRST_EMPTY_GROUP_CONTENT_KEY',h.state[prefix+'SecretRef'])
        self.assertEqual([5],[x['slot'] for x in h.roles])
        self.assertEqual(2,len(h.writes));self.assertEqual(2,len(h.compose));self.assertEqual(2,len(h.ready))
        self.assertTrue(all(x[0]==('up','-d','--no-deps','--force-recreate','core-api') and x[1]=={'overridden':True} for x in h.compose))

    def test_real_ingress_call_uses_the_new_factory_and_keeps_old_no_work_call(self):
        h=self.harness()
        calls=[n for n in ast.walk(h.tree) if isinstance(n,ast.Call) and isinstance(n.func,ast.Attribute) and n.func.attr=='verify']
        first=[n for n in calls if isinstance(n.func.value,ast.Name) and n.func.value.id=='first_empty_proof']
        self.assertEqual(1,len(first))
        source=next(k.value for k in first[0].keywords if k.arg=='prepare_source')
        self.assertIsInstance(source,ast.Name);self.assertEqual('prepare_first_empty_source',source.id)
        old=[n for n in calls if any(k.arg=='proof' and isinstance(k.value,ast.Constant) and k.value.value=='no_work' for k in n.keywords)]
        self.assertEqual(1,len(old))
        self.assertEqual('prepare_no_work_source',next(k.value.id for k in old[0].keywords if k.arg=='prepare_source'))

    def test_repeated_slot_nineteen_or_reused_source_binding_is_refused_before_any_new_secret(self):
        h=self.harness();h.scope['prepare_first_empty_source']();before=copy.deepcopy(h.state)
        with self.assertRaises(AssertionError):h.scope['prepare_first_empty_source']()
        self.assertEqual(before,h.state);self.assertEqual(1,len(h.generated))
        h=self.harness();old=h.scope['prepare_no_work_source']();before=copy.deepcopy(h.state)
        with self.assertRaises(AssertionError):h.scope['enroll_owned_source'](old[0],19)
        self.assertEqual(before,h.state);self.assertEqual(1,len(h.generated))

    def test_reserved_unknown_or_boolean_slots_do_not_mutate_registry(self):
        for slot in [15,22,True]:
            with self.subTest(slot=slot):
                h=self.harness()
                with self.assertRaises(AssertionError):h.scope['enroll_owned_source'](str(uuid.uuid4()),slot)
                self.assertEqual({},h.state);self.assertEqual([],h.generated);self.assertEqual([],h.writes)

    def test_generated_key_stays_in_private_override_and_is_not_a_compose_argument(self):
        h=self.harness();source,events,key=h.scope['prepare_first_empty_source']()
        self.assertEqual(2,len(set(events)));self.assertEqual(32,len(base64.b64decode(key)))
        self.assertEqual(h.state,h.writes[0]['services']['core-api']['environment'])
        self.assertNotIn(key,str(h.compose));self.assertEqual([],h.roles)


if __name__=='__main__':unittest.main()
