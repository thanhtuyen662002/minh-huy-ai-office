import copy
import unittest
from browser_deployment_contract import validate_browser_deployment


class BrowserDeploymentTests(unittest.TestCase):
    def setUp(self):
        self.api = {"AIOffice__Authentication__Authority": "https://identity.example.test/realm"}
        self.web = {
            "AIOFFICE_BROWSER_OIDC_ENABLED": "true",
            "AIOFFICE_BROWSER_OIDC_LOCAL_HTTP": "false", "AIOFFICE_BROWSER_CI_PROOF": "false",
            "AIOFFICE_BROWSER_OIDC_PUBLIC_ORIGIN": "https://office.example.test",
            "AIOFFICE_BROWSER_OIDC_ISSUER": self.api["AIOffice__Authentication__Authority"],
            "AIOFFICE_BROWSER_OIDC_AUTHORIZATION_ENDPOINT": "https://identity.example.test/authorize",
            "AIOFFICE_BROWSER_OIDC_TOKEN_ENDPOINT": "https://identity.example.test/token",
            "AIOFFICE_BROWSER_OIDC_JWKS_URI": "https://identity.example.test/certs",
            "AIOFFICE_BROWSER_OIDC_CLIENT_ID": "office-browser", "AIOFFICE_BROWSER_OIDC_TRANSACTION_KEY": "A" * 43,
            "AIOFFICE_BROWSER_CORE_API_ORIGIN": "https://api.example.test",
            "AIOFFICE_BROWSER_SESSION_REDIS_URL": "rediss://private-cache.example.test:6380/1",
        }

    def test_enabled_and_explicit_disabled_profiles(self):
        self.assertIsNone(validate_browser_deployment(self.web, self.api))
        self.assertIsNone(validate_browser_deployment({"AIOFFICE_BROWSER_OIDC_ENABLED": "false",
            "AIOFFICE_BROWSER_OIDC_LOCAL_HTTP": "false", "AIOFFICE_BROWSER_CI_PROOF": "false"}, self.api))

    def test_unsafe_and_incomplete_configuration_refuses_without_reflecting_values(self):
        cases = [(name, "http://PRIVATE.invalid") for name in self.web if name.endswith(("ORIGIN", "ENDPOINT", "JWKS_URI"))]
        cases += [("AIOFFICE_BROWSER_OIDC_ISSUER", "https://PRIVATE.invalid"),
            ("AIOFFICE_BROWSER_OIDC_PUBLIC_ORIGIN", "https://PRIVATE.invalid/path"),
            ("AIOFFICE_BROWSER_CORE_API_ORIGIN", "https://PRIVATE@api.example.test"),
            ("AIOFFICE_BROWSER_SESSION_REDIS_URL", "redis://user:PRIVATE@cache.example.test/1"),
            ("AIOFFICE_BROWSER_SESSION_REDIS_URL", "rediss://user:PRIVATE@cache.example.test/16"),
            ("AIOFFICE_BROWSER_SESSION_REDIS_URL", "rediss://user:PRIVATE@cache.example.test:bad"),
            ("AIOFFICE_BROWSER_OIDC_TRANSACTION_KEY", "PRIVATE"),
            ("AIOFFICE_BROWSER_OIDC_TRANSACTION_KEY", "A" * 42 + "B"),
            ("AIOFFICE_BROWSER_OIDC_CLIENT_ID", "PRIVATE/invalid"),
            ("AIOFFICE_BROWSER_OIDC_CLIENT_SECRET", "PRIVATE\nvalue"),
            ("AIOFFICE_BROWSER_OIDC_LOCAL_HTTP", "true"), ("AIOFFICE_BROWSER_CI_PROOF", "true")]
        cases += [(name, "") for name in self.web if name not in {"AIOFFICE_BROWSER_OIDC_CLIENT_SECRET"}]
        for name, value in cases:
            with self.subTest(name=name):
                candidate = copy.deepcopy(self.web); candidate[name] = value
                message = validate_browser_deployment(candidate, self.api)
                self.assertIsInstance(message, str)
                self.assertNotIn("PRIVATE", message)

    def test_disabled_profile_still_cannot_enable_local_or_proof_modes(self):
        self.web["AIOFFICE_BROWSER_OIDC_ENABLED"] = "false"
        for name in ["AIOFFICE_BROWSER_OIDC_LOCAL_HTTP", "AIOFFICE_BROWSER_CI_PROOF"]:
            with self.subTest(name=name):
                candidate = dict(self.web); candidate[name] = "true"
                self.assertIsNotNone(validate_browser_deployment(candidate, self.api))


if __name__ == "__main__":
    unittest.main()
