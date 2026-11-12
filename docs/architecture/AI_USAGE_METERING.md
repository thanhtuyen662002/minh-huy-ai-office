# AI usage metering, limits and concurrency

Phase 1 of making Trợ lý ready for many companies and many people at once.

## What is recorded

| Table | Written by | When | Runtime rights |
|---|---|---|---|
| `AiQuestionAdmissions` | Core.Api, inside the submission transaction | every accepted question | insert/read only |
| `AiUsageRecords` | Agent.Worker (`MeteredAiGateway`) | every answered model call (an agent question makes several) | insert/read only |
| `AiPrices` | Core.Api (admin) | a price is set or changed (new row, effective now) | insert/read only |
| `AiBillingAccounts`, `AiBillingAccountSources` | Core.Api (admin) | a billed customer or its data sources change | read/write |

- A usage row keeps the provider, the reported model, input / cached input / output tokens, the asker, the data source,
  the customer it was billed to **at that moment**, and the cost computed with the price in effect **at that moment**.
  Moving a source to another customer or changing a price never rewrites history.
- `Cost` is null when no price matches the model; the tokens are still recorded and the report flags those calls.
- Prices are VND per million tokens. A price model matches exactly, else the longest price model the reported model
  starts with (providers report dated variants).
- Months are Vietnam time (UTC+7).
- The usage row is written with its own short-lived `PlatformDbContext`, so it commits even when the question later fails
  and never mixes with the delivery handler's tracked changes. A failed write is logged at error level with the token counts.

## Limits

Checked in `AiUsageAdmission` while the submission holds the company lock (so counts cannot race):

1. **Per person**: 20 questions per minute (`AiUsageAdmission.UserQuestionsPerMinute`).
2. **Per customer**: `QuestionsPerMinute`, when set.
3. **Monthly budget**: when the customer's spend this month has reached `MonthlyBudget`.

A refusal is HTTP 429 `{ error, code }` with code `ai-user-rate`, `ai-account-rate` or `ai-budget`; nothing is written and
the prepared question stays saved so it can be sent again. The worker also checks the budget **before every model call**,
so a long agent run stops (permanent failure, readable message) as soon as the budget is spent. A question may overshoot
the budget by at most the calls already in flight.

## Concurrency

- The worker handles `RabbitMqWork:ConsumerConcurrency` deliveries at once (default 4, at most 32 and the prefetch). Each
  delivery has its own DI scope and database context; acknowledgements on the shared channel are serialized.
- Each data source's `MaxConcurrency` ("Đồng thời") is enforced by `DataSourceConcurrencyLimiter` for the whole question.
  A question that waits more than 90 s for a slot fails as transient and is retried. The limiter is in-process: correct for
  the single worker we run; a second worker instance would need a shared (database or Redis) gate.

## Not in this phase

Server-side conversations, the external ERP API (HMAC, per-ERP-user identity in `ExternalUserId`), row-level data scope
per user, streaming, and write/delete actions.
