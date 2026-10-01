# Local Pilot End-to-End Validation

Validated against Git commit `8b9564ecc56a66c68431aaa1a95f12e90457c245`.

## Scope

This evidence covers the bounded internal pilot path only. It proves the runtime/control-plane chain for an authenticated, read-only data-source operation. It does **not** prove AI provider execution, ERP business-query answering, write operations, production identity, or production infrastructure.

## Verified local chain

The operator verified all of the following on one local Docker environment:

1. Real Keycloak OIDC token issuance succeeded.
2. Core.Api validated issuer, audience, signature and lifetime and preserved raw `idp`/`sub` claims.
3. `GET /api/auth/context` returned HTTP 200 and resolved the expected tenant, company, user and `admin` role from SQL.
4. A logical data source named `erp.local-pilot` was registered with `allowRead=true` and `allowWrite=false`.
5. The authenticated connection-test endpoint returned `success` after resolving `secretref://env/PILOT_ERP_CONNECTION` without exposing the underlying connection string.
6. `POST /api/tasks` returned HTTP 202 for an authenticated, company-scoped request.
7. The durable outbox published the task to RabbitMQ.
8. Agent.Worker consumed the queue with one active consumer and zero container restarts during the final smoke.
9. The worker executed the authorized read-only SQL connection probe and persisted a terminal checkpoint.
10. The final checkpoint contained `status=connected`, `evidence=read-only-connection-probe`, and `aiCredits=0`.
11. `GET /api/audit` returned a matching authorized `connection-test` audit record for the task.

## Final observed smoke result

```text
TASK_SUBMIT_HTTP: 202
TASK_ACCEPTED: True
TASK_COMPLETED: True
CHECKPOINT_EVIDENCE: PASS
AUDIT_HTTP: 200
AUDIT_MATCH: True
WORKER_RESTARTS: 0
QUEUE_CONSUMERS: 1
```

Authentication evidence immediately before the task smoke:

```text
OIDC_TOKEN: PASS
AUTH_CONTEXT_HTTP: 200
TENANT_MATCH: True
COMPANY_MATCH: True
USER_MATCH: True
ADMIN_ROLE: True
```

Read-only data-source evidence:

```text
OIDC_TOKEN: PASS
DATASOURCE_FOUND: True
CONNECTION_TEST_HTTP: 200
CONNECTION_TEST: PASS
RESULT_CODE: success
```

## Important limitation

The current `PilotDataSourceProbeExecutor` is deliberately a connectivity probe. It loads and validates the customer question as durable input, but it does not send that question to an AI provider and does not execute a business query against ERP data. The checkpoint intentionally reports `aiCredits=0`.

Therefore the correct claim is:

> The bounded authenticated task/worker/audit pipeline is operational end-to-end locally.

It is **not** yet correct to claim that the AI Office can answer accounting/ERP questions from live customer data.

## Remaining production prerequisites

Before the same release can be treated as a company pilot deployment:

- approved production OIDC issuer/audience and client flow;
- private SQL Server platform database with backup/restore verification;
- real read-only ERP credential stored outside Git;
- company host with Docker/runtime prerequisites;
- private tunnel/VPN boundary to Core.Api only;
- repeatable first-company/identity bootstrap procedure;
- authenticated production smoke with matching durable audit evidence.
