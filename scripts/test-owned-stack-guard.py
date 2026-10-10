"""Verify the adversarial fixture boundary without files, Docker or network."""
import importlib.util
import ast
import base64
import hashlib
import json
import io
from email.message import Message
import os
import re
from pathlib import Path
import unittest
import tempfile
import uuid
from unittest.mock import patch
from types import SimpleNamespace

spec = importlib.util.spec_from_file_location("owned_stack_smoke", Path(__file__).with_name("smoke-local-stack.py"))
smoke = importlib.util.module_from_spec(spec)
spec.loader.exec_module(smoke)
administrator_spec = importlib.util.spec_from_file_location("administrator_smoke", Path(__file__).with_name("smoke-company-administrators.py"))
administrator_smoke = importlib.util.module_from_spec(administrator_spec)
administrator_spec.loader.exec_module(administrator_smoke)
history_spec = importlib.util.spec_from_file_location("history_smoke", Path(__file__).with_name("smoke-task-history.py"))
history_smoke = importlib.util.module_from_spec(history_spec)
history_spec.loader.exec_module(history_smoke)
submission_spec = importlib.util.spec_from_file_location("submission_smoke", Path(__file__).with_name("smoke-task-submission.py"))
submission_smoke = importlib.util.module_from_spec(submission_spec)
submission_spec.loader.exec_module(submission_smoke)
group_spec = importlib.util.spec_from_file_location("group_ingress_smoke", Path(__file__).with_name("smoke-group-ingress.py"))
group_smoke = importlib.util.module_from_spec(group_spec)
group_spec.loader.exec_module(group_smoke)
listener_spec = importlib.util.spec_from_file_location("listener_smoke", Path(__file__).with_name("smoke-group-listener.py"))
listener_smoke = importlib.util.module_from_spec(listener_spec)
listener_spec.loader.exec_module(listener_smoke)
spool_spec = importlib.util.spec_from_file_location("spool_smoke", Path(__file__).with_name("smoke-group-spool.py"))
spool_smoke = importlib.util.module_from_spec(spool_spec)
spool_spec.loader.exec_module(spool_smoke)
reference_spec = importlib.util.spec_from_file_location("reference_smoke", Path(__file__).with_name("smoke-group-reference.py"))
reference_smoke = importlib.util.module_from_spec(reference_spec)
reference_spec.loader.exec_module(reference_smoke)
recovery_spec = importlib.util.spec_from_file_location("recovery_host_smoke", Path(__file__).with_name("smoke-group-recovery-host.py"))
recovery_smoke = importlib.util.module_from_spec(recovery_spec)
recovery_spec.loader.exec_module(recovery_smoke)


