# Full product completion matrix

Tracking issue: [#233](https://github.com/thanhtuyen662002/minh-huy-ai-office/issues/233). Persistent goal: the complete product and one-click Windows setup.

## Scope and evidence policy

Every bullet of `docs/ROADMAP.md` is represented below: 47 roadmap requirements, 12 Windows setup requirements and 3 completion gates. The historical closure of a thin-slice EPIC does not prove completion of its full product scope. No percentage is inferred from test counts or closed issues.

Baseline inspected on 2026-10-03: `fd804753ee7ac181f1885b07b5bb673ba927fd58`. [Build 36997742737](https://github.com/thanhtuyen662002/minh-huy-ai-office/actions/runs/36997742737) and [Governance 36997742805](https://github.com/thanhtuyen662002/minh-huy-ai-office/actions/runs/36997742805) passed. The tracked pilot documentation is `docs/validation/LOCAL_PILOT_E2E.md` and `LOCAL_WEB_UI.md`. A tracked full browser/business-workflow run report was not found in this baseline; instructions and source code alone do not prove that those scenarios passed.

`Partial` means source/contracts or bounded evidence exist but do not prove the full acceptance below. `Missing` means the required shipped flow or installation artifact has not been found in the inspected baseline. Neither means `Done`. Mark a requirement `Done` only with its actual implementation paths, exact version and a relevant passing run or observable runtime evidence. Unit tests of a contract cannot replace a real browser/API/worker/database or clean-Windows test where that is the requirement.

Implementation #234 supplies the FE container dependency and runtime configuration test. It does not complete the Windows installer, production identity, accounting writes, billing, HA/DR or the full product. Keep #233 and the persistent goal active.

## P0 — Engineering foundation

| ID | Requirement | Required acceptance | Current state |
| --- | --- | --- | --- |
| P0-01 | Governance and ADRs | Current issue/PR/head/CI and architecture decisions let a new contributor resume without chat history. | Partial |
| P0-02 | Monorepo, .NET solution and Next.js workspace | A clean checkout reproducibly installs, builds and tests every workspace from tracked inputs. | Done — clean hosted checkout restore/build/test and web install/type/build at accepted main002, Build37801975078. |
| P0-03 | Local Docker dependencies | SQL, identity, RabbitMQ, Redis and observability are provisioned automatically and retain durable data across restart. | Partial |
| P0-04 | Core API and Agent Worker skeletons | Authenticated requests cross the real API, broker, worker and database with useful health/error reporting. | Partial |
| P0-05 | Next.js/shadcn web skeleton | The browser UI builds reproducibly, serves its assets and runs as a non-root container using runtime configuration. | Done — FE container runtime smoke and actual Chromium at accepted main002, Build37801975078/actual113396238709. |
| P0-06 | Initial migrations and shadow test harness | Fresh install, upgrade and rollback/compatibility checks execute against real SQL Server and validate schema history. | Partial |
| P0-07 | CI format/build/tests and observability | Mandatory CI covers every shipped service; a real request can be followed through logs, metrics and traces. | Partial |
| P0-08 | Secret provider and development bootstrap | Installer generates/stores local secrets safely and bootstraps resources without editing env files or logging credentials. | Partial |

## P1 — Platform core

| ID | Requirement | Required acceptance | Current state |
| --- | --- | --- | --- |
| P1-01 | Tenant/company/user/role model | Users and roles are administered through supported product flows; inactive or cross-company access is denied at every boundary. | Partial |
| P1-02 | Data Source Registry and typed secret references | An owner registers, tests, enables/disables and selects a source through the UI without exposing credentials. | Partial |
| P1-03 | Task/step/checkpoint/execution/event persistence | A multi-step task resumes after process failure with correct dependencies, lease fencing and owner-scoped durable results. | Partial |
| P1-04 | RabbitMQ envelopes, retries, DLQ and idempotency | Real broker fault/redelivery tests prove bounded retries, dead-letter handling and one authoritative durable completion. | Partial |
| P1-05 | Permission/risk/approval engine | Read, write and high-risk operations obey real resource policies and approvals; denied actions never execute. | Partial |
| P1-06 | Audit log and change history | Durable immutable/redacted evidence is produced for each operation and remains scoped to the requesting company. | Partial |
| P1-07 | SignalR task-status streams | Authenticated users receive live task updates and recover after reconnect without stale or cross-company events. | Partial |
| P1-08 | Manager Agent and specialist roles | Manager plans and delegates real IT/ERP work to authorized specialists and persists outcomes and escalation. | Partial |

## P2 — AI, context and model layer

| ID | Requirement | Required acceptance | Current state |
| --- | --- | --- | --- |
| P2-01 | Provider adapters and AI Gateway | Supported configured providers execute real bounded requests; provider faults, cancellation and credentials are handled safely. | Partial |
| P2-02 | Model Registry and routing | Capability/health/budget policy selects a compatible model and applies safe fallback; unsupported calls fail closed. | Partial |
| P2-03 | Context engine and task memory | Conversation/task memory is durable, budgeted and authorized; another user/company cannot retrieve or inject it. | Partial |
| P2-04 | Conversation and attachment ingestion | Supported attachments are uploaded, validated, extracted and used in an authorized conversation with safe limits. | Partial |
| P2-05 | Usage/cost ledger | Real token/cost usage, holds and settlement appear in durable balances; retry/replay cannot duplicate charges. | Partial |
| P2-06 | Eval/replay framework | Representative versioned evaluations and deterministic replay detect functional, safety and tenant-isolation regressions. | Partial |

## P3 — ERP adaptability

| ID | Requirement | Required acceptance | Current state |
| --- | --- | --- | --- |
| P3-01 | Schema Observer and snapshots/diffs | The observer captures a real authorized ERP schema, detects changes and publishes durable versioned diffs. | Partial |
| P3-02 | ERP Catalog | DB objects, forms, reports and capabilities can be searched and used through the supported product flows. | Partial |
| P3-03 | Feature Registry and compatibility contracts | Incompatible schema/provider/feature versions prevent execution; supported capabilities have traceable implementations. | Partial |
| P3-04 | Source diff indexing | A real version/source change is indexed and linked to affected catalog objects and workflows. | Partial |
| P3-05 | Change-impact analysis | Changes identify affected workflows and gate unsafe operations with inspectable evidence. | Partial |
| P3-06 | Versioned skills/workflows/adapters | Supported ERP adapters execute versioned semantic capabilities against authorized real data and survive tested upgrades. | Partial |

## P4 — Controlled self-improvement

| ID | Requirement | Required acceptance | Current state |
| --- | --- | --- | --- |
| P4-01 | IT Agent coding loop | A product-issued IT task creates its issue/branch/PR, runs CI/evaluations and reports its gated result through the UI. | Partial |
| P4-02 | Migration generation and shadow DB tests | Generated changes are tested on a shadow database before compatible deployment; failure prevents release. | Partial |
| P4-03 | Release Catalog | Operators inspect immutable release manifests, artifact hashes, schema compatibility and deployment evidence. | Partial |
| P4-04 | Canary/blue-green/drain/rollback | A real staged deployment and fault scenario prove safe draining, compatibility and restoration of the previous release. | Partial |
| P4-05 | Emergency capability kill switch | An authorized operator disables a capability; queued and executing work respects the fence without bypass. | Partial |
| P4-06 | Dependency/model upgrade candidates | Candidates have versioned evidence and evaluations; unsafe upgrades cannot become the active release/model. | Partial |
| P4-07 | Self-healing versus self-improvement | Bounded repair restores availability without unauthorized code/schema changes or repeated business effects. | Partial |

## P5 — Accounting and operations automation

| ID | Requirement | Required acceptance | Current state |
| --- | --- | --- | --- |
| P5-01 | Accounting investigation workflows | Authorized users investigate supported accounting questions from real scoped evidence and inspect the result and audit. | Partial |
| P5-02 | Posting preview/execute/reconcile | Typed previews, approvals, execution and reconciliation work end to end; duplicate requests cannot duplicate posting. | Partial |
| P5-03 | Tax/invoice integrations | Supported authorized integrations validate, submit and reconcile invoices with durable retries and redacted audit. | Partial |
| P5-04 | Order/inventory workflows | Supported order/inventory operations enforce authorization, concurrency and reconciliation through BE and FE. | Partial |
| P5-05 | Exception queue and specialist escalation | Failures enter a durable queue; authorized specialists act, retry or resolve them through the product with audit. | Partial |
| P5-06 | Bulk deterministic execution | A representative batch has deterministic/idempotent execution, partial-failure recovery and bounded AI planning/escalation. | Partial |

## P6 — Customer-facing multi-tenant product

| ID | Requirement | Required acceptance | Current state |
| --- | --- | --- | --- |
| P6-01 | Customer identity and role enforcement | Production OIDC login, company selection, membership/role administration and session expiry work in the actual browser. | Partial |
| P6-02 | Customer portal and chat | Customers use complete navigation, durable conversations, task history/results and actionable failure/recovery states. | Partial |
| P6-03 | Plans, AI credits and billing | Plans, balances, admission/reservation/settlement and supported billing flows are operable in BE and FE. | Partial |
| P6-04 | SLA and priority controls | Plan/resource priority settings affect real scheduling and display truthful service status under load/failure. | Partial |
| P6-05 | Customer-level audit and reporting | Customers browse/filter/export scoped durable audit/reporting evidence; unauthorized data never appears. | Partial |
| P6-06 | Hardening, HA, DR and multi-server scale | Multi-instance, restart, database/broker outage and backup-restore drills prove isolation, recovery and no duplicate effects. | Partial |

## W — One-click Windows installation

| ID | Requirement | Required acceptance | Current state |
| --- | --- | --- | --- |
| W-01 | One setup artifact | One double-clickable delivered file obtains the versioned application payload with integrity verification. | Missing |
| W-02 | Automatic prerequisites | Supported Windows/architecture/virtualization checks, Docker/WSL dependencies and required restart/resume are handled without command-line setup. | Missing |
| W-03 | Automatic resources | Pinned FE, BE, worker, SQL, identity, broker, cache and observability resources are downloaded/provisioned with bounded retries. | Missing |
| W-04 | Automatic configuration and secrets | Local credentials and service configuration are generated and stored with restrictive permissions; no manual env editing is required. | Missing |
| W-05 | Database and identity bootstrap | Migrations, first tenant/company/user/roles and supported data-source onboarding are idempotent and recover after interruption. | Missing |
| W-06 | Start FE, BE and workers | Installer verifies real browser/API/DB/broker readiness and opens a working authenticated product rather than only starting processes. | Partial |
| W-07 | Windows startup registration | At sign-in the registered task starts Docker and the stack, waits for readiness and preserves configuration/data without visible helper consoles. | Missing |
| W-08 | Install replay and recovery | Repeated or interrupted setup repairs missing work without clobbering users, secrets or durable data. | Missing |
| W-09 | Upgrade and rollback | An upgrade backs up durable state, validates compatibility and can recover the prior version when a required gate fails. | Missing |
| W-10 | Clean-machine and reboot proof | A clean supported Windows VM, subsequent reboot/sign-in and repeated install pass the actual installer end to end. | Missing |
| W-11 | Diagnostics | Failures produce useful redacted logs and clear next actions for disk, network, port, permission or unsupported-host problems. | Missing |
| W-12 | Release delivery and operations | Delivered artifact/version/checksum and supported start/stop/update/recovery instructions are linked to verified release evidence. | Missing |

## G — Completion and integration gates

| ID | Requirement | Required acceptance | Current state |
| --- | --- | --- | --- |
| G-01 | Requirement evidence | Each row has a current implementation and scoped executable evidence; scaffolding, file presence and a narrow pilot cannot prove full completion. | Partial |
| G-02 | Feature PR and CI closure | Each feature issue owns one branch/PR, passes relevant acceptance and CI on its exact head, then merges and passes main CI. | Partial |
| G-03 | External dependencies and final audit | Required external credentials/accounts/business policy are recorded explicitly; the goal stays active until all applicable requirements are proven. | Partial |

## Implementation and evidence map

- P0/P1 baseline: `src/Core.Api`, `src/Agent.Worker`, `src/Platform.Persistence`, `src/Platform.Configuration`, `src/Platform.Observability`, `infra`, `compose*.yaml`, `.github/workflows` and the .NET test projects. Complete bootstrap and failure/recovery integration evidence remains required.
- P2 baseline: provider execution and grounded questions in `src/Agent.Worker`; persistence and policy contracts in `src/Platform.Persistence` and `src/Shared.Contracts`. Broader context, attachments, real charging/billing and evaluations must be verified against the acceptance above.
- P3/P5 baseline: bounded scoped ERP business reads and typed policy/workflow contracts. This evidence cannot prove schema adaptation, accounting posting, tax/invoice, inventory writes, specialist operations or bulk reconciliation.
- P4 baseline: contract/safety frameworks, repository CI, migration validation and operator deployment script. A product-operated coding/release/repair workflow requires its own end-to-end evidence.
- P6 baseline: `apps/web` local AI workspace/BFF and current authorized API endpoints. The local password-grant pilot is not the production OIDC browser flow or a complete customer portal.
- W baseline: no shipped single Windows setup artifact or clean-machine/reboot proof. #234 adds `apps/web/Dockerfile`, runtime Compose wiring and `scripts/smoke-web-container.mjs`; these are prerequisites for setup.

### Accepted progress after the baseline

- Accepted main `002e5cd9c1fd95eabc02dd3580bb8e964409764b` has [Build37801975078](https://github.com/thanhtuyen662002/minh-huy-ai-office/actions/runs/37801975078) and [Governance37801976469](https://github.com/thanhtuyen662002/minh-huy-ai-office/actions/runs/37801976469) PASS, including actual SQL/OIDC/broker/worker/FE/Chromium113396238709. P0-02 and P0-05 are marked Done against their bounded requirements; the other rows retain their broader acceptance.
- P1-01/P6-01: reviewed exact identity, scoped member listing and audited suspend/reactivate are shipped (#254/#256/#262), and provider Code/S256 browser authentication/session lifecycle is shipped (#260). Authoritative company discovery/switching #266/PR267 is delivered on main `a75f31a2761ffe0afcbf067425eb8324789b3554` after full frozen independent review and exact PR/main all8 gates, including [Build37815044494](https://github.com/thanhtuyen662002/minh-huy-ai-office/actions/runs/37815044494), Governance37815044554 and actual SQL/Chromium113441393702. Existing-member administrator role management #268/PR269 has actual SQL/two-session shipping Chromium acceptance and all8 exact gates at `d85be6c6df108c1f4a411889792a4727e41ab107`, Build37873606708/Gov37873606687/actual113636976840. Independent review found and regression-tested the committed Core-to-BFF loss defect; real Core/SQL header/body fault controls now prove503/sameSID/exact one-audit replay. Final frozen review/documentation/PR/main delivery remains. Invitations/custom role policy/approved production identity remain broader acceptance.
- P1-02: reviewed source administration, typed secret bindings, read-only registration and fresh capability authorization are shipped (#240/#246/#248/#250/#252). Real customer source qualification remains separate.
- P3/P5: reviewed #264/#265 conservative standalone SQL Server read credential profile refuses elevation, unsupported dependency graphs and mid-read privilege changes on the actual connection. Its owned SQL proof is recorded in `docs/validation/ERP_READ_CREDENTIAL_PROFILE.md`; it does not accept the elevated private Medcom/Novo connections or prove inventory business answers. Dedicated read credentials, versioned legacy module profiles and reconciliation remain; Santino is deferred.
- W: Draft PR239 contains a self-contained development installer with full frozen723 integration/runtime/docs independent approval, receipt6072831478, exact Build37819060872/Gov37819060881/all9/native113455075455/actual113455075394 PASS and independently checked artifact checksum/rendered previews/browser. Clean supported Windows install/UAC/reboot/resume/sign-in/startup/repair/backup and signing are still required. Missing W rows describe delivered acceptance, not the absence of all source code.
- Current branch eligibility is recorded in `docs/validation/BRANCH_INTEGRATION_AUDIT.md`. Historical merged branches, contract tests and installer artifacts do not substitute for remaining product acceptance.

## Next executable work

1. Close active #268/PR269 final documentation review/current exact gates, merge and exact main CI. Currentd85 full independent frozen security/runtime/images APPROVED receipt6073002478; all8 and actual SQL/two-session Chromium including committed Core-to-BFF header/body loss and stable one-audit replay PASS. Read its live HANDOFF before acting.
2. Continue Draft installer239 only after accepted dependency delivery. Frozen723 full scoped integration/runtime/docs review and all9 exact hosted gates pass; clean Windows lifecycle and signing remain external acceptance before delivery.
3. After269 accepted main closure, acquire queued #270 owner-scoped durable task history/recovery with one Draft lease. Continue missing member administration/customer workflow prerequisites; stable browser task-submission replay remains a separate follow-up. Customer inventory/stock-movement execution requires dedicated approved credentials and qualified schema/version dependencies before business-row proof.
4. Continue all remaining P0-P6 rows through feature issues and actual browser/runtime acceptance. Keep #233 active until every applicable row and completion gate is proven.

## Genuine external evidence boundaries

The repository contains placeholders rather than approved live customer ERP credentials, provider credentials, production OIDC accounts and tax/billing provider approval. Live external integrations require those inputs or permissions. Continue all unblocked code and sandbox/integration work while recording these boundaries; do not substitute fake provider responses for required live proof. Windows installation must be validated on a clean supported Windows host with the required OS privileges and virtualization. Linux container CI alone cannot prove Windows install/reboot behavior.
