import copy
import unittest
from ai_gateway_deployment_contract import OUTPUT_LIMIT_KEYS, validate_ai_gateway_deployment


class AiGatewayDeploymentTests(unittest.TestCase):
    def setUp(self):
        self.services = {
            "agent-worker": {"environment": {key: "1024" for key in OUTPUT_LIMIT_KEYS}},
            "web": {"environment": {}}, "core-api": {"environment": {}},
        }

    def test_defaults_allow_absent_provider_without_model_request(self):
        self.assertIsNone(validate_ai_gateway_deployment(self.services))

    def test_independent_primary_backup_and_legacy_bounds_are_accepted(self):
        self.services["agent-worker"]["environment"].update(zip(OUTPUT_LIMIT_KEYS, ("1", "32768", "4096")))
        self.assertIsNone(validate_ai_gateway_deployment(self.services))

    def test_missing_mapping_cannot_silently_drop_environment_file_override(self):
        for key in OUTPUT_LIMIT_KEYS:
            with self.subTest(key=key):
                services = copy.deepcopy(self.services)
                del services["agent-worker"]["environment"][key]
                self.assertIsNotNone(validate_ai_gateway_deployment(services))

    def test_unsafe_boundaries_refuse_without_reflecting_values(self):
        for value in ("0", "32769", "0001", "+1", "-1", "1.0", "1e3", " 1", "1 ", "1\n", "\u0661", "PRIVATE", "9" * 10000):
            for key in OUTPUT_LIMIT_KEYS:
                with self.subTest(key=key, kind=type(value).__name__):
                    services = copy.deepcopy(self.services)
                    services["agent-worker"]["environment"][key] = value
                    message = validate_ai_gateway_deployment(services)
                    self.assertIsInstance(message, str)
                    self.assertEqual("AI output limits must be canonical integers from 1 through 32768", message)

    def test_nonstring_environment_values_refuse(self):
        for value in (None, 1, True, [], {}):
            services = copy.deepcopy(self.services)
            services["agent-worker"]["environment"][OUTPUT_LIMIT_KEYS[0]] = value
            self.assertIsNotNone(validate_ai_gateway_deployment(services))

    def test_limit_misrouting_to_public_services_refuses_without_reflection(self):
        for name in ("web", "core-api"):
            for key in OUTPUT_LIMIT_KEYS:
                services = copy.deepcopy(self.services)
                services[name]["environment"][key] = "PRIVATE"
                message = validate_ai_gateway_deployment(services)
                self.assertIsNotNone(message)
                self.assertNotIn("PRIVATE", message)

    def test_provider_secret_value_is_not_read_or_changed(self):
        self.services["agent-worker"]["environment"]["AIOFFICE_AI_PRIMARY_AUTHORIZATION"] = "PRIVATE"
        before = copy.deepcopy(self.services)
        self.assertIsNone(validate_ai_gateway_deployment(self.services))
        self.assertEqual(before, self.services)

    def test_missing_or_nonmapping_worker_environment_refuses(self):
        for services in ({}, {"agent-worker": None}, {"agent-worker": {}}, {"agent-worker": {"environment": []}}):
            self.assertIsNotNone(validate_ai_gateway_deployment(services))


if __name__ == "__main__":
    unittest.main()
