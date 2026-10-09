import importlib.util
from pathlib import Path
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("ci_cache", Path(__file__).with_name("configure-ci-docker-cache.py"))
cache = importlib.util.module_from_spec(spec)
spec.loader.exec_module(cache)


class CacheTests(unittest.TestCase):
    def test_preserves_existing_configuration_and_is_idempotent(self):
        original = {"live-restore": True, "features": {"containerd-snapshotter": True},
                    "registry-mirrors": ["https://existing.example.invalid", cache.CACHE, cache.CACHE]}
        result = cache.configuration(original)
        self.assertEqual(result["registry-mirrors"], [cache.CACHE, "https://existing.example.invalid"])
        self.assertTrue(result["live-restore"])
        self.assertEqual(result["features"], original["features"])
        self.assertEqual(cache.configuration(result), result)
        self.assertEqual(len(original["registry-mirrors"]), 3)
        result["features"]["containerd-snapshotter"] = False
        self.assertTrue(original["features"]["containerd-snapshotter"])

    def test_invalid_daemon_shapes_refused(self):
        for value in [[], None, {"registry-mirrors": "https://bad.invalid"}, {"registry-mirrors": [7]}]:
            with self.subTest(value=value), self.assertRaises(ValueError):
                cache.configuration(value)

    def test_every_host_ownership_flag_required(self):
        env = {"CI": "true", "GITHUB_ACTIONS": "true", "RUNNER_OS": "Linux",
               "AIOFFICE_DOCKER_CACHE_RUNNER": "github-hosted"}
        cache.require_hosted_ci(env, "posix", 0)
        for name in env:
            for value in [None, "false", "self-hosted"]:
                changed = dict(env)
                if value is None:
                    changed.pop(name)
                else:
                    changed[name] = value
                with self.subTest(name=name, value=value), self.assertRaises(RuntimeError):
                    cache.require_hosted_ci(changed, "posix", 0)
        for platform, uid in [("nt", 0), ("posix", 1000)]:
            with self.assertRaises(RuntimeError):
                cache.require_hosted_ci(env, platform, uid)

    def test_refuses_unowned_invocation_before_files_or_cli(self):
        with patch.dict(cache.os.environ, {}, clear=True), patch.object(cache, "command") as command:
            with self.assertRaises(RuntimeError):
                cache.main()
            command.assert_not_called()

    def test_running_service_refused_before_configuration_access(self):
        with patch.object(cache, "require_hosted_ci"), patch.object(cache, "command", return_value="owned-container\n"), \
                patch.object(cache.Path, "read_text") as read:
            with self.assertRaises(RuntimeError):
                cache.main()
            read.assert_not_called()


if __name__ == "__main__":
    unittest.main()
