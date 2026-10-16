"""Deployment preflight for output limits consumed by the existing worker loader.

This checks rendered wiring only; it neither selects a model nor reads secrets.
"""
import re


OUTPUT_LIMIT_KEYS = tuple(
    prefix + "_MAX_OUTPUT_TOKENS"
    for prefix in ("AIOFFICE_AI_PRIMARY", "AIOFFICE_AI_BACKUP", "AIOFFICE_AI")
)


def validate_ai_gateway_deployment(services: dict) -> str | None:
    worker = services.get("agent-worker")
    if not isinstance(worker, dict) or not isinstance(worker.get("environment"), dict):
        return "AI worker environment mapping is required"
    environment = worker["environment"]
    for key in OUTPUT_LIMIT_KEYS:
        if key not in environment:
            return "AI worker output-limit wiring is incomplete"
        value = environment[key]
        if (not isinstance(value, str) or not re.fullmatch(r"[1-9][0-9]{0,4}", value)
                or int(value) > 32768):
            return "AI output limits must be canonical integers from 1 through 32768"
    for name, service in services.items():
        if name == "agent-worker" or not isinstance(service, dict):
            continue
        other = service.get("environment")
        if isinstance(other, dict) and any(key in other for key in OUTPUT_LIMIT_KEYS):
            return "AI output-limit variables must be routed only to the worker"
    return None
