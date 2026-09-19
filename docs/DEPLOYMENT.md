# Deployment Baseline

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
2. Git and PowerShell 7.
3. Network reachability to SQL Server through LAN/VPN/private routing.
4. An approved secure-tunnel/private-network client configured outside this repository.
5. A clean checkout at the exact Git commit to deploy.

Create the ignored runtime environment file:

```powershell
Copy-Item .env.production.example .env.production
```

Populate `.env.production` from approved secret sources. At minimum, set the RabbitMQ production identity/password and `AIOFFICE_DB_CONNECTION`. The application configuration stores only `secretref://env/AIOFFICE_DB_CONNECTION`; the resolved connection string exists only at the Core.Api runtime boundary.

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

For any Internet-reachable hostname, require provider-side access control before traffic reaches Core.Api. P0 does not yet include customer authentication/authorization, so an unauthenticated public route is not an acceptable production configuration.

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
