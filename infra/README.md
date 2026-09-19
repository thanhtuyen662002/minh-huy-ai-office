# Local Infrastructure

This directory documents the infrastructure used by Minh Huy AI Office. The initial Docker Compose baseline runs only infrastructure that is safe and useful for local development.

## Services

### RabbitMQ
- AMQP: `127.0.0.1:5672`
- Management UI: `127.0.0.1:15672`
- Durable named volume.
- Health check via `rabbitmq-diagnostics ping`.

### Redis
- Redis: `127.0.0.1:6379`
- Append-only persistence enabled for development convenience.
- Durable named volume.
- Health check via `redis-cli ping`.

Ports are bound to loopback by default. They are not intentionally exposed to the public network.

## Start

```bash
cp .env.example .env
# Replace RABBITMQ_DEFAULT_PASS in the ignored .env before starting services.
docker compose up -d
docker compose ps
```

## Stop

```bash
docker compose down
```

Use `docker compose down -v` only when intentionally deleting local RabbitMQ/Redis development data.

## Validate configuration

```bash
docker compose --env-file .env.example config --quiet
```

## SQL Server strategy

SQL Server is deliberately not created by this compose baseline because Minh Huy may use:
- SQL Server on the same 24/7 PC,
- another server on the Minh Huy LAN,
- a customer/private remote SQL Server,
- a dedicated SQL Server for AI Office state.

Rules:
1. Never expose SQL Server port 1433 directly to the public Internet.
2. Remote SQL connectivity must use a trusted private network/VPN/site-to-site path.
3. Credentials never live in Git, prompts, or ordinary configuration/application tables.
4. Data Source Registry stores logical resource metadata and a `secretref://provider/resource`, not plaintext credentials.
5. Agents request a logical source such as `company.erp.production`; Tool Gateway resolves the physical server/database and secret at the authorized runtime boundary.
6. Dev/staging/prod use separate databases, identities and secret namespaces.

For local API development, an example reference is:

```text
AIOffice__PlatformDatabase__ConnectionSecretRef=secretref://env/AIOFFICE_DB_CONNECTION
```

Set the actual `AIOFFICE_DB_CONNECTION` only in the ignored local environment/process environment. When the backend itself runs in Docker on Windows/macOS Docker Desktop and must reach SQL Server on the host, `host.docker.internal` may be used for development. Production addressing must be explicit and environment-specific.
