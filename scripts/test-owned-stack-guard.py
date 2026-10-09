"""Verify the adversarial fixture boundary without files, Docker or network."""
import importlib.util
import os
from pathlib import Path
import unittest
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


if __name__ == "__main__":
    unittest.main()
