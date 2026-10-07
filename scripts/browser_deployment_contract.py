"""Preflight only; runtime OIDC/Core/Redis validation remains authoritative."""
import base64
import re
from urllib.parse import urlsplit


def validate_browser_deployment(web, api):
    if web.get("AIOFFICE_BROWSER_OIDC_LOCAL_HTTP") != "false":
        return "production browser must forbid local HTTP mode"
    if web.get("AIOFFICE_BROWSER_CI_PROOF") != "false":
        return "production browser must disable disposable proof diagnostics"
    enabled = web.get("AIOFFICE_BROWSER_OIDC_ENABLED")
    if enabled not in {"true", "false"}:
        return "production browser enablement must be explicit"
    if enabled == "false":
        return None

    origins = {"AIOFFICE_BROWSER_OIDC_PUBLIC_ORIGIN", "AIOFFICE_BROWSER_CORE_API_ORIGIN"}
    endpoints = origins | {"AIOFFICE_BROWSER_OIDC_ISSUER", "AIOFFICE_BROWSER_OIDC_AUTHORIZATION_ENDPOINT",
                           "AIOFFICE_BROWSER_OIDC_TOKEN_ENDPOINT", "AIOFFICE_BROWSER_OIDC_JWKS_URI"}
    for name in endpoints:
        value = web.get(name)
        try:
            if not isinstance(value, str) or not value.startswith("https://") or len(value) > 2048 or re.search(r"[\s\\?#]", value):
                return "enabled production browser requires trusted HTTPS endpoints"
            url = urlsplit(value)
            if not url.hostname or url.username is not None or url.password is not None or (name in origins and url.path):
                return "enabled production browser requires trusted HTTPS endpoints"
            if url.port is not None and not 1 <= url.port <= 65535:
                return "enabled production browser requires trusted HTTPS endpoints"
        except ValueError:
            return "enabled production browser requires trusted HTTPS endpoints"
    if web["AIOFFICE_BROWSER_OIDC_ISSUER"] != api.get("AIOffice__Authentication__Authority"):
        return "browser and Core must use the same trusted issuer"
    client = web.get("AIOFFICE_BROWSER_OIDC_CLIENT_ID", "")
    if not isinstance(client, str) or not re.fullmatch(r"[A-Za-z0-9._-]{1,100}", client):
        return "enabled production browser requires a client identity"
    key = web.get("AIOFFICE_BROWSER_OIDC_TRANSACTION_KEY", "")
    if not isinstance(key, str) or not re.fullmatch(r"[A-Za-z0-9_-]{43}", key):
        return "enabled production browser requires a private canonical32-byte key"
    decoded = base64.urlsafe_b64decode(key + "=")
    if len(decoded) != 32 or base64.urlsafe_b64encode(decoded).decode().rstrip("=") != key:
        return "enabled production browser requires a private canonical32-byte key"
    secret = web.get("AIOFFICE_BROWSER_OIDC_CLIENT_SECRET", "")
    if not isinstance(secret, str) or len(secret) > 4096 or re.search(r"[\r\n\x00]", secret):
        return "production browser client secret is invalid"
    redis = web.get("AIOFFICE_BROWSER_SESSION_REDIS_URL", "")
    try:
        if not isinstance(redis, str) or not redis.startswith("rediss://") or len(redis) > 4096 or re.search(r"[\s\\?#]", redis):
            return "production browser session authority requires private TLS Redis"
        url = urlsplit(redis)
        if not url.hostname or not re.fullmatch(r"(?:/(?:[0-9]|1[0-5]))?", url.path) or (url.port is not None and not 1 <= url.port <= 65535):
            return "production browser session authority requires private TLS Redis"
    except ValueError:
        return "production browser session authority requires private TLS Redis"
    return None
