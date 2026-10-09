"""Configure Google's Docker Hub cache only on an empty hosted Linux CI runner."""
import copy
import json
import os
from pathlib import Path
import subprocess
import tempfile

CACHE = "https://mirror.gcr.io"


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


def command(arguments):
    return subprocess.run(arguments, check=True, capture_output=True, text=True, timeout=60).stdout


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


if __name__ == "__main__":
    try:
        main()
    except Exception:
        # Existing daemon configuration and CLI diagnostics may contain secrets.
        raise SystemExit("CI Docker cache configuration failed.") from None
