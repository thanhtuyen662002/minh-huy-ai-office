# Deployment Baseline

## Standalone FE runtime

Production Compose includes `web` alongside Core.Api and Agent.Worker. `infra/deploy-backend.ps1` builds/starts FE and waits for its loopback HTTP endpoint as well as the API and the worker's RabbitMQ consumer. The FE image runs without a host Node/npm installation, using Next.js standalone output and a non-root, read-only runtime. Cache and temporary files use bounded ephemeral mounts.

`AIOFFICE_WEB_PORT` defaults to 3000 and is bound to `127.0.0.1`. Company settings for a local profile are read at runtime. Never pass provider credentials, SQL connection strings or access tokens through public Next.js environment variables.

Production Compose sets `AIOFFICE_LOCAL_UI_ENABLED=false`. Browser Code/S256 sign-in can be enabled through the private operator configuration described in [browser sign-in setup](validation/BROWSER_SIGN_IN_OPERATOR.md). Its issuer is the same approved `AIOFFICE_AUTHORITY` as Core. HTTPS endpoints and private TLS Redis are required, and local HTTP/proof diagnostics are fixed off. FE HTTP readiness proves that a page is served; actual approved production identity, DB, broker, AI and customer business readiness need separate acceptance in [PRODUCT_COMPLETION.md](PRODUCT_COMPLETION.md).

CI builds the real image and runs `node scripts/smoke-web-container.mjs aioffice/web:ci`. It starts one image with two company environments, verifies runtime settings and static assets, enforces the non-root/read-only boundary and checks disabled sessions/login stay fail-closed with no-store responses. This is a mandatory Required quality gates dependency.

For a development/CI image check (Docker and Node are needed only for this verification):

```sh
docker build --file apps/web/Dockerfile --tag aioffice/web:ci .
node scripts/smoke-web-container.mjs aioffice/web:ci
```

