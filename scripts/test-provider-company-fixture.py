import importlib.util
import json
import os
from pathlib import Path
import re
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("company_fixture", Path(__file__).with_name("owned-provider-company-fixture.py"))
fixture = importlib.util.module_from_spec(spec)
spec.loader.exec_module(fixture)


class ProviderCompanyFixtureTests(unittest.TestCase):
    def test_unowned_refuses_before_configuration_or_callbacks(self):
        with patch.dict(os.environ, {"CI": "false", "GITHUB_ACTIONS": "true", "RUNNER_TEMP": "owned"}):
            with self.assertRaisesRegex(RuntimeError, "requires the owned"):
                fixture.prepare(directory=Path("unowned"), manifest=None, sql=None, identity_admin=None, api="http://127.0.0.1:8080")

    def test_ordinary_owned_enrollment_has_no_default_membership_or_browser_subject(self):
        with tempfile.TemporaryDirectory() as temporary, patch.dict(os.environ, {"CI": "true", "GITHUB_ACTIONS": "true", "RUNNER_TEMP": temporary}):
            directory = Path(temporary) / "aioffice-local"
            directory.mkdir()
            default = "22222222-2222-2222-2222-222222222222"
            manifest = {"AIOFFICE_TENANT_ID": "11111111-1111-1111-1111-111111111111", "AIOFFICE_COMPANY_ID": default,
                "AIOFFICE_DATA_SOURCE_ID": "33333333-3333-3333-3333-333333333333", "AIOFFICE_OWNER_PASSWORD": "PRIVATE_NOT_EXPORTED"}
            statements, creates = [], []

            def sql(query):
                statements.append(query)
                return "0" if "SELECT COUNT(*)" in query else ""

            def admin(path, payload=None, method=None):
                if method == "POST":
                    creates.append(payload)
                    return None
                return [{"username": path.split("=", 1)[1].split("&", 1)[0], "id": f"44444444-4444-4444-4444-{len(creates):012d}"}]

            fixture.prepare(directory=directory, manifest=manifest, sql=sql, identity_admin=admin, api="http://127.0.0.1:8080")
            exported = (directory / "company-signin-fixture.json").read_text(encoding="utf-8")
            value = json.loads(exported)
            self.assertEqual([len(item["companies"]) for item in value["identities"]], [1, 2, 0])
            self.assertEqual(len(creates), 3)
            self.assertNotIn("subject", exported)
            self.assertNotIn("PRIVATE_NOT_EXPORTED", exported)
            inserts = "\n".join(query for query in statements if "INSERT" in query)
            memberships = re.findall(r"INSERT aioffice.CompanyMemberships[^;]+;", inserts)
            self.assertEqual(len(memberships), 3)
            self.assertTrue(all(default not in item for item in memberships))
            prior = len(statements), len(creates)
            with self.assertRaisesRegex(RuntimeError, "already exists"):
                fixture.prepare(directory=directory, manifest=None, sql=sql, identity_admin=admin, api="http://127.0.0.1:8080")
            self.assertEqual(prior, (len(statements), len(creates)))


if __name__ == "__main__":
    unittest.main()
