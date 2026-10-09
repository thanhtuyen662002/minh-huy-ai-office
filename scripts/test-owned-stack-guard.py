"""Verify the adversarial fixture boundary without files, Docker or network."""
import importlib.util
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


class OwnedStackGuardTests(unittest.TestCase):
    root = (Path.cwd() / "guard-test-no-resources").resolve()
    owned = root / "aioffice-local"
    environment = {"CI": "true", "GITHUB_ACTIONS": "true", "RUNNER_TEMP": str(root)}

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
                manifest = {"AIOFFICE_TENANT_ID": "11111111-1111-4111-8111-111111111111", "AIOFFICE_COMPANY_ID": "22222222-2222-4222-8222-222222222222"}
                with patch.dict(os.environ, environment, clear=True), patch.object(group_smoke.urllib.request, "urlopen", return_value=DisabledResponse()), \
                    patch.object(group_smoke, "owned_sql", side_effect=RuntimeError("Owned SQL admission failure.")), \
                    patch.object(group_smoke.subprocess, "run", return_value=SimpleNamespace(returncode=1 if restore_failure else 0, stdout="", stderr="")), \
                    patch.object(group_smoke.time, "monotonic", side_effect=[0, 61]):
                    expected = "Owned group API recreation failed." if restore_failure else "Owned group API readiness deadline exceeded."
                    with self.assertRaisesRegex(RuntimeError, "^" + re.escape(expected) + "$"):
                        group_smoke.verify(directory=directory, manifest=manifest, compose=["docker", "compose"], environment=environment, api="http://127.0.0.1:8080")
                self.assertFalse((directory / "group-ingress-owned.override.json").exists())
                self.assertEqual("retained", marker.read_text(encoding="utf-8"))

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
