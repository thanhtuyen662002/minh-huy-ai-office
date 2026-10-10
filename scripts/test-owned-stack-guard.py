"""Verify the adversarial fixture boundary without files, Docker or network."""
import importlib.util
import ast
import base64
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


class OwnedStackGuardTests(unittest.TestCase):
    root = (Path.cwd() / "guard-test-no-resources").resolve()
    owned = root / "aioffice-local"
    environment = {"CI": "true", "GITHUB_ACTIONS": "true", "RUNNER_TEMP": str(root)}

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
                    stable=["same-receipt"], count=lambda: 2, full_graph=lambda: ["changed-receipt" if fault == "changed-receipt" else "same-receipt"],
                    queue_counts=lambda: (1, 0) if fault == "pending-queue" else (0, 0))
                if fault:
                    with self.assertRaises(AssertionError): exec(block, namespace)
                else:
                    exec(block, namespace); self.assertEqual(["restart", "publish-existing"], calls)

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
        callback = next(node for node in ast.walk(tree) if isinstance(node, ast.FunctionDef) and node.name == "enroll_spool_source")
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
        with self.assertRaises(ValueError): namespace["enroll_spool_source"]("invalid-source")
        self.assertEqual(original, environment); self.assertEqual([], calls)
        namespace["enroll_spool_source"](source)
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
        with self.assertRaises(AssertionError): namespace["enroll_spool_source"](source)
        self.assertEqual(before, environment); self.assertEqual(4, len(calls))

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
                    "call": lambda *args, **kwargs: (200, {"wasAlreadyCommitted": True}), "receipt": lambda status, value, **kwargs: value}
                if fault is None:
                    exec(compile(module, "inert_listener_permissions", "exec"), namespace)
                    self.assertEqual(["grant", "refusal", "restore"], effects)
                else:
                    with self.assertRaises(AssertionError): exec(compile(module, "inert_listener_permissions", "exec"), namespace)
                    self.assertEqual(["grant", "restore"] if fault == "ineffective-grant" else [], effects)
                self.assertFalse(current["granted"])

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


if __name__ == "__main__":
    unittest.main()