class OwnedStackGuardTests(unittest.TestCase):
    def brain_fixture(self):
        value = {field: str(uuid.uuid4()) for field in ("requestId", "glossaryId", "publisherId", "batchId", "messageId")}
        value.update({"claimEpoch": 4, "sourceVersion": 1, "deletionGeneration": 0, "messageRevision": 1,
            "credentialEpoch": 1, "grantVersion": 1, "accountVersion": 1, "createdAtUtc": "2026-10-10T16:00:00.0000001+00:00"})
        for kind in ("request", "glossary"):
            cipher = bytes([1]) + bytes([0x32 if kind == "request" else 0x33]) * 40
            value[kind + "Envelope"] = cipher.hex().upper()
            value[kind + "Hash"] = hashlib.sha256(cipher).hexdigest().upper()
        value["sourceSetHash"] = hashlib.sha256(f"{value['messageId']}/1".encode("ascii")).hexdigest().upper()
        return value

    def test_brain_fixture_accepts_only_closed_bounded_cipher_metadata(self):
        value = self.brain_fixture()
        self.assertEqual(value, reference_smoke.validated_brain_fixture(json.dumps(value)))
        self.assertEqual(18, len(value))

    def test_brain_fixture_rejects_sql_shaped_or_unbounded_or_inconsistent_metadata(self):
        for field, invalid in (("requestId", "'; DROP TABLE aioffice.Tasks;--"), ("glossaryId", str(uuid.UUID(int=0))),
                ("publisherId", None), ("claimEpoch", 3), ("claimEpoch", True), ("sourceVersion", 0),
                ("deletionGeneration", -1), ("grantVersion", 9223372036854775808), ("accountVersion", 1.0),
                ("messageRevision", 0), ("createdAtUtc", "2026-10-10T16:00:00.0000001+00:00'; PRIVATE"),
                ("createdAtUtc", "2026-10-10T16:00:00.0000001+01:00"), ("createdAtUtc", "2026-99-10T16:00:00.0000001+00:00"),
                ("requestEnvelope", "01" * 1025), ("requestEnvelope", "01" * 29), ("requestEnvelope", "02" * 30),
                ("requestHash", "F" * 64), ("glossaryHash", "F" * 64), ("sourceSetHash", "F" * 64),
                ("glossaryEnvelope", None), ("PRIVATE_KEY", "PRIVATE")):
            with self.subTest(field=field, invalid=invalid):
                value = self.brain_fixture(); value[field] = invalid
                with self.assertRaises((AssertionError, ValueError)): reference_smoke.validated_brain_fixture(json.dumps(value))

    def test_brain_fixture_rejects_duplicate_fields_before_sql_and_total_output_overflow(self):
        raw = json.dumps(self.brain_fixture(), separators=(",", ":"))
        duplicate = raw.replace('"claimEpoch":4', '"claimEpoch":4,"claimEpoch":4')
        for invalid in (duplicate, raw + " " * 8192, "[]", "null"):
            with self.assertRaises(AssertionError): reference_smoke.validated_brain_fixture(invalid)

    def work_schema_fixture(self, fail=False):
        tree = ast.parse(Path(reference_smoke.__file__).read_text(encoding="utf-8"))
        functions = [node for node in ast.walk(tree) if isinstance(node, ast.FunctionDef) and node.name in
            ("work_permission_catalog", "unsafe_work_column")]
        loops = [node for node in ast.walk(tree) if isinstance(node, ast.For) and isinstance(node.target, ast.Tuple)
            and [getattr(x, "id", None) for x in node.target.elts] == ["table", "column"]
            and any(isinstance(x, ast.Constant) and x.value == "GroupCustomerRequests" for x in ast.walk(node.iter))]
        self.assertEqual(2, len(functions)); self.assertEqual(1, len(loops))
        allowed = {"GroupCustomerRequests": "RequestCode", "GroupEditorGrants": "IsEnabled"}
        permissions = {name: False for name in allowed}; calls = []
        def sql(command):
            calls.append(command)
            table = next((name for name in allowed if name in command), None)
            self.assertIsNotNone(table)
            if "sys.database_permissions" in command:
                self.assertIn("grantee_principal_id=DATABASE_PRINCIPAL_ID(N'aioffice_binding_runtime')", command)
                self.assertIn("ORDER BY minor_id,type", command)
                return ("B" if permissions[table] else "A") * 64
            if command.startswith("GRANT UPDATE"): permissions[table] = True; return ""
            if command.startswith("DENY UPDATE"): permissions[table] = False; return ""
            self.assertTrue(command.startswith("EXECUTE AS LOGIN=N'aioffice_runtime';"))
            self.assertTrue(command.endswith(" REVERT;")); self.assertIn("N'COLUMN'", command)
            self.assertIn("N'" + allowed[table] + "'", command)
            return "1" if permissions[table] else "0"
        runs = []
        def run(mode):
            runs.append(mode)
            if mode == "work-unsafe":
                self.assertEqual(1, sum(permissions.values()))
                if fail: raise RuntimeError("Owned fixture failure")
            else:
                self.assertEqual("work-schema", mode); self.assertFalse(any(permissions.values()))
        namespace = {"sql": sql, "run": run, "re": re, "temporary_sql": reference_smoke.temporary_sql}
        exec(compile(ast.Module(body=functions, type_ignores=[]), '<actual-work-schema-functions>', 'exec'), namespace)
        return namespace, permissions, calls, runs, compile(ast.Module(body=loops, type_ignores=[]), '<actual-work-schema-loop>', 'exec')

    def test_work_schema_effective_column_escalations_require_exact_catalog_restore_and_positive(self):
        namespace, permissions, calls, runs, loop = self.work_schema_fixture()
        exec(loop, namespace)
        self.assertEqual(["work-unsafe", "work-schema", "work-unsafe", "work-schema"], runs)
        self.assertFalse(any(permissions.values()))
        self.assertEqual(2, sum(command.startswith("GRANT UPDATE") for command in calls))
        self.assertEqual(2, sum(command.startswith("DENY UPDATE") for command in calls))
        self.assertEqual(4, sum("sys.database_permissions" in command for command in calls))

    def test_work_schema_failed_unsafe_control_restores_original_permission_and_cannot_run_positive(self):
        namespace, permissions, calls, runs, loop = self.work_schema_fixture(fail=True)
        with self.assertRaisesRegex(RuntimeError, "^Owned fixture failure$"): exec(loop, namespace)
        self.assertFalse(any(permissions.values())); self.assertEqual(["work-unsafe"], runs)
        self.assertEqual(1, sum(command.startswith("DENY UPDATE") for command in calls))

    def test_work_schema_catalog_refuses_foreign_table_before_sql(self):
        namespace, _, calls, _, _ = self.work_schema_fixture()
        for table in ("Tasks", "GroupGlossaryRevisions", "GroupCustomerRequests; PRIVATE", None):
            with self.assertRaises(AssertionError): namespace["work_permission_catalog"](table)
        self.assertEqual([], calls)

    def test_source_checkpoint_type_binding_waits_for_container_without_image_fallback(self):
        tree = ast.parse(Path(reference_smoke.__file__).read_text(encoding="utf-8"))
        functions = [node for node in ast.walk(tree) if isinstance(node, ast.FunctionDef) and node.name in
            ("source_reader_container", "source_reader_awaiting")]
        self.assertEqual(2, len(functions))
        compiled = compile(ast.Module(body=functions, type_ignores=[]), '<actual-source-type-binding>', 'exec')
        identity, label = "a" * 64, "b" * 32
        calls = []
        created = False
        def run(arguments, **kwargs):
            calls.append(arguments)
            if arguments[:2] == ["docker", "inspect"]:
                typed = arguments[2:4] == ["--type", "container"]
                if not created:
                    return SimpleNamespace(returncode=1 if typed else 0,
                        stdout="" if typed else "sha256:" + identity + "|<no value>")
                return SimpleNamespace(returncode=0, stdout=identity + "|" + label)
            self.assertEqual(["docker", "exec", identity, "test", "-f", "/tmp/aioffice-source-proof-awaiting"], arguments)
            return SimpleNamespace(returncode=0)
        namespace = {"subprocess": SimpleNamespace(run=run), "re": re, "name": "aioffice-reference-proof-" + label, "suffix": label}
        exec(compiled, namespace)
        self.assertFalse(namespace["source_reader_awaiting"]())
        self.assertEqual(1, len(calls))
        created = True
        self.assertTrue(namespace["source_reader_awaiting"]())
        self.assertEqual(3, len(calls))
        self.assertEqual(["--type", "container"], calls[0][2:4])
        self.assertEqual(["--type", "container"], calls[1][2:4])

    def test_owned_kill_type_binding_cannot_select_same_named_image(self):
        tree = ast.parse(Path(reference_smoke.__file__).read_text(encoding="utf-8"))
        functions = [node for node in ast.walk(tree) if isinstance(node, ast.FunctionDef) and node.name == "kill_owned"]
        self.assertEqual(1, len(functions))
        compiled = compile(ast.Module(body=functions, type_ignores=[]), '<actual-kill-type-binding>', 'exec')
        identity, label = "a" * 64, "b" * 32
        calls = []
        created = False
        def run(arguments, **kwargs):
            calls.append(arguments)
            if arguments[:2] == ["docker", "inspect"]:
                typed = arguments[2:4] == ["--type", "container"]
                if not created:
                    return SimpleNamespace(returncode=1 if typed else 0,
                        stdout="" if typed else "sha256:" + identity + "|<no value>")
                return SimpleNamespace(returncode=0, stdout=identity + "|" + label)
            self.assertEqual(["docker", "kill", identity], arguments)
            return SimpleNamespace(returncode=0)
        namespace = {"subprocess": SimpleNamespace(run=run), "re": re, "name": "aioffice-reference-proof-" + label, "suffix": label}
        exec(compiled, namespace)
        self.assertFalse(namespace["kill_owned"]())
        self.assertEqual(1, len(calls))
        created = True
        self.assertTrue(namespace["kill_owned"]())
        self.assertEqual(3, len(calls))
        self.assertEqual(["--type", "container"], calls[0][2:4])
        self.assertEqual(["--type", "container"], calls[1][2:4])

    def test_reference_source_key_is_canonical_and_refusal_never_echoes_input(self):
        reference_smoke.require_source_key(base64.b64encode(bytes([0x32]) * 32).decode("ascii"))
        for value in (None, "", "owned-private-key-detail", "A" * 44, "A" * 43 + "!", base64.b64encode(bytes(31)).decode("ascii")):
            with self.subTest(kind=type(value).__name__):
                with self.assertRaisesRegex(RuntimeError, r"^Owned reference source key is unavailable\.$") as refusal:
                    reference_smoke.require_source_key(value)
                self.assertIsNone(refusal.exception.__cause__)

    def test_source_key_gate_checks_owned_container_before_observation_or_release(self):
        tree = ast.parse(Path(reference_smoke.__file__).read_text(encoding="utf-8"))
        functions = [node for node in ast.walk(tree) if isinstance(node, ast.FunctionDef) and node.name in
            ("source_reader_container", "source_reader_awaiting", "release_source_reader")]
        self.assertEqual(3, len(functions))
        compiled = compile(ast.Module(body=functions, type_ignores=[]), '<actual-source-reader-owned-gate>', 'exec')
        identity, label = "a" * 64, "b" * 32
        for inspected in (SimpleNamespace(returncode=1, stdout=""),
                SimpleNamespace(returncode=0, stdout=identity + "|wrong"),
                SimpleNamespace(returncode=0, stdout="g" * 64 + "|" + label)):
            calls = []
            def run(arguments, **kwargs):
                calls.append(arguments)
                self.assertEqual(["docker", "inspect"], arguments[:2])
                return inspected
            namespace = {"subprocess": SimpleNamespace(run=run), "re": re, "name": "aioffice-owned-source-reader", "suffix": label}
            exec(compiled, namespace)
            if inspected.returncode:
                self.assertFalse(namespace["source_reader_awaiting"]())
                with self.assertRaises(AssertionError): namespace["release_source_reader"]()
            else:
                with self.assertRaises(AssertionError): namespace["source_reader_awaiting"]()
                with self.assertRaises(AssertionError): namespace["release_source_reader"]()
            self.assertEqual(2, len(calls))

    def test_source_key_release_reinspects_identity_and_requires_exact_terminal_reply(self):
        tree = ast.parse(Path(reference_smoke.__file__).read_text(encoding="utf-8"))
        functions = [node for node in ast.walk(tree) if isinstance(node, ast.FunctionDef) and node.name in
            ("source_reader_container", "release_source_reader")]
        compiled = compile(ast.Module(body=functions, type_ignores=[]), '<actual-source-reader-release>', 'exec')
        identity, label = "a" * 64, "b" * 32
        for fault in (None, "release", "reply"):
            calls = []
            def run(arguments, **kwargs):
                calls.append(arguments)
                if arguments[:2] == ["docker", "inspect"]:
                    return SimpleNamespace(returncode=0, stdout=identity + "|" + label)
                self.assertEqual(["docker", "exec", identity, "sh", "-c", ": > /tmp/aioffice-source-proof-release"], arguments)
                return SimpleNamespace(returncode=1 if fault == "release" else 0)
            held = SimpleNamespace(returncode=0, communicate=lambda **kwargs: ("unexpected" if fault == "reply" else
                "CHECKPOINT owned source key resolved outside SQL before final fence\n"
                "PASS owned source runtime current Extract revocation during key await denies private context before decrypt\n", ""))
            namespace = {"subprocess": SimpleNamespace(run=run), "re": re, "name": "aioffice-owned-source-reader", "suffix": label, "hold": held}
            exec(compiled, namespace)
            if fault:
                with self.assertRaises(AssertionError): namespace["release_source_reader"]()
            else:
                namespace["release_source_reader"]()
            self.assertEqual(["docker", "inspect"], calls[0][:2]); self.assertEqual(2, len(calls))

    root = (Path.cwd() / "guard-test-no-resources").resolve()
    owned = root / "aioffice-local"
    environment = {"CI": "true", "GITHUB_ACTIONS": "true", "RUNNER_TEMP": str(root)}

    def test_managed_recovery_refuses_unowned_before_credentials_files_sql_or_processes(self):
        for directory, override, api in [(self.owned, {"CI": "false"}, "http://127.0.0.1:8080"),
                (self.owned, {"GITHUB_ACTIONS": "false"}, "http://127.0.0.1:8080"),
                (self.owned, {"RUNNER_TEMP": ""}, "http://127.0.0.1:8080"),
                (self.root, {}, "http://127.0.0.1:8080"), (self.owned / "nested", {}, "http://127.0.0.1:8080"),
                (self.owned, {}, "https://foreign.invalid")]:
            with self.subTest(override=override), patch.dict(os.environ, {**self.environment, **override}), \
                    patch.object(recovery_smoke.subprocess, "run") as process, patch.object(Path, "mkdir") as directory_create, \
                    patch.object(recovery_smoke.secrets, "token_bytes") as key:
                sql = lambda *args: self.fail("Unowned recovery touched SQL")
                with self.assertRaisesRegex(RuntimeError, "owned disposable GitHub CI fixture"):
                    recovery_smoke.verify(directory=directory, api=api, tenant="INVALID", company="INVALID", service="INVALID",
                        key=None, runtime_password=None, sql=sql, restart=sql, ready=sql, identity_index=sql, enroll_source=sql)
                process.assert_not_called(); directory_create.assert_not_called(); key.assert_not_called()

    def test_managed_recovery_cleanup_attempts_each_owned_resource_and_retains_first_failure(self):
        tree = ast.parse(Path(recovery_smoke.__file__).read_text(encoding="utf-8"))
        function = next(node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name == "verify")
        boundary = next(node for node in function.body if isinstance(node, ast.Try) and node.finalbody)
        wrapper = ast.FunctionDef(name="cleanup", args=ast.arguments(posonlyargs=[], args=[ast.arg(arg="failure")],
            kwonlyargs=[], kw_defaults=[], defaults=[]), body=boundary.finalbody + [ast.Return(value=ast.Name(id="failure", ctx=ast.Load()))],
            decorator_list=[])
        block = compile(ast.fix_missing_locations(ast.Module(body=[wrapper], type_ignores=[])), '<actual-managed-cleanup>', 'exec')
        for fault in (None, "poll", "kill", "drain", "timeout", "timeout-kill", "timeout-drain", "shutdown", "close", "join"):
            for earlier in (False, True):
                effects = []; first = RuntimeError("earlier-owned-failure") if earlier else None
                injected = RuntimeError(fault) if fault is not None else None
                timeout = recovery_smoke.subprocess.TimeoutExpired("owned-child", 5)
                drain_calls = kill_calls = 0
                def effect(name):
                    effects.append(name)
                    if name == fault: raise injected
                def poll():
                    effect("poll"); return None
                def kill():
                    nonlocal kill_calls
                    kill_calls += 1; effect("kill")
                    if fault == "timeout-kill" and kill_calls == 2: raise injected
                def communicate(*, timeout):
                    nonlocal drain_calls
                    self.assertEqual(5, timeout)
                    drain_calls += 1; effect("drain")
                    if fault in ("timeout", "timeout-kill", "timeout-drain"):
                        if drain_calls == 1: raise namespace['owned_timeout']
                        if fault == "timeout-drain": raise injected
                process = SimpleNamespace(poll=poll, kill=kill, communicate=communicate)
                proxy = SimpleNamespace(shutdown=lambda: effect("shutdown"), server_close=lambda: effect("close"))
                serving = SimpleNamespace(is_alive=lambda: True, ident=1, join=lambda **kwargs: effect("join"))
                namespace = dict(process=process, proxy=proxy, serving=serving, subprocess=recovery_smoke.subprocess,
                    owned_timeout=timeout, release=SimpleNamespace(set=lambda: effect("release")))
                exec(block, namespace)
                result = namespace['cleanup'](first)
                self.assertEqual(["shutdown", "close", "join"], effects[-3:])
                self.assertIn("kill", effects)
                self.assertIn("drain", effects)
                self.assertEqual(2 if fault in ("timeout", "timeout-kill", "timeout-drain") else 1, drain_calls)
                self.assertEqual(2 if fault in ("timeout", "timeout-kill", "timeout-drain") else 1, kill_calls)
                if earlier: self.assertIs(first, result)
                else: self.assertIs(timeout if fault in ("timeout", "timeout-kill", "timeout-drain") else injected, result)

    def test_reference_refuses_unowned_before_configuration_sql_or_processes(self):
        for directory, override, api in [(self.owned, {"CI": "false"}, "http://127.0.0.1:8080"),
                (self.owned, {"GITHUB_ACTIONS": "false"}, "http://127.0.0.1:8080"),
                (self.owned, {"RUNNER_TEMP": ""}, "http://127.0.0.1:8080"),
                (self.owned / "nested", {}, "http://127.0.0.1:8080"), (self.root, {}, "http://127.0.0.1:8080"),
                (self.owned, {}, "https://customer.example.invalid")]:
            with self.subTest(directory=str(directory), override=override, api=api), patch.dict(os.environ, {**self.environment, **override}, clear=True):
                with patch.object(reference_smoke.subprocess, "run") as process:
                    with self.assertRaisesRegex(RuntimeError, r"^Group reference proof requires the owned disposable GitHub CI fixture\.$"):
                        reference_smoke.verify(directory=directory, api=api, manifest=None, tenant=None, company=None,
                            service=None, source=None, sql=None, compose=None, environment=None, pipeline=None)
                    process.assert_not_called()

    def test_reference_pipeline_actual_disable_attempts_worker_even_when_core_restore_fails(self):
        tree = ast.parse(Path(group_smoke.__file__).read_text(encoding="utf-8"))
        function = next(node for node in ast.walk(tree) if isinstance(node, ast.FunctionDef) and node.name == "reference_pipeline")
        block = compile(ast.Module(body=[function], type_ignores=[]), '<actual-reference-pipeline>', 'exec')
        for failure in ("write", "chmod", "core-api", "agent-worker", "write-and-worker", None):
            calls = []; ready_calls = []
            class Override:
                def write_text(self, value, encoding):
                    self.value = json.loads(value)
                    if failure in ("write", "write-and-worker"): raise RuntimeError("write")
                def chmod(self, mode):
                    self.mode = mode
                    if failure == "chmod": raise RuntimeError("chmod")
            override = Override()
            def compose_run(*args, **kwargs):
                calls.append((args, kwargs))
                if failure in args or failure == "write-and-worker" and "agent-worker" in args:
                    raise RuntimeError(args[-1])
            namespace = dict(json=json, tenant=str(uuid.uuid4()), company=str(uuid.uuid4()), service=str(uuid.uuid4()),
                private_environment={"OWNED_SECRET": "private-inert", "AIOffice__GroupIntake__Enabled": "true"},
                override=override, compose_run=compose_run, ready=lambda: ready_calls.append("ready"))
            exec(block, namespace)
            if failure:
                with self.assertRaisesRegex(RuntimeError, '^' + ("write" if failure == "write-and-worker" else failure) + '$'):
                    namespace['reference_pipeline']('disable')
            else: namespace['reference_pipeline']('disable')
            self.assertEqual(['agent-worker'] if failure in ("write", "chmod", "write-and-worker") else ['core-api', 'agent-worker'],
                [args[-1] for args, _ in calls])
            self.assertFalse(calls[-1][1])
            if len(calls) == 2: self.assertTrue(calls[0][1]['overridden'])
            self.assertEqual([] if failure else ["ready"], ready_calls)
            self.assertEqual({'services': {'core-api': {'environment': namespace['private_environment']}}}, override.value)
            self.assertNotIn('AIOffice__GroupIntake__PipelineEnabled', override.value['services']['core-api']['environment'])

    def test_reference_temporary_sql_owns_lost_setup_reply_and_independent_restoration(self):
        class InjectedFailure(Exception): pass
        for fault in (None, "setup", "action", "restore-first", "restore-second", "setup-and-restore"):
            with self.subTest(fault=fault):
                effects = []
                def sql(statement):
                    effects.append(statement)
                    if statement == "setup" and fault in ("setup", "setup-and-restore"): raise InjectedFailure("setup")
                    if statement == "restore-first" and fault in ("restore-first", "setup-and-restore"): raise InjectedFailure("restore-first")
                    if statement == "restore-second" and fault == "restore-second": raise InjectedFailure("restore-second")
                def action():
                    effects.append("action")
                    if fault == "action": raise InjectedFailure("action")
                if fault:
                    with self.assertRaises(InjectedFailure) as failure:
                        reference_smoke.temporary_sql(sql, "setup", ["restore-first", "restore-second"], action)
                    self.assertEqual("setup" if fault == "setup-and-restore" else fault, str(failure.exception))
                else: reference_smoke.temporary_sql(sql, "setup", ["restore-first", "restore-second"], action)
                self.assertEqual(["restore-first", "restore-second"], effects[-2:])
                self.assertEqual(fault not in ("setup", "setup-and-restore"), "action" in effects)

    def test_reference_queued_gate_cleanup_survives_every_setup_and_drain_boundary(self):
        tree = ast.parse(Path(reference_smoke.__file__).read_text(encoding="utf-8"))
        function = next(node for node in ast.walk(tree) if isinstance(node, ast.FunctionDef) and node.name == "queued_extract_revoke")
        block = compile(ast.Module(body=[function], type_ignores=[]), '<actual-reference-lock-cleanup>', 'exec')
        class InjectedFailure(Exception): pass
        for fault in (None, "create", "popen", "stdin-write", "stdin-close", "executor", "observation", "pending",
                "release", "drain", "drain-and-kill", "shutdown", "restore", "drop", "observation-and-restore"):
            with self.subTest(fault=fault):
                effects = []; drain_calls = 0
                def effect(name):
                    effects.append(name)
                    if fault == name or fault == "observation-and-restore" and name in ("observation", "restore"):
                        raise InjectedFailure(name)
                class Input:
                    def write(self, value): effect("stdin-write")
                    def close(self): effect("stdin-close")
                class Locker:
                    stdin = Input(); returncode = 0
                    def communicate(self, timeout):
                        nonlocal drain_calls
                        drain_calls += 1; effects.append("drain")
                        if drain_calls == 1 and fault in ("drain", "drain-and-kill"): raise InjectedFailure("drain")
                        return ("", "")
                    def kill(self):
                        effects.append("kill")
                        if fault == "drain-and-kill": raise InjectedFailure("kill")
                class Pending:
                    def result(self, timeout): effect("pending")
                class Executor:
                    def __init__(self, max_workers): effect("executor")
                    def submit(self, *args): effect("submit"); return Pending()
                    def shutdown(self, **kwargs): effect("shutdown")
                def popen(*args, **kwargs): effect("popen"); return Locker()
                def sql(query):
                    if query.startswith("CREATE TABLE"): effect("create"); return ""
                    if query.startswith("IF OBJECT_ID") and "UPDATE" in query: effect("release"); return ""
                    if query.startswith("IF OBJECT_ID") and "DROP" in query: effect("drop"); return ""
                    if "SET IsEnabled=1" in query: effect("restore"); return ""
                    if "SET IsEnabled=0" in query: effect("revoke"); return ""
                    if "sys.dm_tran_locks" in query: effect("observation"); return "1"
                    self.fail("Unexpected owned inert lock SQL")
                namespace = dict(sql=sql, suffix="a" * 32, scope="owned-scope", grant="owned-grant", compose=["owned-compose"],
                    environment={}, subprocess=SimpleNamespace(Popen=popen, PIPE=None), ThreadPoolExecutor=Executor,
                    wait=lambda predicate, seconds: self.assertTrue(predicate()), run=lambda *args: None)
                exec(block, namespace)
                if fault:
                    with self.assertRaises(InjectedFailure) as failure: namespace['queued_extract_revoke']()
                    expected = "drain" if fault == "drain-and-kill" else "observation" if fault == "observation-and-restore" else fault
                    self.assertEqual(expected, str(failure.exception))
                else: namespace['queued_extract_revoke']()
                self.assertIn("release", effects); self.assertIn("restore", effects); self.assertIn("drop", effects)
                if fault not in ("create", "popen"): self.assertIn("drain", effects)
                if fault not in ("create", "popen", "stdin-write", "stdin-close", "executor"): self.assertIn("shutdown", effects)
                if fault in ("drain", "drain-and-kill"):
                    self.assertIn("kill", effects); self.assertEqual(2, drain_calls)

    def test_reference_restart_requires_new_broker_delivery_and_ack_with_same_full_sql_graph(self):
        tree = ast.parse(Path(reference_smoke.__file__).read_text(encoding="utf-8"))
        verify = next(node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name == "verify")
        attempt = next(node for node in verify.body if isinstance(node, ast.Try) and any(isinstance(child, ast.FunctionDef)
            and child.name == "unsafe_column" for child in node.body))
        start = next(i for i, node in enumerate(attempt.body) if isinstance(node, ast.Assign) and isinstance(node.targets[0], ast.Name) and node.targets[0].id == "stable") + 1
        end = next(i for i in range(start, len(attempt.body)) if isinstance(attempt.body[i], ast.Assert)
            and isinstance(attempt.body[i].test, ast.BoolOp) and "full_graph" in ast.unparse(attempt.body[i])) + 1
        block = compile(ast.Module(body=attempt.body[start:end], type_ignores=[]), '<actual-reference-restart-oracle>', 'exec')
        for fault in (None, "no-consumer", "dead-consumer", "no-publication", "wrong-deliver", "pending-queue", "changed-receipt"):
            with self.subTest(fault=fault):
                calls = []; state = {"restarted": False, "published": False}
                def pipeline(operation): calls.append(operation); state["restarted"] = True
                def run(mode): calls.append(mode); state["published"] = True
                def stats():
                    resumed = state["published"] and fault not in ("dead-consumer", "no-publication")
                    return {"ack": 3 if resumed else 2, "deliver": 4 if resumed and fault != "wrong-deliver" else 3,
                        "consumers": 0 if state["restarted"] and fault == "no-consumer" else 1}
                namespace = dict(wait=lambda predicate: self.assertTrue(predicate()), broker_stats=stats, pipeline=pipeline, run=run,
                    wait_reference_statistics=reference_smoke.wait_reference_statistics,
                    stable=["same-receipt"], count=lambda: 2, full_graph=lambda: ["changed-receipt" if fault == "changed-receipt" else "same-receipt"],
                    queue_counts=lambda: (1, 0) if fault == "pending-queue" else (0, 0))
                if fault:
                    with self.assertRaises(AssertionError): exec(block, namespace)
                else:
                    exec(block, namespace); self.assertEqual(["restart", "publish-existing"], calls)

    def test_application_restart_observes_exact_baseline_and_delivery_in_one_snapshot(self):
        tree = ast.parse(Path(reference_smoke.__file__).read_text(encoding="utf-8"))
        verify = next(node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name == "verify")
        attempt = next(node for node in verify.body if isinstance(node, ast.Try) and any(isinstance(child, ast.FunctionDef)
            and child.name == "unsafe_column" for child in node.body))
        start = next(i for i, node in enumerate(attempt.body) if isinstance(node, ast.Assign)
            and isinstance(node.targets[0], ast.Name) and node.targets[0].id == "stable") + 1
        end = next(i for i in range(start, len(attempt.body)) if isinstance(attempt.body[i], ast.Assert)
            and isinstance(attempt.body[i].test, ast.BoolOp) and "full_graph" in ast.unparse(attempt.body[i])) + 1
        block = compile(ast.Module(body=attempt.body[start:end], type_ignores=[]), '<actual-application-restart-statistics>', 'exec')
        cases = (
            ("delayed", True, [(0, -1, 0), (0, 0, 0), (0, 0, 1)]),
            ("disjoint", False, [(0, -1, 1), (-1, 0, 1), (0, -1, 1)]),
            ("extra-ack", False, [(0, -1, 1), (1, 0, 1), (1, 0, 1)]),
            ("extra-delivery", False, [(0, 1, 1)] * 3),
            ("missing-consumer", False, [(0, 0, 0)] * 3),
            ("extra-consumer", False, [(0, 0, 2)] * 3),
        )
        for phase in ("before", "after"):
            for case, succeeds, frames in cases:
                with self.subTest(phase=phase, case=case):
                    state = dict(published=False, restarted=False, observations=0)
                    snapshots = iter(frames)
                    calls = []
                    def pipeline(operation):
                        calls.append(operation); state["restarted"] = True
                    def run(mode):
                        calls.append(mode); state["published"] = True
                    def stats():
                        selected = phase == "before" and not state["restarted"] or phase == "after" and state["published"]
                        if not selected:
                            return dict(ack=3 if state["published"] else 2, deliver=4 if state["published"] else 3, consumers=1)
                        state["observations"] += 1
                        ack_offset, delivery_offset, consumers = next(snapshots)
                        return dict(ack=(3 if state["published"] else 2) + ack_offset,
                            deliver=(4 if state["published"] else 3) + delivery_offset, consumers=consumers)
                    def bounded_wait(predicate, seconds=30):
                        self.assertEqual(seconds, 30)
                        for _ in range(3):
                            if predicate(): return
                        raise AssertionError("Owned observation deadline exceeded")
                    namespace = dict(wait=bounded_wait, broker_stats=stats, pipeline=pipeline, run=run,
                        wait_reference_statistics=reference_smoke.wait_reference_statistics, stable=["unchanged-full8"],
                        count=lambda: 2, full_graph=lambda: ["unchanged-full8"], queue_counts=lambda: (0, 0))
                    if succeeds:
                        exec(block, namespace)
                        self.assertEqual(calls, ["restart", "publish-existing"])
                    else:
                        with self.assertRaisesRegex(AssertionError, f"Owned reference {phase} application restart observation failed") as raised:
                            exec(block, namespace)
                        self.assertEqual(str(raised.exception.__cause__), "Owned observation deadline exceeded")
                        self.assertIn("expected_ack=", str(raised.exception))
                        self.assertIn("expected_deliver=", str(raised.exception))
                        self.assertEqual(calls, [] if phase == "before" else ["restart", "publish-existing"])
                    self.assertEqual(state["observations"], 3)

    def test_pending_broker_restart_refuses_unowned_before_callbacks(self):
        def forbidden(*args): raise AssertionError("Owned guard touched resources")
        with patch.dict(os.environ, {**self.environment, "CI": "false"}, clear=True):
            with self.assertRaisesRegex(RuntimeError, "requires the owned disposable GitHub CI fixture"):
                reference_smoke.verify_pending_broker_restart(directory=self.owned, api="http://127.0.0.1:8080",
                    pause_worker=forbidden, resume_worker=forbidden, broker_state=forbidden,
                    restart_broker=forbidden, queue_counts=forbidden, broker_stats=forbidden,
                    publish=forbidden, inspect_pending=forbidden, full_graph=forbidden, count=forbidden, wait=forbidden)

    def test_pending_broker_restart_original_reference_survives_before_shipping_ack(self):
        faults = (None, "stop-reply-lost", "restart-failed", "different-broker", "same-incarnation", "unhealthy",
            "lost-message", "unexpected-consumer", "graph-after-pause", "graph-after-publish", "graph-after-restart",
            "graph-after-resume", "duplicate-effect", "no-ack", "wrong-delivery", "pending-after-ack", "resume-failed",
            "wrong-original-reference")
        for fault in faults:
            with self.subTest(fault=fault), patch.dict(os.environ, self.environment, clear=True):
                calls = []
                state = dict(paused=False, published=False, restarted=False, resumed=False)
                first = RuntimeError("Owned first operation failed")
                def pause():
                    calls.append("pause"); state["paused"] = True
                    if fault == "stop-reply-lost": raise first
                def resume():
                    calls.append("resume"); state["resumed"] = True
                    if fault == "resume-failed": raise first
                def publish():
                    self.assertTrue(state["paused"]); self.assertFalse(state["restarted"])
                    calls.append("publish"); state["published"] = True
                def restart(identity):
                    self.assertEqual(identity, "owned-broker"); self.assertTrue(state["published"])
                    calls.append("restart"); state["restarted"] = True
                    if fault == "restart-failed": raise first
                def inspect_pending():
                    self.assertTrue(state["published"]); self.assertFalse(state["resumed"])
                    calls.append("inspect")
                    if state["restarted"] and fault == "wrong-original-reference": raise AssertionError("Original pending reference changed")
                def broker():
                    return ("different" if state["restarted"] and fault == "different-broker" else "owned-broker",
                        2 if state["restarted"] and fault != "same-incarnation" else 1,
                        not (state["restarted"] and fault == "unhealthy"))
                def queue():
                    if not state["published"]: return (0, 0)
                    if state["resumed"]: return (1, 0) if fault == "pending-after-ack" else (0, 0)
                    return (0, 0) if state["restarted"] and fault == "lost-message" else (1, 0)
                def statistics():
                    resumed = state["resumed"]
                    # A fresh process may reset counters; require positive deltas.
                    return dict(ack=11 if resumed and fault != "no-ack" else 10,
                        deliver=22 if resumed and fault == "wrong-delivery" else 21 if resumed else 20,
                        consumers=1 if resumed or state["restarted"] and fault == "unexpected-consumer" else 0)
                def graph():
                    changed = any(state[phase] and fault == label for phase, label in (
                        ("paused", "graph-after-pause"), ("published", "graph-after-publish"),
                        ("restarted", "graph-after-restart"), ("resumed", "graph-after-resume")))
                    return ["changed" if changed else "original-eight-table-graph"]
                arguments = dict(directory=self.owned, api="http://127.0.0.1:8080", pause_worker=pause,
                    resume_worker=resume, broker_state=broker, restart_broker=restart, queue_counts=queue,
                    broker_stats=statistics, publish=publish, inspect_pending=inspect_pending, full_graph=graph,
                    count=lambda: 3 if state["resumed"] and fault == "duplicate-effect" else 2,
                    wait=lambda predicate, seconds=30: self.assertTrue(predicate()))
                if fault:
                    with self.assertRaises((AssertionError, RuntimeError)) as raised:
                        reference_smoke.verify_pending_broker_restart(**arguments)
                    if fault in ("stop-reply-lost", "restart-failed", "resume-failed"): self.assertIs(raised.exception, first)
                else: reference_smoke.verify_pending_broker_restart(**arguments)
                self.assertEqual(calls.count("resume"), 1)
                if not fault: self.assertEqual(calls, ["pause", "publish", "inspect", "restart", "inspect", "resume"])

    def test_pending_broker_restart_observes_all_exact_signals_together_within_existing_bound(self):
        sequences = (
            ("delayed", True, [(11, 20, 0), (11, 21, 0), (11, 21, 1)], None),
            ("disjoint", False, [(11, 20, 1), (10, 21, 1), (11, 20, 1)], "ack_delta=1, deliver_delta=0, consumers=1"),
            ("overshoot", False, [(12, 22, 1)] * 3, "ack_delta=2, deliver_delta=2, consumers=1"),
            ("ack-changed-between-samples", False, [(11, 20, 0), (12, 21, 1), (12, 21, 1)], "ack_delta=2, deliver_delta=1, consumers=1"),
            ("missing-consumer", False, [(11, 21, 0)] * 3, "ack_delta=1, deliver_delta=1, consumers=0"),
            ("extra-consumer", False, [(11, 21, 2)] * 3, "ack_delta=1, deliver_delta=1, consumers=2"),
        )
        for case, succeeds, snapshots, diagnostic in sequences:
            with self.subTest(case=case), patch.dict(os.environ, self.environment, clear=True):
                state = dict(published=False, restarted=False, resumed=False, observations=0)
                frames = iter(snapshots)
                def publish():
                    self.assertFalse(state["restarted"]); state["published"] = True
                def restart(identity):
                    self.assertEqual(identity, "owned"); state["restarted"] = True
                def resume(): state["resumed"] = True
                def statistics():
                    if not state["resumed"]: return dict(ack=10, deliver=20, consumers=0)
                    state["observations"] += 1
                    ack, deliver, consumers = next(frames)
                    return dict(ack=ack, deliver=deliver, consumers=consumers)
                def bounded_wait(predicate, seconds=30):
                    self.assertIn(seconds, (30, 60))
                    for _ in range(3):
                        if predicate(): return
                    raise AssertionError("Owned reference observation deadline exceeded")
                arguments = dict(directory=self.owned, api="http://127.0.0.1:8080", pause_worker=lambda: None,
                    resume_worker=resume, broker_state=lambda: ("owned", 2 if state["restarted"] else 1, True),
                    restart_broker=restart, queue_counts=lambda: (1, 0) if state["published"] and not state["resumed"] else (0, 0),
                    broker_stats=statistics, publish=publish, inspect_pending=lambda: None,
                    full_graph=lambda: ["original-eight-table-graph"], count=lambda: 2, wait=bounded_wait)
                if succeeds:
                    reference_smoke.verify_pending_broker_restart(**arguments)
                else:
                    with self.assertRaisesRegex(AssertionError, diagnostic) as raised:
                        reference_smoke.verify_pending_broker_restart(**arguments)
                    self.assertEqual(str(raised.exception.__cause__), "Owned reference observation deadline exceeded")
                self.assertEqual(state["observations"], 3)

    def test_pending_broker_restart_preserves_stop_failure_when_restoration_also_fails(self):
        first = RuntimeError("Owned stop reply lost")
        calls = []
        def stop(): calls.append("stop"); raise first
        def start(): calls.append("start"); raise RuntimeError("Owned restore failed")
        def forbidden(*args): raise AssertionError("Later operation ran after failure")
        with patch.dict(os.environ, self.environment, clear=True):
            with self.assertRaises(RuntimeError) as raised:
                reference_smoke.verify_pending_broker_restart(directory=self.owned, api="http://127.0.0.1:8080",
                    pause_worker=stop, resume_worker=start, broker_state=lambda: ("owned", 1, True),
                    restart_broker=forbidden, queue_counts=lambda: (0, 0), broker_stats=forbidden,
                    publish=forbidden, inspect_pending=forbidden, full_graph=lambda: ["unchanged"], count=lambda: 2, wait=forbidden)
        self.assertIs(raised.exception, first); self.assertEqual(calls, ["stop", "start"])

    def test_spool_proof_refuses_unowned_before_resources(self):
        cases = [(self.owned, "http://127.0.0.1:8080", {"CI": "false"}),
            (self.owned, "http://127.0.0.1:8080", {"GITHUB_ACTIONS": "false"}),
            (self.owned, "http://127.0.0.1:8080", {"RUNNER_TEMP": ""}),
            (self.root, "http://127.0.0.1:8080", {}), (self.owned / "nested", "http://127.0.0.1:8080", {}),
            (self.owned, "https://production.invalid/", {})]
        def forbidden(*args, **kwargs): self.fail("Unowned spool proof touched resources")
        for directory, api, override in cases:
            with self.subTest(directory=str(directory), override=override), patch.dict(os.environ, {**self.environment, **override}, clear=True), \
                    patch.object(spool_smoke.subprocess, "run", forbidden), patch.object(spool_smoke.subprocess, "Popen", forbidden), \
                    patch.object(spool_smoke.http.server, "ThreadingHTTPServer", forbidden), patch.object(Path, "mkdir", forbidden):
                with self.assertRaisesRegex(RuntimeError, "^Spool proof requires the owned disposable GitHub CI fixture\\.$"):
                    spool_smoke.verify(directory=directory, api=api, tenant="invalid", company="invalid", service="invalid",
                        key=None, sql=forbidden, restart=forbidden, ready=forbidden, identity_index=forbidden)

    def test_spool_proof_validates_owned_key_before_files_or_sql(self):
        def forbidden(*args, **kwargs): self.fail("Invalid proof key touched resources")
        with patch.dict(os.environ, self.environment, clear=True), patch.object(Path, "mkdir", forbidden):
            with self.assertRaisesRegex(RuntimeError, "^Owned spool signing key is unavailable\\.$"):
                spool_smoke.verify(directory=self.owned, api="http://127.0.0.1:8080", tenant=str(uuid.uuid4()),
                    company=str(uuid.uuid4()), service=str(uuid.uuid4()), key=b"bad",
                    sql=forbidden, restart=forbidden, ready=forbidden, identity_index=forbidden)

    def test_spool_proof_requires_owned_source_key_enrollment_before_resources(self):
        def forbidden(*args, **kwargs): self.fail("Missing source key enrollment touched resources")
        with patch.dict(os.environ, self.environment, clear=True), patch.object(Path, "mkdir", forbidden):
            with self.assertRaisesRegex(RuntimeError, "^Owned spool source key enrollment is unavailable\\.$"):
                spool_smoke.verify(directory=self.owned, api="http://127.0.0.1:8080", tenant=str(uuid.uuid4()),
                    company=str(uuid.uuid4()), service=str(uuid.uuid4()), key=b"x"*32,
                    sql=forbidden, restart=forbidden, ready=forbidden, identity_index=forbidden)

    def test_native_source_key_callback_preserves_original_enrollment_and_recreates_only_owned_core(self):
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
        namespace = dict(uuid=uuid, base64=base64, json=json, secrets=SimpleNamespace(token_bytes=lambda size: b"n"*size),
            tenant=tenant, company=company, private_environment=environment, override=Override(),
            compose_run=lambda *args, **kwargs: calls.append(("compose", args, kwargs)), ready=lambda: calls.append(("ready",)))
        exec(block, namespace)
        with self.assertRaises(ValueError): namespace["enroll_owned_source"]("invalid-source", 1)
        for slot in (0, 3, True, "1"):
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

    def test_spool_proxy_partial_startup_closes_allocated_resources_and_preserves_first_error(self):
        # Execute only the actual cleanup block with inert resources: no CI
        # flags, sockets, child processes, private configuration or SQL.
        tree = ast.parse(Path(spool_smoke.__file__).read_text(encoding="utf-8"))
        verify = next(node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name == "verify")
        start = next(index for index, node in enumerate(verify.body) if isinstance(node, ast.Assign)
            and any(isinstance(target, ast.Name) and target.id == "committed" for target in node.targets))
        end = next(index for index in range(start, len(verify.body)) if isinstance(verify.body[index], ast.If)
            and isinstance(verify.body[index].test, ast.Compare) and isinstance(verify.body[index].test.left, ast.Name)
            and verify.body[index].test.left.id == "failure")
        block = compile(ast.fix_missing_locations(ast.Module(verify.body[start:end + 1], type_ignores=[])), "<inert-spool-cleanup>", "exec")
        for fault in ("proxy-constructor", "thread-constructor", "thread-start", "save", "popen", "drain"):
            with self.subTest(fault=fault):
                actions, namespace = [], {}
                original = RuntimeError("owned-first-failure")
                class Event:
                    def set(self): actions.append("release")
                    def wait(self, timeout): return True
                class Proxy:
                    server_port = 12345
                    def __init__(self, *args):
                        actions.append("proxy")
                        if fault == "proxy-constructor": raise original
                    def serve_forever(self): pass
                    def shutdown(self): actions.append("shutdown")
                    def server_close(self):
                        actions.append("close")
                        # A later cleanup error must not hide the first fault.
                        raise RuntimeError("owned-secondary-cleanup-failure")
                class Thread:
                    ident = None
                    alive = False
                    def __init__(self, *args, **kwargs):
                        actions.append("thread")
                        if fault == "thread-constructor": raise original
                    def start(self):
                        self.ident, self.alive = 1, True
                        namespace["captured"]["ack"] = {}
                        if fault == "thread-start": raise original
                    def is_alive(self): return self.alive
                    def join(self, timeout): actions.append("join")
                class Process:
                    returncode = None
                    def __init__(self, *args, **kwargs):
                        actions.append("popen")
                        if fault == "popen": raise original
                    def poll(self): return self.returncode
                    def kill(self): actions.append("kill"); self.returncode = -9
                    def communicate(self, timeout):
                        actions.append("drain")
                        if fault == "drain" and actions.count("drain") == 1: raise original
                def save():
                    if fault == "save": raise original
                namespace.update(threading=SimpleNamespace(Event=Event, Thread=Thread),
                    http=SimpleNamespace(server=SimpleNamespace(BaseHTTPRequestHandler=object, ThreadingHTTPServer=Proxy)),
                    subprocess=SimpleNamespace(Popen=Process, PIPE=None), config={}, save_config=save,
                    executable="inert-never-executed.dll", child_environment={})
                with self.assertRaises(RuntimeError) as caught:
                    exec(block, namespace)
                self.assertIs(caught.exception, original)
                self.assertIn("release", actions)
                if fault != "proxy-constructor": self.assertIn("close", actions)
                if fault not in ("proxy-constructor", "thread-constructor"):
                    self.assertIn("shutdown", actions)
                    self.assertIn("join", actions)
                if fault == "drain": self.assertEqual(2, actions.count("drain"))

    def test_spool_proxy_forwards_actual_metadata_and_lease_bytes_but_only_withholds_event_ack(self):
        tree = ast.parse(Path(spool_smoke.__file__).read_text(encoding="utf-8"))
        handler = next(node for node in ast.walk(tree) if isinstance(node, ast.ClassDef) and node.name == "LostReply")
        block = compile(ast.fix_missing_locations(ast.Module([handler], type_ignores=[])), "<inert-spool-proxy>", "exec")
        tenant, company, source = (str(uuid.uuid4()) for _ in range(3))
        for path in ("enrollment", "listener", "events"):
            with self.subTest(path=path):
                actions, captured = [], {}
                raw = json.dumps({"source": {"tenantId": tenant, "companyId": company, "sourceBindingId": source},
                    "owned-inert-body": "\u00e9"}, separators=(",", ":")).encode()
                class Response:
                    status = 200
                    headers = {"Cache-Control": "no-store", "Content-Type": "application/json; charset=utf-8"}
                    def read(self, limit): self_test.assertEqual(8193, limit); return raw
                    def __enter__(self): return self
                    def __exit__(self, *args): actions.append("response-close")
                class Opener:
                    def open(self, request, timeout):
                        actions.append(("upstream", request.full_url, request.data, timeout))
                        return Response()
                self_test = self
                namespace = dict(http=SimpleNamespace(server=SimpleNamespace(BaseHTTPRequestHandler=object)),
                    urllib=spool_smoke.urllib, json=json, tenant=tenant, company=company, source=source,
                    api="http://127.0.0.1:8080", captured=captured,
                    committed=SimpleNamespace(set=lambda: actions.append("committed")),
                    release=SimpleNamespace(wait=lambda timeout: actions.append(("withheld", timeout))))
                exec(block, namespace)
                proxy = namespace["LostReply"]()
                proxy.path = "/internal/group-ingress/" + path
                proxy.headers = Message(); proxy.headers["Content-Length"] = "2"
                for name in ("Service", "Epoch", "Signed-At", "Nonce", "Signature"):
                    proxy.headers["X-AIOffice-Group-" + name] = "owned-inert-" + name
                proxy.rfile = io.BytesIO(b"{}")
                proxy.wfile = io.BytesIO()
                proxy.send_response = lambda code: actions.append(("status", code))
                proxy.send_header = lambda name, value: actions.append(("header", name, value))
                proxy.end_headers = lambda: actions.append("headers-end")
                with patch.object(spool_smoke.urllib.request, "build_opener", return_value=Opener()) as opener:
                    proxy.do_POST()
                self.assertEqual({}, opener.call_args.args[0].proxies)
                self.assertIsNone(opener.call_args.args[1].redirect_request(None, None, None, None, None, None))
                self.assertTrue(proxy.close_connection); self.assertNotIn("failed", captured)
                self.assertIn(("upstream", "http://127.0.0.1:8080" + proxy.path, b"{}", 12), actions)
                if path == "events":
                    self.assertEqual(b"", proxy.wfile.getvalue())
                    self.assertEqual(json.loads(raw), captured["ack"])
                    self.assertIn("committed", actions); self.assertIn(("withheld", 15), actions)
                    self.assertFalse(any(isinstance(action, tuple) and action[0] == "status" for action in actions))
                else:
                    self.assertEqual(raw, proxy.wfile.getvalue()); self.assertNotIn("ack", captured)
                    self.assertNotIn("committed", actions); self.assertEqual(1, captured[proxy.path])
                    self.assertIn(("header", "Content-Length", str(len(raw))), actions)
                    self.assertIn(("header", "Cache-Control", "no-store"), actions)
                    if path == "listener": self.assertEqual(json.loads(raw), captured["lease_ack"])

    def test_spool_proxy_refuses_unknown_path_or_oversized_metadata_before_upstream(self):
        tree = ast.parse(Path(spool_smoke.__file__).read_text(encoding="utf-8"))
        handler = next(node for node in ast.walk(tree) if isinstance(node, ast.ClassDef) and node.name == "LostReply")
        block = compile(ast.fix_missing_locations(ast.Module([handler], type_ignores=[])), "<inert-spool-proxy-refusal>", "exec")
        for path, length in (("enrollment?source=other", 2), ("enrollment", 8193), ("listener", 8193), ("events", 65537)):
            with self.subTest(path=path, length=length):
                captured = {}
                namespace = dict(http=SimpleNamespace(server=SimpleNamespace(BaseHTTPRequestHandler=object)),
                    urllib=spool_smoke.urllib, captured=captured,
                    committed=SimpleNamespace(set=lambda: None))
                exec(block, namespace)
                proxy = namespace["LostReply"](); proxy.path = "/internal/group-ingress/" + path
                proxy.headers = Message(); proxy.headers["Content-Length"] = str(length)
                with patch.object(spool_smoke.urllib.request, "build_opener", side_effect=AssertionError("unexpected upstream")) as opener:
                    proxy.do_POST()
                opener.assert_not_called()
                self.assertEqual({"failed": True, "failure_stage": "request-contract"}, captured)
                self.assertTrue(proxy.close_connection)

    def test_spool_latest_lease_oracle_rejects_foreign_account_owner_epoch_and_long_lease(self):
        tree = ast.parse(Path(spool_smoke.__file__).read_text(encoding="utf-8"))
        helper = next(node for node in ast.walk(tree) if isinstance(node, ast.FunctionDef) and node.name == "load_lease")
        block = compile(ast.fix_missing_locations(ast.Module([helper], type_ignores=[])), "<inert-native-lease>", "exec")
        tenant, company, account, owner = (str(uuid.uuid4()) for _ in range(4))
        good = {"lease": {"account": {"tenantId": tenant, "companyId": company, "connectorAccountId": account},
            "ownerId": owner, "epoch": 1, "heartbeatAtUtc": "2026-10-10T00:00:00Z", "expiresAtUtc": "2026-10-10T00:00:30Z"}}
        class Root:
            def __truediv__(self, name): self_test.assertEqual("lease.json", name); return self
            def read_text(self, encoding): return json.dumps(value)
        self_test = self
        namespace = dict(root=Root(), json=json, datetime=spool_smoke.datetime,
            tenant=tenant, company=company, account=account)
        exec(block, namespace)
        value = good
        self.assertEqual(good, namespace["load_lease"](owner, 1))
        # The retained child file can hold the first Renew. Passing the last
        # observed actual response must select its later expiry unchanged.
        latest = {"lease": {**good["lease"], "heartbeatAtUtc": "2026-10-10T00:00:03Z", "expiresAtUtc": "2026-10-10T00:00:33Z"}}
        self.assertEqual(latest, namespace["load_lease"](owner, 1, latest))
        self.assertEqual(good, value)
        for field, changed in (("account", {**good["lease"]["account"], "connectorAccountId": str(uuid.uuid4())}),
                ("ownerId", str(uuid.uuid4())), ("epoch", 2), ("expiresAtUtc", "2026-10-10T00:00:31Z"),
                ("expiresAtUtc", "2026-10-10T00:00:00Z"), ("heartbeatAtUtc", "2026-10-10T00:00:00+01:00")):
            with self.subTest(field=field, changed=changed):
                value = {"lease": {**good["lease"], field: changed}}
                with self.assertRaises(AssertionError): namespace["load_lease"](owner, 1)

    def test_listener_proof_refuses_unowned_before_resources(self):
        cases = [(self.owned, {"CI": "false"}), (self.owned, {"GITHUB_ACTIONS": "false"}),
            (self.owned, {"RUNNER_TEMP": ""}), (self.root, {}), (self.owned / "nested", {}),
            (self.root / "retained", {})]
        def forbidden(*args, **kwargs): self.fail("Unowned listener proof touched resources")
        for directory, override in cases:
            with self.subTest(directory=str(directory), override=override), patch.dict(os.environ, {**self.environment, **override}, clear=True):
                with self.assertRaisesRegex(RuntimeError, "^Listener proof requires the owned disposable GitHub CI fixture\\.$"):
                    listener_smoke.verify(directory=directory, api="http://127.0.0.1:8080", tenant="invalid", company="invalid",
                        user="invalid", service="invalid", key=None, sql=forbidden, compose=[], environment={},
                        restart=forbidden, ready=forbidden, identity_index=forbidden, read_route=forbidden)
        with patch.dict(os.environ, self.environment, clear=True):
            for api in ("https://production.example", "http://localhost:8080", "http://127.0.0.1:8081"):
                with self.assertRaises(RuntimeError): listener_smoke.require_owned(self.owned, api)
            listener_smoke.require_owned(self.owned, "http://127.0.0.1:8080")

    def test_listener_signature_never_reuses_ingress_domain_and_binds_captured_bytes(self):
        service, nonce = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"
        body = b'{"command":{"operation":1}}'
        listener = listener_smoke.signing_bytes(service, 1, 1700000000, nonce, body)
        ingress = group_smoke.signing_bytes(service, 1, 1700000000, nonce, body)
        self.assertEqual(listener.split(b"\n")[0], b"aioffice-group-listener-v1")
        self.assertNotEqual(listener, ingress)
        for changed in ((service, 2, 1700000000, nonce, body), (service, 1, 1700000001, nonce, body),
                (service, 1, 1700000000, "cccccccc-cccc-cccc-cccc-cccccccccccc", body),
                (service, 1, 1700000000, nonce, body + b" ")):
            self.assertNotEqual(listener, listener_smoke.signing_bytes(*changed))

    def test_listener_observed_fence_cleanup_attempts_all_resources_and_preserves_original_failure(self):
        tree = ast.parse(Path(listener_smoke.__file__).read_text(encoding="utf-8"))
        verify = next(node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name == "verify")
        helper = next(node for node in verify.body if isinstance(node, ast.FunctionDef) and node.name == "queued_revoke")
        module = ast.fix_missing_locations(ast.Module(body=[helper], type_ignores=[]))
        class InjectedFailure(Exception): pass
        for fault in ("create", "observation", "popen", "stdin_write", "stdin_close", "executor", "release", "drain", "shutdown", "restore", "drop"):
            with self.subTest(fault=fault):
                effects = []
                def effect(name):
                    effects.append(name)
                    if name == fault: raise InjectedFailure(name)
                class Input:
                    def write(self, query): effect("stdin_write")
                    def close(self): effect("stdin_close")
                class Process:
                    stdin = Input()
                    def communicate(self, timeout): effect("drain")
                    def kill(self): effect("kill")
                    def poll(self): return None
                def popen(*args, **kwargs): effect("popen"); return Process()
                class Executor:
                    def __init__(self, **kwargs): effect("executor")
                    def shutdown(self, **kwargs): effect("shutdown")
                def sql(query):
                    if "CREATE TABLE" in query: effect("create"); return ""
                    if "SET Released=1" in query: effect("release"); return ""
                    if query == "restore": effect("restore"); return ""
                    if "DROP TABLE" in query: effect("drop"); return ""
                    if "sys.dm_tran_locks" in query: effect("observation"); raise InjectedFailure("observation")
                    self.fail("Unexpected inert listener SQL")
                identifier = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"
                namespace = {"sql": sql, "snapshot": lambda: ["AB" * 32] * 3, "tenant": identifier, "company": identifier, "account": identifier,
                    "uuid": SimpleNamespace(UUID=listener_smoke.uuid.UUID, uuid4=lambda: SimpleNamespace(hex="probe")),
                    "subprocess": SimpleNamespace(Popen=popen, PIPE=None), "ThreadPoolExecutor": Executor,
                    "time": SimpleNamespace(monotonic=lambda: 0), "compose": ["owned-compose"], "environment": {}}
                exec(compile(module, "inert_listener_cleanup", "exec"), namespace)
                with self.assertRaises(InjectedFailure) as failure: namespace["queued_revoke"]("revoke", "restore")
                self.assertEqual(fault if fault in ("create", "popen", "stdin_write", "stdin_close", "executor") else "observation", str(failure.exception))
                self.assertIn("release", effects); self.assertIn("restore", effects); self.assertIn("drop", effects)
                if fault not in ("create", "popen"): self.assertIn("drain", effects)
                if fault not in ("create", "popen", "stdin_write", "stdin_close", "executor"): self.assertIn("shutdown", effects)

    def test_listener_temporary_sql_restores_applied_setup_with_lost_reply_and_keeps_first_error(self):
        tree = ast.parse(Path(listener_smoke.__file__).read_text(encoding="utf-8"))
        verify = next(node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name == "verify")
        helper = next(node for node in verify.body if isinstance(node, ast.FunctionDef) and node.name == "temporary_sql")
        module = ast.fix_missing_locations(ast.Module(body=[helper], type_ignores=[]))
        class InjectedFailure(Exception): pass
        for fault in ("setup", "body", "restore", "setup-and-restore"):
            with self.subTest(fault=fault):
                effects = []
                def sql(query):
                    effects.append(query + "-applied")
                    if query == "setup" and fault in ("setup", "setup-and-restore"): raise InjectedFailure("setup")
                    if query == "restore" and fault in ("restore", "setup-and-restore"): raise InjectedFailure("restore")
                def action():
                    effects.append("body")
                    if fault == "body": raise InjectedFailure("body")
                namespace = {"sql": sql}
                exec(compile(module, "inert_listener_temporary_sql", "exec"), namespace)
                with self.assertRaises(InjectedFailure) as failure: namespace["temporary_sql"]("setup", "restore", action)
                self.assertEqual("setup" if fault in ("setup", "setup-and-restore") else fault, str(failure.exception))
                self.assertIn("restore-applied", effects)
                self.assertEqual(fault not in ("setup", "setup-and-restore"), "body" in effects)

    def test_group_browser_diagnostics_retain_only_fixed_stage_or_bounded_http_status(self):
        prefix = "FAIL owned group Chromium inbox gate: "
        for stage in ("native-reader-grant-ui-refusal", "native-reader-grant-private-http-401", "native-reader-grant-catalog-http-503",
                "native-reader-grant-private-http-200"):
            self.assertEqual(stage, group_smoke.browser_failure_stage(prefix + stage))
        for stage in ("native-reader-grant-private-http-600", "native-reader-grant-private-http-99", "other-http-401",
                "native-reader-grant-private-http-401 PRIVATE_BODY", "PRIVATE_CREDENTIAL", "a" * 81, "native-reader-grant-ui-refusal\nPRIVATE_BODY"):
            self.assertIsNone(group_smoke.browser_failure_stage(prefix + stage))
        self.assertIsNone(group_smoke.browser_failure_stage("OTHER PRIVATE_BODY"))

    def test_listener_permission_oracle_accepts_redundant_deny_absence_and_requires_real_escalation(self):
        tree = ast.parse(Path(listener_smoke.__file__).read_text(encoding="utf-8"))
        verify = next(node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name == "verify")
        helper = next(node for node in verify.body if isinstance(node, ast.FunctionDef) and node.name == "temporary_sql")
        def assigns(node, name):
            return isinstance(node, ast.Assign) and any(isinstance(target, ast.Name) and target.id == name for target in node.targets)
        start = next(i for i, node in enumerate(verify.body) if assigns(node, "column_permission"))
        end = next(i for i in range(start, len(verify.body)) if isinstance(verify.body[i], ast.Assign)
            and isinstance(verify.body[i].targets[0], ast.Tuple))
        module = ast.fix_missing_locations(ast.Module(body=[helper, *verify.body[start:end]], type_ignores=[]))
        for baseline, fault in (("", None), ("D", None), ("G", "column"), ("", "table"),
                ("", "direct"), ("", "initial-effective"), ("", "ineffective-grant")):
            with self.subTest(baseline=baseline, fault=fault):
                effects = []; current = {"granted": False}
                def sql(query):
                    if query.startswith("GRANT UPDATE"):
                        effects.append("grant"); current["granted"] = True; return ""
                    if query.startswith("DENY UPDATE"):
                        effects.append("restore"); current["granted"] = False; return ""
                    if "HAS_PERMS_BY_NAME" in query:
                        return "1" if fault == "initial-effective" or current["granted"] and fault != "ineffective-grant" else "0"
                    if query.startswith("SELECT COUNT(*)"): return "1" if fault == "direct" else "0"
                    if "minor_id=0" in query: return "G" if fault == "table" else "D"
                    if query.startswith("SELECT state"): return "G" if current["granted"] else baseline
                    self.fail("Unexpected inert permission query")
                def no_effect(body):
                    self.assertTrue(current["granted"]); effects.append("refusal")
                original = {"wasAlreadyCommitted": False}
                namespace = {"sql": sql, "no_effect": no_effect, "command": lambda: b"owned-command", "body": b"owned-command",
                    "nonce": "owned-nonce", "original": original,
                    "signed_at": 1700000000, "renewal_count": 8,
                    "timing": lambda *args: self.assertFalse(current["granted"]),
                    "renew_and_replay": lambda: self.assertFalse(current["granted"]),
                    "renew_owned": lambda: self.assertFalse(current["granted"]),
                    "call": lambda *args, **kwargs: (200, {"wasAlreadyCommitted": True}), "receipt": lambda status, value, **kwargs: value}
                if fault is None:
                    exec(compile(module, "inert_listener_permissions", "exec"), namespace)
                    self.assertEqual(["grant", "refusal", "restore"], effects)
                else:
                    with self.assertRaises(AssertionError): exec(compile(module, "inert_listener_permissions", "exec"), namespace)
                    self.assertEqual(["grant", "restore"] if fault == "ineffective-grant" else [], effects)
                self.assertFalse(current["granted"])

    def test_listener_foreground_renewal_rejects_changed_history_extra_receipts_and_extended_duration(self):
        tree = ast.parse(Path(listener_smoke.__file__).read_text(encoding="utf-8"))
        verify = next(node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name == "verify")
        helper = next(node for node in verify.body if isinstance(node, ast.FunctionDef) and node.name == "renew_owned")
        factory = ast.parse("def factory():\n    renewal_count = 0\n    return None\n").body[0]
        factory.body.insert(1, helper)
        factory.body[-1].value = ast.Name(id='renew_owned', ctx=ast.Load())
        module = ast.fix_missing_locations(ast.Module(body=[factory], type_ignores=[]))
        for fault in (None, "gap", "receipt", "extra-receipt", "no-receipt", "duration", "denial"):
            with self.subTest(fault=fault):
                histories = [["A" * 64, "B" * 64, "4"], ["A" * 64, "B" * 64, "5"]]
                if fault == "gap": histories[1][0] = "C" * 64
                if fault == "receipt": histories[1][1] = "C" * 64
                if fault == "extra-receipt": histories[1][2] = "6"
                if fault == "no-receipt": histories[1][2] = "4"
                effects = []
                def history(nonce): effects.append(('history', nonce)); return histories.pop(0)
                def call(body, **kwargs): effects.append(('call', kwargs['nonce'])); return (403 if fault == "denial" else 200), {}
                def receipt(status, value, **kwargs):
                    self.assertEqual(200, status)
                    self.assertEqual({'process': 'owned-owner', 'epoch': 1, 'coverage': False}, kwargs)
                    return {'lease': {'heartbeatAtUtc': '2026-10-10T00:00:00+00:00',
                        'expiresAtUtc': '2026-10-10T00:00:' + ('31' if fault == "duration" else '30') + '+00:00'}}
                namespace = {'owner': 'owned-owner', 'command': lambda *args: b'owned-renew', 'uuid': uuid,
                    'time': SimpleNamespace(time=lambda: 1700000000), 'immutable_history': history,
                    'call': call, 'receipt': receipt, 'timestamp': listener_smoke.datetime.fromisoformat}
                exec(compile(module, 'actual-owned-renewal', 'exec'), namespace)
                action = namespace['factory']()
                if fault is None:
                    captured = action()
                    self.assertEqual(b'owned-renew', captured[0])
                    self.assertEqual([('history', captured[1]), ('call', captured[1]), ('history', captured[1])], effects)
                else:
                    with self.assertRaises(AssertionError): action()
                    self.assertEqual(1, sum(kind == 'call' for kind, _ in effects))

    def test_listener_renewal_history_refuses_malformed_metadata_without_echo(self):
        tree = ast.parse(Path(listener_smoke.__file__).read_text(encoding='utf-8'))
        verify = next(node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name == 'verify')
        helper = next(node for node in verify.body if isinstance(node, ast.FunctionDef) and node.name == 'immutable_history')
        module = ast.fix_missing_locations(ast.Module(body=[helper], type_ignores=[]))
        valid = 'A' * 64 + '|' + 'B' * 64 + '|5'
        for value in (valid, 'PRIVATE_CREDENTIAL', valid + '\nPRIVATE_CREDENTIAL', valid.replace('|5', '|-1'), valid.replace('A', 'g')):
            namespace = {'sql': lambda query: value, 'account_scope': 'owned-scope', 're': re}
            exec(compile(module, 'actual-renewal-history', 'exec'), namespace)
            if value == valid:
                self.assertEqual(['A' * 64, 'B' * 64, '5'], namespace['immutable_history']('owned-nonce'))
            else:
                with self.assertRaisesRegex(AssertionError, '^Invalid owned renewal history metadata$'):
                    namespace['immutable_history']('owned-nonce')

    def test_listener_restored_positive_replays_captured_renewal_and_requires_unchanged_graph(self):
        tree = ast.parse(Path(listener_smoke.__file__).read_text(encoding='utf-8'))
        verify = next(node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name == 'verify')
        helper = next(node for node in verify.body if isinstance(node, ast.FunctionDef) and node.name == 'renew_and_replay')
        module = ast.fix_missing_locations(ast.Module(body=[helper], type_ignores=[]))
        for fault in (None, 'ack', 'graph'):
            snapshots = iter((['original-graph'], ['changed-graph' if fault == 'graph' else 'original-graph']))
            def call(body, **kwargs):
                self.assertEqual(b'captured-renew', body)
                self.assertEqual({'nonce': 'captured-nonce', 'signed_at': 1700000000}, kwargs)
                return 200, {'wasAlreadyCommitted': True, 'changed': fault != 'ack'}
            def receipt(status, value, **kwargs):
                self.assertEqual({'coverage': False, 'already': True}, kwargs)
                return value
            namespace = {'renew_owned': lambda: (b'captured-renew', 'captured-nonce', 1700000000,
                {'wasAlreadyCommitted': False, 'changed': True}), 'snapshot': lambda: next(snapshots), 'call': call, 'receipt': receipt}
            exec(compile(module, 'actual-renewal-replay', 'exec'), namespace)
            if fault is None: namespace['renew_and_replay']()
            else:
                with self.assertRaises(AssertionError): namespace['renew_and_replay']()

    def test_listener_timing_uses_one_server_snapshot_and_only_fixed_flags(self):
        tree = ast.parse(Path(listener_smoke.__file__).read_text(encoding='utf-8'))
        verify = next(node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name == 'verify')
        helper = next(node for node in verify.body if isinstance(node, ast.FunctionDef) and node.name == 'timing')
        module = ast.fix_missing_locations(ast.Module(body=[helper], type_ignores=[]))
        for result in ('1|1|1', '0|1|1', '1|0|1', '1|1|0', 'PRIVATE_CREDENTIAL', '1|1|1\nPRIVATE_CREDENTIAL'):
            queries, messages = [], []
            def sql(query): queries.append(query); return result
            namespace = {'sql': sql, 're': re, 'print': messages.append,
                **{name: 'owned-id' for name in ('tenant', 'company', 'account', 'service')}}
            exec(compile(module, 'actual-owned-clock', 'exec'), namespace)
            if re.fullmatch(r'[01]\|[01]\|[01]', result):
                self.assertEqual(result, namespace['timing']('owned-nonce', 1700000000))
                self.assertEqual(['INFO owned listener timing expired-receipt/live-lease/fresh-signature=' + result], messages)
            else:
                with self.assertRaisesRegex(AssertionError, '^Invalid owned listener timing metadata$'):
                    namespace['timing']('owned-nonce', 1700000000)
                self.assertEqual([], messages)
            self.assertEqual(1, len(queries))
            self.assertEqual(1, queries[0].count('SYSUTCDATETIME()'))
            self.assertIn('DATEDIFF_BIG(millisecond', queries[0])
            self.assertIn('l.OwnerId=r.OwnerId AND l.Epoch=r.ListenerEpoch', queries[0])

    def test_final_group_read_race_cleanup_attempts_all_owned_resources_and_preserves_first_failure(self):
        # Execute only the shipping helper's orchestration with inert dependencies.
        # No files, SQL, processes, threads or HTTP are created by these controls.
        tree = ast.parse(Path(group_smoke.__file__).read_text(encoding="utf-8"))
        verify = next(node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name == "verify")
        helper = next(node for node in verify.body if isinstance(node, ast.FunctionDef) and node.name == "read_release_range")
        module = ast.fix_missing_locations(ast.Module(body=[helper], type_ignores=[]))

        class InjectedFailure(Exception): pass
        for fault in ("observation", "popen", "stdin_write", "stdin_close", "executor", "release", "drain", "shutdown", "restore", "drop"):
            with self.subTest(fault=fault):
                effects = []
                def effect(name):
                    effects.append(name)
                    if name == fault: raise InjectedFailure(name)
                class Input:
                    def write(self, query): effect("stdin_write")
                    def close(self): effect("stdin_close")
                class Process:
                    stdin = Input()
                    returncode = 0
                    def communicate(self, timeout): effect("drain")
                    def kill(self): effect("kill")
                    def poll(self): return None
                def popen(*args, **kwargs): effect("popen"); return Process()
                class Executor:
                    def __init__(self, **kwargs): effect("executor")
                    def shutdown(self, **kwargs): effect("shutdown")
                def sql(query):
                    if "SELECT CommittedSequence" in query: return "1"
                    if "HASHBYTES" in query: return "AB" * 32
                    if "CREATE TABLE" in query: effect("create"); return ""
                    if "SET Released=1" in query: effect("release"); return ""
                    if "UPDATE aioffice.GroupReaderGrants" in query: effect("restore"); return ""
                    if "DROP TABLE" in query: effect("drop"); return ""
                    if "sys.dm_tran_locks" in query: effect("observation"); raise InjectedFailure("observation")
                    self.fail("Unexpected inert fixture SQL")
                namespace = {"re": re, "sql": sql, "counts": lambda: [1, 1, 1, 1], "event": lambda **kwargs: {},
                    "uuid": SimpleNamespace(uuid4=lambda: SimpleNamespace(hex="probe")),
                    "subprocess": SimpleNamespace(Popen=popen, PIPE=None), "ThreadPoolExecutor": Executor,
                    "time": SimpleNamespace(monotonic=lambda: 0), "compose": ["owned-compose"], "environment": {},
                    "scope": "TenantId='owned' AND CompanyId='owned' AND BindingId='owned'", "owner": "owned"}
                exec(compile(module, "owned_inert_read_race", "exec"), namespace)
                with self.assertRaises(InjectedFailure) as failure:
                    namespace["read_release_range"]({"event": {"messageId": "owned"}}, {})
                self.assertEqual(fault if fault in ("popen", "stdin_write", "stdin_close", "executor") else "observation", str(failure.exception))
                self.assertIn("release", effects); self.assertIn("restore", effects); self.assertIn("drop", effects)
                if fault != "popen": self.assertIn("drain", effects)
                if fault not in ("popen", "stdin_write", "stdin_close", "executor"): self.assertIn("shutdown", effects)

    def test_exact_owned_ci_fixture(self):
        with patch.dict(os.environ, self.environment, clear=True):
            smoke.require_owned_ci_fixture(self.owned)

    def test_unowned_execution_flags(self):
        for override in ({"CI": "false"}, {"GITHUB_ACTIONS": "false"}, {"RUNNER_TEMP": ""}):
            with self.subTest(override=override), patch.dict(os.environ, {**self.environment, **override}, clear=True):
                with self.assertRaisesRegex(RuntimeError, "^Adversarial stack proof requires the owned disposable GitHub CI fixture\\.$"):
                    smoke.require_owned_ci_fixture(self.owned)

    def test_retained_nested_or_unrelated_directory(self):
        for directory in (self.root, self.root / "retained", self.owned / "nested", self.root.parent / "aioffice-local"):
            with self.subTest(directory=directory.name), patch.dict(os.environ, self.environment, clear=True):
                with self.assertRaises(RuntimeError):
                    smoke.require_owned_ci_fixture(directory.resolve())

    def test_administrator_proof_refuses_unowned_before_resources(self):
        cases = [(self.owned, {"CI": "false"}), (self.owned, {"GITHUB_ACTIONS": "false"}),
                 (self.owned, {"RUNNER_TEMP": ""}), (self.root, {}), (self.root / "retained", {}),
                 (self.owned / "nested", {}), (self.root.parent / "aioffice-local", {})]
        for directory, override in cases:
            with self.subTest(directory=str(directory), override=override), patch.dict(os.environ, {**self.environment, **override}, clear=True):
                with self.assertRaises(AssertionError):
                    administrator_smoke.verify(directory=directory, manifest=None, compose=None, environment=None,
                        http=None, sql=None, runtime_statement=None, identity_admin=None, identity=None,
                        api="http://127.0.0.1:8080", web="http://127.0.0.1:3000", auth=None)

    def test_history_proof_refuses_unowned_before_resources(self):
        cases = [(self.owned, {"CI": "false"}, "http://127.0.0.1:8080"),
            (self.owned, {"GITHUB_ACTIONS": "false"}, "http://127.0.0.1:8080"),
            (self.owned, {"RUNNER_TEMP": ""}, "http://127.0.0.1:8080"),
            (self.root, {}, "http://127.0.0.1:8080"), (self.owned / "nested", {}, "http://127.0.0.1:8080"),
            (self.owned, {}, "https://customer.example.invalid")]
        for directory, override, api in cases:
            with self.subTest(directory=str(directory), override=override, api=api), patch.dict(os.environ, {**self.environment, **override}, clear=True):
                with self.assertRaisesRegex(RuntimeError, "^Archive proof requires the owned disposable GitHub CI fixture\\.$"):
                    history_smoke.verify(directory=directory, manifest=None, compose=None, environment=None,
                        http=None, sql=None, identity_admin=None, identity=None, api=api, auth=None, retained_task=None)

    def test_archive_large_sql_uses_stdin_without_query_in_argv_or_diagnostics(self):
        query = "SELECT N'PRIVATE_" + "x" * 200000 + "';"
        with patch.object(history_smoke.subprocess, "run", return_value=SimpleNamespace(returncode=0, stdout="PASS\n", stderr="")) as command:
            self.assertEqual("PASS", history_smoke._owned_sql_stdin(["docker", "compose"], {}, query))
            args, kwargs = command.call_args
            self.assertTrue(all("PRIVATE_" not in item for item in args[0]))
            self.assertEqual("SET NOCOUNT ON; " + query, kwargs["input"])
            self.assertIn("/dev/stdin", args[0][-1])
        with patch.object(history_smoke.subprocess, "run", return_value=SimpleNamespace(returncode=1, stdout="Msg 207, Level 16 PRIVATE_DIAGNOSTIC", stderr="PRIVATE_TOKEN")):
            with self.assertRaisesRegex(RuntimeError, r"^Owned archive SQL fixture command failed \(SQL message 207\)\.$"):
                history_smoke._owned_sql_stdin(["docker", "compose"], {}, query)

    def test_submission_proof_refuses_unowned_before_resources(self):
        cases = [(self.owned, {"CI": "false"}, "http://127.0.0.1:8080", "http://127.0.0.1:8081"),
            (self.owned, {"GITHUB_ACTIONS": "false"}, "http://127.0.0.1:8080", "http://127.0.0.1:8081"),
            (self.owned, {"RUNNER_TEMP": ""}, "http://127.0.0.1:8080", "http://127.0.0.1:8081"),
            (self.root, {}, "http://127.0.0.1:8080", "http://127.0.0.1:8081"),
            (self.owned / "nested", {}, "http://127.0.0.1:8080", "http://127.0.0.1:8081"),
            (self.owned, {}, "https://customer.example.invalid", "http://127.0.0.1:8081"),
            (self.owned, {}, "http://127.0.0.1:8080", "https://customer.example.invalid")]
        for directory, override, api, identity in cases:
            with self.subTest(directory=str(directory), override=override, api=api, identity=identity), patch.dict(os.environ, {**self.environment, **override}, clear=True):
                with self.assertRaisesRegex(RuntimeError, "^Submission proof requires the owned disposable GitHub CI fixture\\.$"):
                    submission_smoke.verify(directory=directory, manifest=None, compose=None, environment=None,
                        http=None, sql=None, runtime_statement=None, identity=identity, api=api, auth=None)

    def test_submission_fingerprint_independent_python_vectors_and_scalar_rejection(self):
        source = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"
        for question, expected in [("Tồn kho 😀 �", "6354CD8F9D07F489B9F1B213CE89B4FE5EF4016DFC44329AE740B0C39BDE0656"),
            ("ồ", "653C057F6AFF5A8FF5FEAFA8B8CFAABAE942E33F0605BC03D14876C53B223DE5"),
            ("o\u0302\u0300", "7176B4A97A12F1F815F8133B9E33C7D021885765A6A5D560976A42104F0ED186")]:
            self.assertEqual(expected, submission_smoke.input_fingerprint(source, question))
        with self.assertRaises(UnicodeEncodeError):
            submission_smoke.input_fingerprint(source, "bad\ud800")

    def test_submission_first_commit_oracle_rejects_every_extra_effect_and_altered_credit(self):
        before = [10, 20, 30, 40, 50, 60, 0]
        after = [11, 21, 33, 41, 51, 61, 0]
        credit = "AB" * 32
        submission_smoke.require_single_execution_delta(before, after, credit, credit)
        for index in range(7):
            with self.subTest(extra_effect=index):
                extra = after.copy(); extra[index] += 1
                with self.assertRaises(AssertionError):
                    submission_smoke.require_single_execution_delta(before, extra, credit, credit)
        with self.assertRaises(AssertionError):
            submission_smoke.require_single_execution_delta(before, after, credit, "CD" * 32)
        with self.assertRaises(AssertionError):
            submission_smoke.require_single_execution_delta(before[:-1], after[:-1], credit, credit)

    def test_group_ingress_refuses_unowned_before_private_configuration_or_processes(self):
        cases = [(self.owned, {"CI": "false"}, "http://127.0.0.1:8080"),
            (self.owned, {"GITHUB_ACTIONS": "false"}, "http://127.0.0.1:8080"),
            (self.owned, {"RUNNER_TEMP": ""}, "http://127.0.0.1:8080"),
            (self.root, {}, "http://127.0.0.1:8080"), (self.owned / "nested", {}, "http://127.0.0.1:8080"),
            (self.owned, {}, "https://customer.example.invalid")]
        for directory, override, api in cases:
            with self.subTest(directory=str(directory), override=override, api=api), patch.dict(os.environ, {**self.environment, **override}, clear=True):
                with self.assertRaisesRegex(RuntimeError, "^Group ingress proof requires the owned disposable GitHub CI fixture\\.$"):
                    group_smoke.verify(directory=directory, manifest=None, compose=None, environment=None, api=api)

    def test_group_index_matches_accepted_shared_contract_golden_and_preserves_original_scalars(self):
        self.assertEqual("E6A0749FB48B2CC0E601E3412D462580886A13C3E34971A794B21C63C5E68599",
            group_smoke.identity_index("synthetic", "account ", " group😀 "))
        self.assertNotEqual(group_smoke.identity_index("synthetic", "account", "ồ"), group_smoke.identity_index("synthetic", "account", "o\u0302\u0300"))
        with self.assertRaises(UnicodeEncodeError): group_smoke.identity_index("synthetic", "account", "\ud800")

    def test_group_signing_has_fixed_domain_and_complete_original_body_fingerprint(self):
        service = "11111111-1111-4111-8111-111111111111"
        nonce = "22222222-2222-4222-8222-222222222222"
        expected = ("aioffice-group-ingest-v1\n" + service + "\n1\n1791580000\n" + nonce +
            "\nE3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855").encode("ascii")
        self.assertEqual(expected, group_smoke.signing_bytes(service, 1, 1791580000, nonce, b""))
        self.assertNotEqual(expected, group_smoke.signing_bytes(service, 1, 1791580000, nonce, b" "))

    def test_group_cursor_oracle_refuses_skipped_future_and_duplicate_commits(self):
        group_smoke.require_contiguous_cursor([1, 2], 2)
        for sequences, cursor in [([1, 3], 2), ([1, 1], 2), ([2, 3], 2), ([1], 2), ([1, 2, 3], 2), ([], 0)]:
            with self.subTest(sequences=sequences, cursor=cursor), self.assertRaises(AssertionError):
                group_smoke.require_contiguous_cursor(sequences, cursor)

    def test_group_private_override_is_removed_even_when_restore_process_or_readiness_fails(self):
        class DisabledResponse:
            status = 404
            headers = {"Cache-Control": "no-store"}
            def read(self): return b""
            def __enter__(self): return self
            def __exit__(self, *args): pass
        for restore_failure in (True, False):
            with self.subTest(restore_failure=restore_failure), tempfile.TemporaryDirectory(prefix="aioffice-group-guard-") as root:
                directory = Path(root) / "aioffice-local"; directory.mkdir()
                marker = directory / "retained-unowned-marker"; marker.write_text("retained", encoding="utf-8")
                environment = {"CI": "true", "GITHUB_ACTIONS": "true", "RUNNER_TEMP": root}
                manifest = {"AIOFFICE_TENANT_ID": "11111111-1111-4111-8111-111111111111", "AIOFFICE_COMPANY_ID": "22222222-2222-4222-8222-222222222222", "AIOFFICE_USER_ID": "33333333-3333-4333-8333-333333333333"}
                auth = {"Authorization": "Bearer owned-fixture", "X-AIOffice-Company-Id": manifest["AIOFFICE_COMPANY_ID"]}
                with patch.dict(os.environ, environment, clear=True), patch.object(group_smoke.urllib.request, "urlopen", return_value=DisabledResponse()), \
                    patch.object(group_smoke, "owned_sql", side_effect=RuntimeError("Owned SQL admission failure.")), \
                    patch.object(group_smoke.subprocess, "run", return_value=SimpleNamespace(returncode=1 if restore_failure else 0, stdout="", stderr="")), \
                    patch.object(group_smoke.time, "monotonic", side_effect=[0, 61]):
                    expected = "Owned group API recreation failed." if restore_failure else "Owned group API readiness deadline exceeded."
                    with self.assertRaisesRegex(RuntimeError, "^" + re.escape(expected) + "$"):
                        group_smoke.verify(directory=directory, manifest=manifest, compose=["docker", "compose"], environment=environment, api="http://127.0.0.1:8080", auth=auth)
                self.assertFalse((directory / "group-ingress-owned.override.json").exists())
                self.assertEqual("retained", marker.read_text(encoding="utf-8"))

    def test_group_history_transport_refuses_default_sqlcmd_max_type_truncation(self):
        original = '["' + 'x'*400 + '"]'
        expected_bytes = len(original.encode('utf-16-le'))
        self.assertEqual(['x'*400], group_smoke.decode_bounded_history(original, expected_bytes))
        for text, size in [(original[:256], expected_bytes), (original[:-1], expected_bytes), (original, 8002), ('', 0)]:
            with self.subTest(length=len(text), size=size), self.assertRaises(AssertionError):
                group_smoke.decode_bounded_history(text, size)

    def test_group_sql_private_bytes_stay_in_stdin_and_diagnostics_remain_fixed(self):
        private = "SELECT N'PRIVATE_GROUP_FIXTURE';"
        with patch.object(group_smoke.subprocess, "run", return_value=SimpleNamespace(returncode=0, stdout="PASS\n", stderr="")) as command:
            self.assertEqual("PASS", group_smoke.owned_sql(["docker", "compose"], {}, private))
            args, kwargs = command.call_args
            self.assertTrue(all("PRIVATE_GROUP" not in item for item in args[0]))
            self.assertEqual("SET NOCOUNT ON; USE AIOfficeLocal; " + private, kwargs["input"])
        with patch.object(group_smoke.subprocess, "run", return_value=SimpleNamespace(returncode=1, stdout="Msg 207, Level 16 PRIVATE_GROUP", stderr="PRIVATE_TOKEN")):
            with self.assertRaisesRegex(RuntimeError, r"^Owned group SQL fixture command failed \(SQL message 207\)\.$"):
                group_smoke.owned_sql(["docker", "compose"], {}, private)

    def test_submission_original_event_transport_refuses_silent_sqlcmd_truncation(self):
        original = "7B00" * 300
        self.assertEqual(original, submission_smoke.require_stored_event_hex(original, 600))
        for value, size in [(original[:256], 600), (original[:-1], 600), ("NULL", 600), ("gg" * 600, 600), ("AA" * 4001, 4001)]:
            with self.subTest(length=len(value), expected_bytes=size), self.assertRaises(AssertionError):
                submission_smoke.require_stored_event_hex(value, size)


