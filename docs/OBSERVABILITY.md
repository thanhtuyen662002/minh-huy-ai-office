# OpenTelemetry Observability Baseline

Minh Huy AI Office uses OpenTelemetry as the provider-neutral boundary for traces, metrics and logs.

## Service names

- `MinhHuy.AIOffice.Core.Api`
- `MinhHuy.AIOffice.Agent.Worker`

The .NET applications create telemetry even when no exporter is configured. Set `OTEL_EXPORTER_OTLP_ENDPOINT` to enable OTLP export. The endpoint must be an absolute HTTP/HTTPS URI; invalid endpoints fail closed at startup.

## Correlation dimensions

AI Office reserves:

- trace attribute `aioffice.task.id` / structured log field `TaskId`;
- trace attribute `aioffice.company.id` / structured log field `CompanyId`;
- trace attribute `aioffice.trace.id` / structured log field `TraceId`.

The Core API accepts optional `X-AIOffice-Task-Id` and `X-AIOffice-Company-Id` headers only as observability correlation hints. They are untrusted metadata and **must never be used for authentication, tenant selection, authorization, policy decisions, or database filtering**. Authorization continues to use authenticated platform context.

Correlation identifiers are bounded to 128 safe ASCII identifier characters to prevent log/header injection.

TaskId and TraceId are deliberately not emitted as metric labels because they are high-cardinality. Application metrics are recorded inside the active trace context so telemetry backends that support exemplars can correlate a metric sample to a trace without cardinality explosion. Company/tenant-specific metrics should only be added after an explicit cardinality/privacy review.

## Local path

Docker Compose provides:

1. OpenTelemetry Collector Contrib `0.161.0` on loopback ports 4317/4318.
2. Jaeger `2.21.0` on loopback port 16686.
3. Collector trace forwarding to Jaeger.
4. Collector debug export for P0 metrics/log inspection.

Start the stack:

```bash
cp .env.example .env
# Replace the RabbitMQ password placeholder in .env.
docker compose up -d
```

For an application launched directly on the host:

```bash
export OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:4317
dotnet run --project src/Core.Api/Core.Api.csproj
```

Then open `http://127.0.0.1:16686` and select the Core API service.

The local Jaeger store is transient. This compose path is for development/debugging, not a production observability architecture.

## Security and privacy

- Never add secrets, prompts, SQL text containing sensitive values, tokens, or resolved credentials as span attributes/log fields.
- CompanyId/TaskId are identifiers, not authority.
- Production exporter authentication/TLS configuration is release-managed and injected from approved secret references.
- Keep OTLP receivers and dashboards private; local ports are bound to loopback.
