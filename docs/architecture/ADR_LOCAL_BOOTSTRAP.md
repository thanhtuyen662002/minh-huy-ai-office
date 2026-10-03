# Local runtime provisioning

Status: proposed in issue #236, under full-product tracker #233.

The local development profile needs a real identity provider, databases and broker before FE sign-in can work. `compose.local.yaml` provisions SQL Server Developer, a pinned Keycloak with PostgreSQL, RabbitMQ, Redis, telemetry, bootstrap, API, worker and the standalone FE. Each installation has its own Compose project and durable volumes. Published ports bind IPv4 loopback. SQL, PostgreSQL, Redis and RabbitMQ have no host port.

`infra/initialize-local-config.ps1` generates identifiers and random credentials once in a protected directory outside the checkout. It writes an atomic manifest before service startup, retains it on repeat setup, validates every identifier and credential before regenerating the environment file, and refuses unsupported state rather than replacing it. Windows DACL and Linux file permissions protect this state. Host Docker administrators can inspect container environments; this profile is for a trusted local machine.

The non-root bootstrap container explicitly requires Development and local-bootstrap mode. It applies versioned EF migrations, creates the local OIDC realm/client/owner through the administrative API, and seeds the actual OIDC subject into the authoritative SQL authorization directory. It records installation identity in the same SQL transaction as company membership and roles. Repeat setup preserves revoked accounts, role edits and data-source changes. Changed installation identity and existing unowned platform data are refused.

SQL administration credentials are passed only to the bootstrap and SQL services. API and worker use a separate data reader/writer account without DDL or server administration. The sample ERP account has read-only permissions on a separate database. The sample is explicitly local test data; it does not prove access to a customer ERP or a live AI model.

The production profile remains separate and keeps local password-grant sign-in disabled. Local Keycloak start-dev, direct-access login, SQL Developer and HTTP identity metadata are confined to this profile. They are not production deployment defaults.

The required CI gate builds and starts the real profile, verifies FE sign-in and company context, authorized SQL connection, foreign-company and inactive-user denial, RabbitMQ worker completion, retained SQL/task/identity state after stop/start and repeat provisioning. Unit tests cover fail-closed bootstrap and preservation of user changes. CI uses an isolated manifest and removes only its own volumes after testing; user startup never deletes volumes.

`infra/start-local.ps1` is an engineering launcher for a machine with Docker already ready. A downloadable Windows installer, automatic prerequisites, initial-credential onboarding, Windows startup/reboot/recovery and every remaining P0–P6 workflow are still required by #233. Linux container integration cannot prove clean-Windows installation or Windows startup behavior.
