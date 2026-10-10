"""Verify the adversarial fixture boundary without files, Docker or network."""
import importlib.util
import ast
import os
import re
from pathlib import Path
import unittest
import tempfile
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


class OwnedStackGuardTests(unittest.TestCase):
    root = (Path.cwd() / "guard-test-no-resources").resolve()
    owned = root / "aioffice-local"
    environment = {"CI": "true", "GITHUB_ACTIONS": "true", "RUNNER_TEMP": str(root)}

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
