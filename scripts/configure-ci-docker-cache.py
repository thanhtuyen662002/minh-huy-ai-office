"""Configure Google's Docker Hub cache only on an empty hosted Linux CI runner."""
import copy
import json
import os
import re
from pathlib import Path
import subprocess
import tempfile

CACHE = "https://mirror.gcr.io"
PROFILES = {
    "web": [("library/redis:8-alpine", "redis:8-alpine")],
    "web-runtime": [("library/node:24-alpine", "node:24-alpine")],
    "local-runtime": [("library/node:24-alpine", "node:24-alpine"),
                      ("library/postgres:17-alpine", "postgres:17-alpine"),
                      ("library/rabbitmq:4-management", "rabbitmq:4-management"),
                      ("library/redis:8-alpine", "redis:8-alpine"),
                      ("otel/opentelemetry-collector-contrib:0.161.0", "otel/opentelemetry-collector-contrib:0.161.0")],
    "deployment": [("library/rabbitmq:4-management", "rabbitmq:4-management"),
                   ("library/redis:8-alpine", "redis:8-alpine"),
                   ("otel/opentelemetry-collector-contrib:0.161.0", "otel/opentelemetry-collector-contrib:0.161.0")],
}


def configuration(existing):
    if not isinstance(existing, dict):
        raise ValueError("Invalid daemon configuration.")
    mirrors = existing.get("registry-mirrors", [])
    if not isinstance(mirrors, list) or any(not isinstance(item, str) for item in mirrors):
        raise ValueError("Invalid daemon mirrors.")
    result = copy.deepcopy(existing)
    result["registry-mirrors"] = [CACHE] + [item for item in mirrors if item != CACHE]
    return result


def require_hosted_ci(environ, platform, uid):
    if (platform != "posix" or uid != 0 or environ.get("CI") != "true"
            or environ.get("GITHUB_ACTIONS") != "true"
            or environ.get("RUNNER_OS") != "Linux"
            or environ.get("AIOFFICE_DOCKER_CACHE_RUNNER") != "github-hosted"):
        raise RuntimeError("Hosted Linux CI is required.")


def command(arguments, timeout=60):
    return subprocess.run(arguments, check=True, capture_output=True, text=True, timeout=timeout).stdout


def prime_images(profile):
    if profile not in PROFILES:
        raise RuntimeError("A fixed CI image profile is required.")
    for repository, canonical in PROFILES[profile]:
        cached = "mirror.gcr.io/" + repository
        # An explicit cached reference cannot silently fall back to a throttled
        # Docker Hub endpoint. Retain the canonical build/compose references.
        command(["docker", "pull", cached], timeout=180)
        evidence = json.loads(command(["docker", "image", "inspect", cached]))
        if not isinstance(evidence, list) or len(evidence) != 1:
            raise RuntimeError("Cached image evidence is unavailable.")
        identity = evidence[0].get("Id", "")
        digests = evidence[0].get("RepoDigests", [])
        prefix = cached.rsplit(":", 1)[0] + "@sha256:"
        if (not isinstance(identity, str) or not re.fullmatch(r"sha256:[0-9a-f]{64}", identity)
                or not isinstance(digests, list)
                or not any(isinstance(item, str) and item.startswith(prefix)
                           and re.fullmatch(r"[0-9a-f]{64}", item[len(prefix):]) for item in digests)):
            raise RuntimeError("Cached image digest evidence is unavailable.")
        command(["docker", "image", "tag", cached, canonical])
        restored = command(["docker", "image", "inspect", "--format", "{{.Id}}", canonical]).strip()
        if restored != identity:
            raise RuntimeError("Canonical image identity verification failed.")
    print("PASS fixed CI images loaded from managed cache with identical canonical image IDs")


def main():
    require_hosted_ci(os.environ, os.name, os.geteuid() if hasattr(os, "geteuid") else -1)
    # Restarting a daemon with service containers would disrupt their gate.
    if command(["docker", "ps", "--quiet"]).strip():
        raise RuntimeError("An empty hosted daemon is required.")
    directory = Path("/etc/docker")
    target = directory / "daemon.json"
    if directory.is_symlink() or target.is_symlink():
        raise RuntimeError("A regular daemon configuration is required.")
    existing = json.loads(target.read_text(encoding="utf-8")) if target.exists() else {}
    updated = configuration(existing)
    if updated != existing:
        directory.mkdir(parents=True, exist_ok=True)
        temporary = None
        try:
            with tempfile.NamedTemporaryFile(mode="w", encoding="utf-8", dir=directory,
                                             prefix="aioffice-ci-cache-", delete=False) as stream:
                temporary = Path(stream.name)
                json.dump(updated, stream)
                stream.flush()
                os.fsync(stream.fileno())
            temporary.replace(target)
            temporary = None
        finally:
            if temporary is not None:
                temporary.unlink(missing_ok=True)
        command(["systemctl", "restart", "docker"])
    mirrors = json.loads(command(["docker", "info", "--format", "{{json .RegistryConfig.Mirrors}}"]))
    if not isinstance(mirrors, list) or not any(item.rstrip("/") == CACHE for item in mirrors):
        raise RuntimeError("Daemon cache verification failed.")
    print("PASS hosted Docker Hub cache configuration; canonical image names retained")
    prime_images(os.environ.get("AIOFFICE_DOCKER_CACHE_PROFILE", "local-runtime"))


if __name__ == "__main__":
    try:
        main()
    except Exception:
        # Existing daemon configuration and CLI diagnostics may contain secrets.
        raise SystemExit("CI Docker cache configuration failed.") from None
