# Local Pilot End-to-End Validation

## Baseline authenticated runtime validation

The original bounded pilot validation proved the authenticated read-only runtime/control-plane chain with real local OIDC/JWT, server-derived company authorization, SQL-backed task state, RabbitMQ worker execution, read-only data-source resolution, durable checkpoint completion and audit evidence.

Observed baseline smoke:

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

Authentication evidence:

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

## Real primary AI provider validation — 2026-10-02

The operator subsequently enabled the existing model-backed pilot path with a direct OpenAI primary provider and rebuilt only Agent.Worker.

Observed end-to-end result:

```text
TASK_SUBMIT_HTTP: 202
CHECKPOINT: PASS
AI_PROVIDER: openai-direct
AI_MODEL: gpt-6-sol
AI_ANSWER: MH-AI-OFFICE-E2E-OPENAI-DIRECT-OK
AI_INPUT_TOKENS: 121
AI_OUTPUT_TOKENS: 31
AI_TOTAL_TOKENS: 152
PROVIDER_CHECK: PASS
MODEL_CHECK: PASS
ANSWER_CHECK: PASS
TOKEN_USAGE_CHECK: PASS
REAL_OPENAI_E2E: PASS
```

This proves the local chain:

> OIDC -> Core.Api -> durable SQL task/outbox -> RabbitMQ -> Agent.Worker -> authorized read-only connection probe -> OpenAI direct -> durable checkpoint -> authorized result projection.

The provider credential itself is not stored in Git and is not part of this evidence.

## Issue #223 / PR #224 — bounded ERP-grounded AI slice

The next slice replaces question-only reasoning with a bounded SQL Server catalog evidence read before the model call.

Safety properties of this slice:

- the customer question and model output never become SQL text;
- the query is fixed and server-authored;
- the query reads SQL Server catalog metadata only;
- command timeout is 10 seconds;
- at most 20 table summaries are included;
- the data source must remain enabled, readable and non-writable;
- connection strings and secret references never enter model context or checkpoint payloads.

The model receives only:

- database name;
- total non-system table count;
- up to 20 table schema/name pairs with approximate row counts;
- the customer question.

The checkpoint records the same bounded catalog evidence plus provider/model/token usage.

## Bounded ERP-grounded AI validation — 2026-10-02

The operator rebuilt Agent.Worker from PR #224 and executed the authenticated local pilot against `erp.local-pilot` with the direct OpenAI primary.

Observed result:

```text
WORKER_REBUILD: PASS
WORKER_RUNNING: True
WORKER_RESTARTS: 0
OIDC_TOKEN: PASS
DATASOURCE: PASS
DATASOURCE_READ: True
DATASOURCE_WRITE: False
TASK_SUBMIT_HTTP: 202
CHECKPOINT: PASS
AI_PROVIDER: openai-direct
AI_MODEL: gpt-6-sol
AI_ANSWER: DATABASE=AIOffice_Pilot_CleanTest; TABLE_COUNT=19
ERP_DATABASE: AIOffice_Pilot_CleanTest
ERP_TABLE_COUNT: 19
ERP_SAMPLED_TABLES: 19
ERP_LARGEST_TABLE: aioffice.__EFMigrationsHistory
ERP_LARGEST_TABLE_ROWS: 12
AI_INPUT_TOKENS: 440
AI_OUTPUT_TOKENS: 19
AI_TOTAL_TOKENS: 459
EVIDENCE_TYPE: ai-provider-reasoning-after-bounded-read-only-erp-catalog
PROVIDER_CHECK: PASS
MODEL_CHECK: PASS
ERP_EVIDENCE_CHECK: PASS
DATABASE_EVIDENCE_CHECK: PASS
TABLE_COUNT_CHECK: PASS
SAMPLE_BOUND_CHECK: PASS
SECRET_LEAK_CHECK: PASS
ANSWER_DATABASE_GROUNDED: PASS
ANSWER_TABLE_COUNT_GROUNDED: PASS
ERP_GROUNDED_AI_E2E: PASS
```

This proves that the model response was grounded in bounded catalog evidence retrieved from the authorized SQL Server data source. It does not prove access to business-row values.

## Current limitation

This slice does **not** expose business-row contents. It can ground answers about the selected database/catalog, but it must not claim to know balances, invoice values, inventory quantities, customer records or other business values.

Those require separate, explicitly authorized business-query capabilities with their own deterministic query contracts, bounds, redaction rules, audit actions and tests.

Therefore the correct progression is:

1. authenticated read-only connectivity — proven;
2. real primary AI provider execution — proven;
3. bounded ERP catalog-grounded AI — implementation in #223 / PR #224, local E2E still required;
4. capability-specific business queries — future slice;
5. write/accounting execution — separate higher-risk scope, not enabled here.

## Remaining production prerequisites

Before treating the same release as a company production pilot:

- approved production OIDC issuer/audience and client flow;
- private SQL Server platform database with verified backup/restore;
- real read-only ERP credential stored outside Git;
- company host with private RabbitMQ/worker runtime;
- private tunnel/VPN boundary to Core.Api only;
- repeatable first-company/identity bootstrap procedure;
- authenticated production smoke with durable audit evidence.
