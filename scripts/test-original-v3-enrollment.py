"""Inert V3 enrollment and call order controls; not SQL or deployment proof."""
import ast
import base64
import importlib.util
import json
import os
from pathlib import Path
import secrets
from types import SimpleNamespace
import unittest
import uuid

spec = importlib.util.spec_from_file_location("v3_enrollment_ingress", Path(__file__).with_name("smoke-group-ingress.py"))
group_smoke = importlib.util.module_from_spec(spec); spec.loader.exec_module(group_smoke)

class OriginalV3EnrollmentTests(unittest.TestCase):
    owned = Path('/owned/aioffice-local')
    def test_v3_slot17_preserves_every_existing_enrollment_and_refuses_duplicate_or_foreign_slot(self):
        tree = ast.parse(Path(group_smoke.__file__).read_text(encoding="utf-8"))
        callback = next(node for node in ast.walk(tree) if isinstance(node, ast.FunctionDef) and node.name == "enroll_owned_source")
        block = compile(ast.fix_missing_locations(ast.Module([callback], type_ignores=[])), "<inert-source-key-enrollment>", "exec")
        calls = []
        class Override:
            def write_text(self, text, encoding): calls.append(("write", json.loads(text), encoding))
            def chmod(self, mode): calls.append(("chmod", mode))
        original = {"AIOffice__GroupIntake__SourceKeys__0__SourceBindingId": str(uuid.uuid4()),
            "AIOffice__GroupIntake__SourceKeys__0__SecretRef": "secretref://env/INERT_ORIGINAL_CONTENT_KEY",
            "INERT_ORIGINAL_CONTENT_KEY": "owned-inert-original-value"}
        environment = original.copy()
        tenant, company, source = (str(uuid.uuid4()) for _ in range(3))
        original_enrollments = []
        def enroll_original(**arguments):
            # Inert callback dependency: the real SQL boundary has separate
            # adversarial controls. Verify it precedes config and recreation.
            self.assertFalse(any(name.startswith(f"AIOffice__GroupIntake__SourceKeys__{arguments['slot']}__") for name in environment))
            original_enrollments.append(arguments)
        original_module = SimpleNamespace(enroll=enroll_original)
        original_spec = SimpleNamespace(loader=SimpleNamespace(exec_module=lambda module: None))
        namespace = dict(uuid=uuid, base64=base64, json=json, secrets=SimpleNamespace(token_bytes=lambda size: b"n"*size),
            tenant=tenant, company=company, private_environment=environment, override=Override(),
            importlib=SimpleNamespace(util=SimpleNamespace(spec_from_file_location=lambda *args: original_spec,
                module_from_spec=lambda spec: original_module)), Path=Path, __file__=group_smoke.__file__,
            directory=self.owned, api="http://127.0.0.1:8080", sql=object(),
            compose_run=lambda *args, **kwargs: calls.append(("compose", args, kwargs)), ready=lambda: calls.append(("ready",)))
        exec(block, namespace)
        with self.assertRaises(ValueError): namespace["enroll_owned_source"]("invalid-source", 1)
        for slot in (0, 15, 19, True, "1"):
            with self.assertRaises(AssertionError): namespace["enroll_owned_source"](source, slot)
        self.assertEqual(original, environment); self.assertEqual([], calls)
        namespace["enroll_owned_source"](source, 1)
        self.assertTrue(all(environment[name] == value for name, value in original.items()))
        prefix = "AIOffice__GroupIntake__SourceKeys__1__"
        self.assertEqual({"TenantId": tenant, "CompanyId": company, "SourceBindingId": source,
            "KeyId": "owned-native-source-v1", "SecretRef": "secretref://env/OWNED_NATIVE_GROUP_CONTENT_KEY", "IsWriteKey": "true"},
            {name[len(prefix):]: value for name, value in environment.items() if name.startswith(prefix)})
        self.assertEqual(environment, calls[0][1]["services"]["core-api"]["environment"])
        self.assertEqual(("chmod", 0o600), calls[1])
        self.assertEqual(("compose", ("up", "-d", "--no-deps", "--force-recreate", "core-api"), {"overridden": True}), calls[2])
        self.assertEqual(("ready",), calls[3])
        before = environment.copy()
        with self.assertRaises(AssertionError): namespace["enroll_owned_source"](source, 1)
        self.assertEqual(before, environment); self.assertEqual(4, len(calls))
        second_source = str(uuid.uuid4())
        namespace["enroll_owned_source"](second_source, 2)
        self.assertTrue(all(environment[name] == value for name, value in before.items()))
        second_prefix = "AIOffice__GroupIntake__SourceKeys__2__"
        self.assertEqual({"TenantId": tenant, "CompanyId": company, "SourceBindingId": second_source,
            "KeyId": "owned-managed-source-v1", "SecretRef": "secretref://env/OWNED_MANAGED_GROUP_CONTENT_KEY", "IsWriteKey": "true"},
            {name[len(second_prefix):]: value for name, value in environment.items() if name.startswith(second_prefix)})
        self.assertEqual(before["OWNED_NATIVE_GROUP_CONTENT_KEY"], environment["OWNED_NATIVE_GROUP_CONTENT_KEY"])
        self.assertEqual(["write", "chmod", "compose", "ready"], [call[0] for call in calls[4:]])
        final = environment.copy()
        with self.assertRaises(AssertionError): namespace["enroll_owned_source"](second_source, 2)
        self.assertEqual(final, environment); self.assertEqual(8, len(calls))
        third_source = str(uuid.uuid4())
        namespace["enroll_owned_source"](third_source, 3)
        self.assertTrue(all(environment[name] == value for name, value in final.items()))
        third_prefix = "AIOffice__GroupIntake__SourceKeys__3__"
        self.assertEqual({"TenantId": tenant, "CompanyId": company, "SourceBindingId": third_source,
            "KeyId": "owned-native-source-v1", "SecretRef": "secretref://env/OWNED_EFFECT_GROUP_CONTENT_KEY", "IsWriteKey": "true"},
            {name[len(third_prefix):]: value for name, value in environment.items() if name.startswith(third_prefix)})
        last = environment.copy()
        with self.assertRaises(AssertionError): namespace["enroll_owned_source"](third_source, 3)
        self.assertEqual(last, environment); self.assertEqual(12, len(calls))
        fourth_source = str(uuid.uuid4())
        namespace["enroll_owned_source"](fourth_source, 4)
        self.assertTrue(all(environment[name] == value for name, value in last.items()))
        fourth_prefix = "AIOffice__GroupIntake__SourceKeys__4__"
        self.assertEqual({"TenantId": tenant, "CompanyId": company, "SourceBindingId": fourth_source,
            "KeyId": "owned-native-source-v1", "SecretRef": "secretref://env/OWNED_AUTOMATIC_GROUP_CONTENT_KEY", "IsWriteKey": "true"},
            {name[len(fourth_prefix):]: value for name, value in environment.items() if name.startswith(fourth_prefix)})
        final_automatic = environment.copy()
        with self.assertRaises(AssertionError): namespace["enroll_owned_source"](fourth_source, 4)
        self.assertEqual(final_automatic, environment); self.assertEqual(16, len(calls))
        fifth_source = str(uuid.uuid4())
        namespace["enroll_owned_source"](fifth_source, 5)
        self.assertTrue(all(environment[name] == value for name, value in final_automatic.items()))
        fifth_prefix = "AIOffice__GroupIntake__SourceKeys__5__"
        self.assertEqual({"TenantId": tenant, "CompanyId": company, "SourceBindingId": fifth_source,
            "KeyId": "owned-native-source-v1", "SecretRef": "secretref://env/OWNED_NO_WORK_GROUP_CONTENT_KEY", "IsWriteKey": "true"},
            {name[len(fifth_prefix):]: value for name, value in environment.items() if name.startswith(fifth_prefix)})
        final_no_work = environment.copy()
        with self.assertRaises(AssertionError): namespace["enroll_owned_source"](fifth_source, 5)
        self.assertEqual(final_no_work, environment); self.assertEqual(20, len(calls))
        sixth_source = str(uuid.uuid4())
        namespace["enroll_owned_source"](sixth_source, 6)
        self.assertTrue(all(environment[name] == value for name, value in final_no_work.items()))
        sixth_prefix = "AIOffice__GroupIntake__SourceKeys__6__"
        self.assertEqual({"TenantId": tenant, "CompanyId": company, "SourceBindingId": sixth_source,
            "KeyId": "owned-native-source-v1", "SecretRef": "secretref://env/OWNED_HOST_GROUP_CONTENT_KEY", "IsWriteKey": "true"},
            {name[len(sixth_prefix):]: value for name, value in environment.items() if name.startswith(sixth_prefix)})
        final_host = environment.copy()
        with self.assertRaises(AssertionError): namespace["enroll_owned_source"](sixth_source, 6)
        self.assertEqual(final_host, environment); self.assertEqual(24, len(calls))
        seventh_source = str(uuid.uuid4())
        namespace["enroll_owned_source"](seventh_source, 7)
        self.assertTrue(all(environment[name] == value for name, value in final_host.items()))
        seventh_prefix = "AIOffice__GroupIntake__SourceKeys__7__"
        self.assertEqual({"TenantId": tenant, "CompanyId": company, "SourceBindingId": seventh_source,
            "KeyId": "owned-native-source-v1", "SecretRef": "secretref://env/OWNED_COVERAGE_GROUP_CONTENT_KEY", "IsWriteKey": "true"},
            {name[len(seventh_prefix):]: value for name, value in environment.items() if name.startswith(seventh_prefix)})
        final_coverage = environment.copy()
        with self.assertRaises(AssertionError): namespace["enroll_owned_source"](seventh_source, 7)
        self.assertEqual(final_coverage, environment); self.assertEqual(28, len(calls))
        eighth_source = str(uuid.uuid4())
        namespace["enroll_owned_source"](eighth_source, 8)
        self.assertTrue(all(environment[name] == value for name, value in final_coverage.items()))
        eighth_prefix = "AIOffice__GroupIntake__SourceKeys__8__"
        self.assertEqual({"TenantId": tenant, "CompanyId": company, "SourceBindingId": eighth_source,
            "KeyId": "owned-native-source-v1", "SecretRef": "secretref://env/OWNED_RAW_HISTORY_GROUP_CONTENT_KEY", "IsWriteKey": "true"},
            {name[len(eighth_prefix):]: value for name, value in environment.items() if name.startswith(eighth_prefix)})
        final_raw_history = environment.copy()
        with self.assertRaises(AssertionError): namespace["enroll_owned_source"](eighth_source, 8)
        self.assertEqual(final_raw_history, environment); self.assertEqual(32, len(calls))
        ninth_source = str(uuid.uuid4())
        namespace["enroll_owned_source"](ninth_source, 9)
        self.assertTrue(all(environment[name] == value for name, value in final_raw_history.items()))
        ninth_prefix = "AIOffice__GroupIntake__SourceKeys__9__"
        self.assertEqual({"TenantId": tenant, "CompanyId": company, "SourceBindingId": ninth_source,
            "KeyId": "owned-native-source-v1", "SecretRef": "secretref://env/OWNED_MODEL_JOURNAL_GROUP_CONTENT_KEY", "IsWriteKey": "true"},
            {name[len(ninth_prefix):]: value for name, value in environment.items() if name.startswith(ninth_prefix)})
        final_journal = environment.copy()
        with self.assertRaises(AssertionError): namespace["enroll_owned_source"](ninth_source, 9)
        self.assertEqual(final_journal, environment); self.assertEqual(36, len(calls))
        for slot, secret_name in ((10, "OWNED_ORIGINAL_PLAN_GROUP_CONTENT_KEY"), (11, "OWNED_ORIGINAL_PLAN_EXPIRY_GROUP_CONTENT_KEY"),
                (12, "OWNED_ATTEMPT_RESULT_GROUP_CONTENT_KEY"), (13, "OWNED_ATTEMPT_RESULT_DEADLINE_GROUP_CONTENT_KEY"), (14, "OWNED_HISTORY_SELECTION_GROUP_CONTENT_KEY")):
            retained = environment.copy(); source_id = str(uuid.uuid4())
            namespace["enroll_owned_source"](source_id, slot)
            self.assertTrue(all(environment[name] == value for name, value in retained.items()))
            self.assertEqual("secretref://env/" + secret_name, environment[f"AIOffice__GroupIntake__SourceKeys__{slot}__SecretRef"])
            self.assertEqual(source_id, environment[f"AIOffice__GroupIntake__SourceKeys__{slot}__SourceBindingId"])
            enrolled = environment.copy()
            with self.assertRaises(AssertionError): namespace["enroll_owned_source"](source_id, slot)
            self.assertEqual(enrolled, environment)
        self.assertEqual(56, len(calls))
        retained = environment.copy(); v3_source = str(uuid.uuid4())
        namespace["enroll_owned_source"](v3_source, 17)
        self.assertTrue(all(environment[name] == value for name,value in retained.items()))
        self.assertEqual("secretref://env/OWNED_ORIGINAL_V3_GROUP_CONTENT_KEY", environment["AIOffice__GroupIntake__SourceKeys__17__SecretRef"])
        self.assertEqual(v3_source, environment["AIOffice__GroupIntake__SourceKeys__17__SourceBindingId"])
        enrolled = environment.copy()
        with self.assertRaises(AssertionError): namespace["enroll_owned_source"](v3_source, 17)
        self.assertEqual(enrolled,environment); self.assertEqual(60,len(calls))
        with self.assertRaises(AssertionError): namespace["enroll_owned_source"](source, 16)
        self.assertEqual(enrolled,environment); self.assertEqual(60,len(calls))
        admission_source = str(uuid.uuid4())
        namespace["enroll_owned_source"](admission_source, 18)
        self.assertTrue(all(environment[name] == value for name,value in enrolled.items()))
        self.assertEqual(admission_source, environment["AIOffice__GroupIntake__SourceKeys__18__SourceBindingId"])
        self.assertEqual("secretref://env/OWNED_ORIGINAL_ADMISSION_GROUP_CONTENT_KEY", environment["AIOffice__GroupIntake__SourceKeys__18__SecretRef"])
        admission_enrolled = environment.copy()
        with self.assertRaises(AssertionError): namespace["enroll_owned_source"](admission_source, 18)
        with self.assertRaises(AssertionError): namespace["enroll_owned_source"](v3_source, 18)
        self.assertEqual(admission_enrolled,environment); self.assertEqual(64,len(calls))
        self.assertEqual([4, 5, 6, 8], [item['slot'] for item in original_enrollments])
        self.assertEqual([fourth_source, fifth_source, sixth_source, eighth_source], [item['source'] for item in original_enrollments])
        for item in original_enrollments:
            self.assertEqual(dict(directory=self.owned, api="http://127.0.0.1:8080", tenant=tenant,
                company=company, source=item['source'], slot=item['slot'], sql=namespace['sql']), item)

    def test_v3_call_follows_retained_legacy_history_and_precedes_existing_cleanup(self):
        tree=ast.parse(Path(group_smoke.__file__).read_text(encoding='utf-8'))
        verify=next(node for node in tree.body if isinstance(node,ast.FunctionDef) and node.name=='verify')
        calls=[node for node in ast.walk(verify) if isinstance(node,ast.Call) and isinstance(node.func,ast.Attribute)
            and isinstance(node.func.value,ast.Name) and node.func.attr=='verify']
        history=next(node for node in calls if node.func.value.id=='history_proof')
        v3=next(node for node in calls if node.func.value.id=='v3_proof')
        self.assertLess(history.lineno,v3.lineno)
        supplied={item.arg:ast.unparse(item.value) for item in v3.keywords}
        self.assertEqual('prepare_v3_source',supplied['prepare_source']);self.assertEqual('append_v3_followup',supplied['append_followup'])
        self.assertEqual('reference_proof',supplied['reference']);self.assertEqual('automatic_proof',supplied['automatic'])
        factory=next(node for node in ast.walk(verify) if isinstance(node,ast.FunctionDef) and node.name=='prepare_v3_source')
        enrollment=next(node for node in ast.walk(factory) if isinstance(node,ast.Call) and isinstance(node.func,ast.Name) and node.func.id=='enroll_owned_source')
        self.assertEqual(17,enrollment.args[1].value)
        text=Path(group_smoke.__file__).read_text(encoding='utf-8')
        self.assertIn('return new_source, new_events, private_environment["OWNED_ORIGINAL_V3_GROUP_CONTENT_KEY"]',text)

if __name__=='__main__':unittest.main()