A native Windows development artifact is published by Draft239 CI. Clean supported Windows installation, reboot/repair/startup and production signing remain unverified; track these gates in [issue #233](https://github.com/thanhtuyen662002/minh-huy-ai-office/issues/233).

This document defines the P0 deployment contract for Minh Huy AI Office. It is intentionally conservative: production credentials stay outside Git, backend services bind to the host loopback interface, and SQL Server is never published by this stack.

## Topology

```text
Vercel (Next.js web)
        |
        | HTTPS through approved identity-aware tunnel/private access
        v
24/7 Minh Huy host
  127.0.0.1:8080 -> Core.Api container
                    Agent.Worker container
                    RabbitMQ / Redis / OpenTelemetry / Jaeger
        |
        | private LAN/VPN only
        v
SQL Server / ERP data sources
```

The secure tunnel or private-network agent is an external access boundary. It must terminate to `http://127.0.0.1:<CORE_API_PORT>`. Never route SQL Server, RabbitMQ, Redis, OTLP or Jaeger through a public hostname.

## Vercel frontend

Create the Vercel project from this repository with:

- Root Directory: `apps/web`
- Framework: Next.js
- Production branch: `main`
- Build command: `npm run build`
- Install command: Vercel default `npm install`

`apps/web/vercel.json` pins the framework contract in source control.

Do not place database credentials, tunnel tokens, provider keys or other secrets in `NEXT_PUBLIC_*` variables. Browser-visible API configuration may contain only a public HTTPS endpoint after the backend authentication/access boundary exists.

## Backend host preparation

Prerequisites on the 24/7 host:

1. Docker Engine/Desktop with Compose v2.
2. .NET SDK 10.0.401 (or a compatible .NET 10 patch) for the versioned migration script.
3. Git and PowerShell 7.
4. Network reachability to SQL Server through LAN/VPN/private routing.
5. An approved secure-tunnel/private-network client configured outside this repository.
6. A clean checkout at the exact Git commit to deploy.

Create the ignored runtime environment file:

```powershell
Copy-Item .env.production.example .env.production
```

Populate `.env.production` from approved secret sources. Set the RabbitMQ production identity/password, OIDC issuer/audience, `AIOFFICE_DB_CONNECTION`, and the read-only pilot ERP connection. The application configuration stores only `secretref://env/AIOFFICE_DB_CONNECTION`; the resolved connection string exists only at the Core.Api runtime boundary. The worker resolves the pilot source through `secretref://env/PILOT_ERP_CONNECTION`.

Use the configured Core.Api host port for the smoke commands below (the production example defaults to `8080`):

```powershell
$apiPort = if ([string]::IsNullOrWhiteSpace($env:CORE_API_PORT)) { "8080" } else { $env:CORE_API_PORT }
```

The production Compose file starts Core.Api and Agent.Worker with the same release tag. With the database, RabbitMQ, OIDC and pilot secret configured, the worker runs the bounded read-only data-source connectivity probe behind the trusted tool metadata, permission and audit gates. If any prerequisite is absent, startup or task execution fails closed; a running container alone is not proof that the pilot path has completed a task.

## Operator path

Run these steps from a clean checkout of the release SHA on the private backend host. The commands assume PowerShell 7 and Docker Compose v2.

### 1. Prepare SQL Server and a backup

Use a private LAN/VPN route. Do not publish TCP 1433 through the tunnel or Compose. Before the first migration, take a full backup of the target SQL Server database and record where the backup and restore verification will be retained. The rollback procedure is only safe while the release remains inside the schema compatibility window.

Create the empty platform database and a dedicated login using the organization's DBA process. The password is generated and stored in the approved secret manager; it must never appear in this repository, shell history, a ticket, or a data-source record. A typical DBA sequence is:

```sql
CREATE DATABASE [AIOffice_Pilot];
-- Create the login and database user through the approved password/secret workflow.
-- Grant the migration identity the DDL rights required for the release window.
-- After migration, reduce the long-running application identity to the minimum
-- read/write permissions required by the application and its append-only audit tables.
```

Set the resulting private connection string only in the ignored `.env.production` file (or inject the same name into the migration process environment):

```text
AIOFFICE_DB_CONNECTION=<resolved-by-secret-manager-on-the-private-host>
```

Apply the versioned migrations with the repository script. It reads the connection string into the child process environment and does not put it on the command line or in a generated file:

```powershell
dotnet tool restore
pwsh ./infra/migrate-platform-database.ps1 -EnvironmentFile .env.production
```

To move to a specific compatibility point, pass the exact migration name with `-TargetMigration`; do not edit migration history manually.

### 2. Validate and start infrastructure

Run the same checks used by CI before changing traffic:

```powershell
docker compose --env-file .env.production -f compose.yaml -f compose.production.yaml config --quiet
$rendered = Join-Path $env:TEMP "aioffice-deployment-compose.json"
docker compose --env-file .env.production -f compose.yaml -f compose.production.yaml config --format json | Set-Content -LiteralPath $rendered
python scripts/validate-deployment.py $rendered
```

Start the durable infrastructure and wait for health checks:

```powershell
docker compose --env-file .env.production -f compose.yaml -f compose.production.yaml up -d rabbitmq redis otel-collector jaeger
docker compose --env-file .env.production -f compose.yaml -f compose.production.yaml ps
```

RabbitMQ AMQP, Redis, OpenTelemetry and Jaeger remain loopback/private services. Only the Core.Api loopback endpoint is eligible for the approved identity-aware tunnel.

### 3. Start API and worker

After the database migration succeeds, deploy the release images and check that Core.Api is serving its process health endpoint:

```powershell
pwsh ./infra/deploy-backend.ps1 -EnvironmentFile .env.production
docker compose --env-file .env.production -f compose.yaml -f compose.production.yaml ps
Invoke-WebRequest http://127.0.0.1:$apiPort/health -UseBasicParsing
```

Inspect both application logs before routing traffic:

```powershell
docker compose --env-file .env.production -f compose.yaml -f compose.production.yaml logs --since 5m core-api agent-worker
```

`/health` is an HTTP/process check; it does not prove that SQL Server or RabbitMQ is reachable. The dependency checks are the healthy Compose services, startup logs, and the authenticated task smoke test in the pilot gate.

The deployment script does not create an identity provider, a first company, a task executor, or a public route. Those are explicit pilot prerequisites below.

### 4. Configure identity and the first company

Configure an OIDC issuer and audience in the approved secret/configuration store. Core.Api accepts an authenticated request only when the JWT is valid and the server can resolve the `(identity provider, subject, company id)` tuple to active rows in `aioffice.Users`, `aioffice.Companies`, `aioffice.CompanyMemberships` and `aioffice.RoleAssignments`. Browser-supplied tenant and user headers are ignored; `X-AIOffice-Company-Id` is only a company selector and is verified against the membership directory.

There is no bootstrap-admin HTTP endpoint in this release. A designated administrator/DBA must provision the first IdP subject, company, membership and role through the approved versioned bootstrap procedure, then verify the server-derived context:

```powershell
$headers = @{ Authorization = "Bearer <short-lived-pilot-token>"; "X-AIOffice-Company-Id" = "<company-guid>" }
Invoke-RestMethod http://127.0.0.1:$apiPort/api/auth/context -Headers $headers
```

Do not insert real credentials or copy a production token into Git or this document.

### 5. Register the first data source

Create a secret-manager reference, never a connection string, and use the authenticated company context:

```powershell
$body = @{
  logicalName = "erp.pilot"
  kind = "sqlserver"
  environment = "pilot"
  purpose = "approved internal pilot"
  connectionSecretReference = "secretref://env/PILOT_ERP_CONNECTION"
  allowRead = $true
  allowWrite = $false
  maxConcurrency = 1
  isEnabled = $true
} | ConvertTo-Json
Invoke-RestMethod http://127.0.0.1:$apiPort/api/data-sources/ -Method Post -Headers $headers -Body $body -ContentType "application/json"
```

List the registry and run its connection test. Responses intentionally omit secret references and connection strings:

```powershell
Invoke-RestMethod http://127.0.0.1:$apiPort/api/data-sources/ -Headers $headers
Invoke-RestMethod http://127.0.0.1:$apiPort/api/data-sources/<data-source-guid>/connection-test -Method Post -Headers $headers
```

### 6. Frontend and smoke test

Run the web checks from the repository root, then deploy the `apps/web` directory to the production Vercel project:

```powershell
npm install --no-audit --no-fund
npm run web:test
npm run web:typecheck
npm run web:build
```

Set only the public HTTPS API base URL in Vercel. Keep OIDC client secrets, SQL credentials, RabbitMQ credentials, tunnel tokens and data-source secret references server-side. The browser must obtain company context from the authenticated API response and must never become an authority source.

The minimum smoke test is:

```powershell
Invoke-WebRequest http://127.0.0.1:$apiPort/ -UseBasicParsing
Invoke-WebRequest http://127.0.0.1:$apiPort/health -UseBasicParsing
Invoke-RestMethod http://127.0.0.1:$apiPort/api/auth/context -Headers $headers
Invoke-RestMethod http://127.0.0.1:$apiPort/api/data-sources/ -Headers $headers
Invoke-RestMethod http://127.0.0.1:$apiPort/api/audit?offset=0`&limit=50 -Headers $headers
```

An unauthenticated request to `/api/auth/context`, `/api/data-sources/`, `/api/audit` or `/api/sla/status` must return `503` when authentication is not configured; an authenticated identity without an active company membership must return `403`.

## Optional model-backed pilot runtime

The worker keeps the verified read-only connectivity-probe executor when AI runtime configuration is absent. For normal testing and real operation, configure the direct API as the PRIMARY provider:

```text
AIOFFICE_AI_PRIMARY_BASE_URL=https://api.openai.com/v1
AIOFFICE_AI_PRIMARY_MODEL=<approved-direct-api-model>
AIOFFICE_AI_PRIMARY_AUTHORIZATION=Bearer <runtime-secret-api-key>
AIOFFICE_AI_PRIMARY_PROVIDER_ID=openai-direct
```

An external OpenAI-compatible provider may be configured as BACKUP:

```text
AIOFFICE_AI_BACKUP_BASE_URL=<backup-provider-base-url-ending-in-v1>
AIOFFICE_AI_BACKUP_MODEL=<approved-backup-model>
AIOFFICE_AI_BACKUP_AUTHORIZATION=<complete-authorization-header-value>
AIOFFICE_AI_BACKUP_PROVIDER_ID=external-backup
```

The worker always attempts PRIMARY first. BACKUP is used only after a retryable primary failure such as network failure, timeout, HTTP 408, 429 or 5xx. Permanent primary failures such as invalid credentials or malformed requests fail closed instead of being hidden by backup. A BACKUP without PRIMARY is rejected at worker startup.

The shipping provider client disables redirects and automatic response decompression. Configured provider URLs must use HTTPS and must not contain user-info, query strings or fragments. An explicitly owned loopback fixture may set `<prefix>_ALLOW_INSECURE_LOOPBACK=true` alongside its HTTP loopback URL; this does not permit a remote HTTP provider. Prefixes follow the existing primary/backup/legacy names. Never use the coding agent credentials for runtime calls.

StructuredGeneration uses the strict Responses JSON-schema profile described in [checkpoint verification](validation/GROUP_STRUCTURED_RESPONSES.md). Operators must separately qualify the configured model/profile; this transport checkpoint is not evidence of semantic accuracy or customer activation.

All authorization values are secret material. Do not commit them, print them in smoke output, persist them in a task/checkpoint, or expose them to the browser. A partial provider configuration is rejected at worker startup. The legacy `AIOFFICE_AI_*` single-provider names remain supported as a backward-compatible primary alias when explicit `AIOFFICE_AI_PRIMARY_*` values are absent.

When enabled, the bounded AI executor still verifies the selected read-only SQL data source first, then sends only the durable customer question plus a fixed instruction explaining that no ERP rows have been supplied. The model is not allowed to claim that it queried live ERP data. The checkpoint stores the answer, the provider actually used after any failover, model identity and input/output token counts, but no connection string or secret reference.

This slice proves real provider execution only. It does not yet let the model generate or execute SQL, retrieve ERP business rows, or settle customer AI credits. Those remain separate gated work.

## Pilot activation gate

This checkout now contains one bounded, read-only pilot path: authenticated `POST /api/tasks` accepts a `CustomerPilotTaskRequest` plus an `Idempotency-Key`, derives company/user authority on the server, persists the task graph and pending dispatch, and the outbox publishes it to RabbitMQ. Agent.Worker composes the authorized executor, probes the selected `secretref://` data source, persists a redacted checkpoint and task status, and the existing audit/settlement boundary remains before broker acknowledgement. `GET /api/tasks/{taskId}` and `/api/audit` provide the durable result and evidence projections.

The path is enabled only when the pilot supplies the external OIDC issuer/audience, SQL Server connection, RabbitMQ credentials, and a valid read-only data-source secret. If any of those are absent, startup or the request fails closed; starting containers alone is not a successful task smoke test. Exercise the path with the authenticated request below only after the identity/company bootstrap and data-source registration steps have succeeded.

```powershell
$task = @{ dataSourceId = "<data-source-guid>"; question = "read the current customer balance" } | ConvertTo-Json
$taskHeaders = $headers.Clone()
$taskHeaders["Idempotency-Key"] = "pilot-$(Get-Date -Format yyyyMMddHHmmss)"
$accepted = Invoke-RestMethod http://127.0.0.1:$apiPort/api/tasks -Method Post -Headers $taskHeaders -Body $task -ContentType "application/json"
Invoke-RestMethod "http://127.0.0.1:$apiPort/api/tasks/$($accepted.taskId)" -Headers $headers
Invoke-RestMethod http://127.0.0.1:$apiPort/api/audit?offset=0`&limit=50 -Headers $headers
```

The bounded implementation and its remaining external prerequisites are tracked in [pilot issue #212](https://github.com/thanhtuyen662002/minh-huy-ai-office/issues/212). Do not route customer traffic until the smoke test returns a completed task and a matching audit record.

## Validate before deployment

The same deterministic checks run in CI:

```powershell
docker compose --env-file .env.production -f compose.yaml -f compose.production.yaml config --quiet
```

CI additionally parses the rendered Compose JSON and fails if:

- any service publishes a host port outside loopback,
- any service publishes SQL Server port 1433,
- Core.Api is not bound to `127.0.0.1`,
- Agent.Worker publishes any host port,
- the platform DB secret-reference contract changes.

## Deploy backend

From a clean checkout of the intended release commit:

```powershell
pwsh ./infra/deploy-backend.ps1
```

The script:

1. requires a clean Git worktree,
2. tags application images with the current Git SHA,
3. validates the Compose configuration,
4. builds/starts the backend stack,
5. checks `/health` through the loopback binding.

It does **not** expose the API publicly, change tunnel configuration, or apply destructive database migrations.

## Secure access boundary

Approved patterns include an identity-aware outbound tunnel or a private VPN/tailnet. Configure the provider to reach only the loopback Core.Api endpoint.

For any Internet-reachable hostname, require provider-side access control before traffic reaches Core.Api. Core.Api validates the OIDC token and server-side company membership for customer routes, but an unauthenticated public route is still not an acceptable production configuration.

The access boundary must never route:

- TCP 1433 / SQL Server,
- RabbitMQ AMQP or management UI,
- Redis,
- OpenTelemetry receivers,
- Jaeger UI.

## Rollback

Rollback is source-controlled and release-based, not a manual file edit.

1. Stop routing new traffic to the unhealthy release at the secure access boundary when possible.
2. Identify the last known-good Git SHA whose schema contract is still compatible.
3. Check out that exact clean commit on the backend host.
4. Run `pwsh ./infra/deploy-backend.ps1`.
5. Confirm `/health` and relevant smoke checks.
6. Restore traffic only after health is green.

Do not roll application code backward across a contracted/breaking database migration. Database evolution remains `EXPAND -> MIGRATE -> CONTRACT`; rollback is guaranteed only inside the active compatibility window.

## Production safety boundaries

- No plaintext credential is committed to Git.
- No application or infrastructure service publishes SQL Server.
- All host-published Compose ports are loopback-only.
- Core.Api and Agent.Worker run as non-root containers with a read-only root filesystem, dropped Linux capabilities and `no-new-privileges`.
- The worker has no published host port.
- Production changes come from Git commits and the versioned deployment script, not manual source edits on the host.
- External Vercel/tunnel account setup and credentials are intentionally outside source control.
