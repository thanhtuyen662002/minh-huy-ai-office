import importlib.util
import json
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

    def test_profile_is_fixed_and_refused_before_cli(self):
        for profile in [None, "", "https://foreign.invalid", "web; dangerous"]:
            with patch.object(cache, "command") as command, self.assertRaises(RuntimeError):
                cache.prime_images(profile)
            command.assert_not_called()

    def test_cached_digest_and_exact_canonical_identity_required(self):
        identity = "sha256:" + "a" * 64
        digest = "mirror.gcr.io/library/redis@sha256:" + "b" * 64
        calls = []

        def command(arguments, timeout=60):
            calls.append((arguments, timeout))
            if arguments == ["docker", "image", "inspect", "mirror.gcr.io/library/redis:8-alpine"]:
                return json.dumps([{"Id": identity, "RepoDigests": [digest]}])
            if "--format" in arguments:
                return identity + "\n"
            return ""

        with patch.object(cache, "command", side_effect=command):
            cache.prime_images("web")
        self.assertEqual(calls[0], (["docker", "pull", "mirror.gcr.io/library/redis:8-alpine"], 180))
        self.assertIn((["docker", "image", "tag", "mirror.gcr.io/library/redis:8-alpine", "redis:8-alpine"], 60), calls)
        self.assertEqual(len(calls), 4)
        for evidence in [[], [{"Id": identity}], [{"Id": "bad", "RepoDigests": [digest]}],
                         [{"Id": identity, "RepoDigests": ["foreign@sha256:" + "b" * 64]}]]:
            with patch.object(cache, "command", side_effect=["", json.dumps(evidence)]) as mocked, self.assertRaises(RuntimeError):
                cache.prime_images("web")
            self.assertEqual(mocked.call_count, 2)
        with patch.object(cache, "command", side_effect=["", json.dumps([{"Id": identity, "RepoDigests": [digest]}]), "", "sha256:" + "c" * 64]), self.assertRaises(RuntimeError):
            cache.prime_images("web")

    def test_failed_cached_pull_never_tags_or_contacts_canonical_hub(self):
        with patch.object(cache, "command", side_effect=RuntimeError("cache unavailable")) as command, self.assertRaises(RuntimeError):
            cache.prime_images("web")
        command.assert_called_once_with(["docker", "pull", "mirror.gcr.io/library/redis:8-alpine"], timeout=180)


if __name__ == "__main__":
    unittest.main()