class ReferenceChildClosureTests(unittest.TestCase):
    class Child:
        def __init__(self, poll_error=None, drain_errors=(), input_error=None, completed=False):
            self.poll_error = poll_error
            self.drain_errors = list(drain_errors)
            self.drains = []
            self.completed = completed
            self.stdin = SimpleNamespace(close=lambda: self.close_input(input_error))
            self.input_closed = 0

        def close_input(self, error):
            self.input_closed += 1
            if error is not None: raise error

        def poll(self):
            if self.poll_error is not None: raise self.poll_error
            return 0 if self.completed else None

        def communicate(self, timeout):
            self.drains.append(timeout)
            if self.drain_errors:
                error = self.drain_errors.pop(0)
                if error is not None: raise error
            return ("", "")

    def test_poll_failure_still_attempts_owned_kill_and_drain_preserving_original(self):
        original = RuntimeError("owned-poll-fault")
        child = self.Child(poll_error=original)
        kills = []
        with self.assertRaises(RuntimeError) as observed:
            reference_smoke.close_owned_reference_child(child, lambda: kills.append("inspected-kill"))
        self.assertIs(original, observed.exception)
        self.assertEqual(["inspected-kill"], kills)
        self.assertEqual([15], child.drains)

    def test_kill_failure_cannot_skip_drain(self):
        original = RuntimeError("owned-kill-fault")
        child = self.Child()
        def kill(): raise original
        with self.assertRaises(RuntimeError) as observed:
            reference_smoke.close_owned_reference_child(child, kill)
        self.assertIs(original, observed.exception)
        self.assertEqual([15], child.drains)

    def test_drain_timeout_reinspects_kill_and_always_performs_final_bounded_drain(self):
        original = TimeoutError("owned-first-drain")
        child = self.Child(drain_errors=[original, None])
        kills = []
        with self.assertRaises(TimeoutError) as observed:
            reference_smoke.close_owned_reference_child(child, lambda: kills.append("inspected-kill"))
        self.assertIs(original, observed.exception)
        self.assertEqual(["inspected-kill", "inspected-kill"], kills)
        self.assertEqual([15, 5], child.drains)

    def test_every_failure_still_attempts_secondary_kill_and_final_drain(self):
        original = RuntimeError("owned-input-close")
        child = self.Child(poll_error=RuntimeError("poll"), drain_errors=[TimeoutError("first"), TimeoutError("final")], input_error=original)
        kills = []
        def kill():
            kills.append("inspected-kill")
            raise RuntimeError("kill")
        with self.assertRaises(RuntimeError) as observed:
            reference_smoke.close_owned_reference_child(child, kill)
        self.assertIs(original, observed.exception)
        self.assertEqual(2, len(kills)); self.assertEqual([15, 5], child.drains)
        self.assertIsNone(child.stdin)

    def test_completed_child_is_drained_without_kill_and_partial_child_is_inert(self):
        child = self.Child(completed=True)
        kills = []
        reference_smoke.close_owned_reference_child(child, lambda: kills.append("kill"))
        reference_smoke.close_owned_reference_child(None, lambda: kills.append("kill"))
        self.assertEqual([], kills); self.assertEqual([15], child.drains)
        self.assertEqual(1, child.input_closed)


if __name__ == "__main__":
    unittest.main()
