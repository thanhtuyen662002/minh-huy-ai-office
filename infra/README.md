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
docker compose --env-file .env.example config
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
3. Credentials never live in Git, prompts, or ordinary configuration tables.
4. The future Data Source Registry stores logical resource metadata and a secret reference, not plaintext credentials.
5. Agents request a logical source such as `company.erp.production`; Tool Gateway resolves the physical server/database.
6. Dev/staging/prod use separate databases/credentials.

When the backend itself runs in Docker on Windows/macOS Docker Desktop and must reach a SQL Server on the host, `host.docker.internal` may be used for development. Production addressing must be explicit and environment-specific.
