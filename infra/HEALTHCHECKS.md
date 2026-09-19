# Infrastructure Health Checks

## RabbitMQ
```bash
docker compose exec rabbitmq rabbitmq-diagnostics -q ping
```

## Redis
```bash
docker compose exec redis redis-cli ping
```

Expected Redis result: `PONG`.

## Failure behavior
- Containers use `restart: unless-stopped`.
- Persistent data lives in Docker named volumes.
- Application workers must still implement retry/backoff/circuit-breaker behavior; container restart is not a business-level recovery strategy.
- RabbitMQ/Redis are not the source of truth for durable business task state; SQL Server will remain the durable task source of truth.
