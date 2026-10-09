# Roadmap

## Immediate owner-prioritized delivery — customer group intake

Owner direction 2026-10-09: ship [Zalo customer groups → IT task notes → one reviewed same-group digest](features/customer-group-intake/README.md) before conversational auto-replies. Epic #275; slices #276–#279; detailed flow/contracts/acceptance live in that feature folder.

Deliver read-only group intake first, then evidence-backed request triage/IT notes, then a single reviewed consolidated report routed by backend to the originating group. Capture permitted future group text, not only mentions. Keep customer-safe report separate from internal notes. Full wiki/RAG, ERP business adapters, billing, installer completion and auto-replies are not prerequisites for this vertical slice.

Retain security-critical/incident work and current active recovery leases (#274/#239 at planning time); sequence shared contracts/migrations through lead review instead of stealing ownership. This reorders the next available product-capacity slice, not a cancellation or false completion of #233. No live account/automation is enabled by the plan.

## P0 — Engineering foundation
Goal: make development resumable, testable and deployable.
- Repository governance and ADRs.
- Monorepo scaffold.
- Local Docker dependencies.
- .NET API + worker skeleton.
- Next.js/shadcn web skeleton.
- SQL Server migration strategy.
- CI gates, test harness, observability baseline.
- Environment/config/secrets contract.

## P1 — Platform core
Goal: internal AI Office with durable work.
- Identity/company/membership/roles.
- Data Source Registry and connection testing.
- Task/step/dependency/lease/checkpoint engine.
- RabbitMQ worker execution.
- Permission/policy engine.
- Audit/tool execution log.
- SignalR task status.
- Basic Manager + IT/ERP agent roles.

## P2 — AI/context/model layer
- AI Gateway and provider adapters.
- Model Capability Registry and routing/fallback.
- Context Engine and task memory.
- Conversation/attachment ingestion.
- Usage/cost ledger.
- Eval/replay framework.

## P3 — ERP adaptability
- Schema Observer and schema snapshots/diffs.
- ERP Catalog: DB objects, forms, reports, capabilities.
- Feature Registry and compatibility contracts.
- Source diff indexing.
- Change-impact analysis.
- Versioned skills/workflows/adapters.

## P4 — Controlled self-improvement
- IT Agent coding loop: issue -> branch -> PR -> CI -> eval.
- Migration generation + shadow DB tests.
- Release Catalog.
- Canary/blue-green/drain/rollback.
- Emergency capability kill switch.
- Dependency/model upgrade candidates.
- Self-healing versus self-improvement policies.

## P5 — Accounting/operations automation
- Accounting investigation workflows.
- Posting preview/execute/reconcile.
- Tax/invoice integrations.
- Order/inventory workflows.
- Exception queue and specialist escalation.
- Bulk deterministic execution with AI on planning/exceptions.

## P6 — Customer-facing multi-tenant product
- Customer identity/role enforcement.
- Customer portal/chat.
- Plans/AI credits/billing.
- SLA/priority controls.
- Customer-level audit/reporting.
- Hardening, HA, DR and multi-server scale.

## Release principle
Each phase is delivered in thin vertical slices; no phase waits for every subsystem to be perfect before producing a testable end-to-end capability.
