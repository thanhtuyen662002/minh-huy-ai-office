#!/usr/bin/env python3
import json
import sys
from pathlib import Path


def fail(message: str) -> None:
    print(f"deployment validation failed: {message}", file=sys.stderr)
    raise SystemExit(1)


if len(sys.argv) != 2:
    fail("usage: validate-deployment.py <docker-compose-config.json>")

config_path = Path(sys.argv[1])
with config_path.open(encoding="utf-8") as handle:
    config = json.load(handle)

services = config.get("services")
if not isinstance(services, dict):
    fail("compose config has no services map")

required_services = {"core-api", "agent-worker", "rabbitmq", "redis", "otel-collector"}
missing_services = required_services.difference(services)
if missing_services:
    fail(f"missing required services: {', '.join(sorted(missing_services))}")

for service_name, service in services.items():
    if service.get("network_mode") == "host":
        fail(f"{service_name} must not use host networking")

    for port in service.get("ports") or []:
        target = int(port.get("target", 0))
        published = port.get("published")
        host_ip = port.get("host_ip")

        if published is None:
            continue
        if target == 1433:
            fail(f"{service_name} publishes SQL Server port 1433")
        if host_ip not in {"127.0.0.1", "::1"}:
            fail(
                f"{service_name} publishes target {target} outside loopback "
                f"(host_ip={host_ip!r})"
            )

core_api = services["core-api"]
api_ports = core_api.get("ports") or []
if not any(
    int(port.get("target", 0)) == 8080
    and port.get("published") is not None
    and port.get("host_ip") == "127.0.0.1"
    for port in api_ports
):
    fail("core-api must publish container port 8080 on IPv4 loopback only")

api_environment = core_api.get("environment") or {}
expected_secret_ref = "secretref://env/AIOFFICE_DB_CONNECTION"
if (
    api_environment.get("AIOffice__PlatformDatabase__ConnectionSecretRef")
    != expected_secret_ref
):
    fail("core-api database credential must be resolved through the approved secretref")

if "AIOFFICE_DB_CONNECTION" not in api_environment:
    fail("core-api runtime database secret value is not wired")

worker_ports = services["agent-worker"].get("ports") or []
if worker_ports:
    fail("agent-worker must not publish host ports")

print("deployment contract validation passed")
