#!/bin/bash
set -euo pipefail

# Compose file-backed secrets; no credentials in container arguments or rendered config.
# Do not enable shell tracing: these variables are private process environment only.
export KC_DB_USERNAME="$(</run/secrets/keycloak_db_username)"
export KC_DB_PASSWORD="$(</run/secrets/keycloak_db_password)"
export KC_BOOTSTRAP_ADMIN_USERNAME="$(</run/secrets/keycloak_bootstrap_admin_username)"
export KC_BOOTSTRAP_ADMIN_PASSWORD="$(</run/secrets/keycloak_bootstrap_admin_password)"
for name in KC_DB_USERNAME KC_DB_PASSWORD KC_BOOTSTRAP_ADMIN_USERNAME KC_BOOTSTRAP_ADMIN_PASSWORD; do
    if [[ -z "${!name}" || "${!name}" == *replace-with-* ]]; then
        echo "Keycloak secret file is empty or contains a placeholder: $name" >&2
        exit 1
    fi
done

export AIOFFICE_KEYCLOAK_PROVISIONING_ENABLED="${AIOFFICE_KEYCLOAK_PROVISIONING_ENABLED:-false}"
export AIOFFICE_KEYCLOAK_PROVISIONING_CLIENT_SECRET=""
if [[ "$AIOFFICE_KEYCLOAK_PROVISIONING_ENABLED" == "true" ]]; then
    export AIOFFICE_KEYCLOAK_PROVISIONING_CLIENT_SECRET="$(</run/secrets/keycloak_provisioning_client_secret)"
    if [[ -z "$AIOFFICE_KEYCLOAK_PROVISIONING_CLIENT_SECRET" || "$AIOFFICE_KEYCLOAK_PROVISIONING_CLIENT_SECRET" == *replace-with-* ]]; then
        echo "Keycloak provisioning secret file is empty or contains a placeholder" >&2
        exit 1
    fi
fi

exec /opt/keycloak/bin/kc.sh "$@"
