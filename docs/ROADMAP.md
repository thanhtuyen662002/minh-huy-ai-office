# Roadmap

## Immediate owner-prioritized delivery — autonomous customer-group intake

Corrected by owner 2026-10-10: [customer source groups → connector → agent → SQL work notes → agent → INTERNAL TECHNICAL GROUP](features/customer-group-intake/OWNER_CORRECTION_AUTO_IT.md). Epic #275; #276–#279 keep IDs with revised acceptance. **No runtime human approval, no manual Generate/Accept/Send prerequisite and no report back to customer source groups.** Earlier PR #280 wording is superseded.

Build durable intake, automatically recorded evidence-backed notes, and a DB-driven IT notifier. Missing information produces an attention note automatically sent to IT, not an approval queue. Backend maps source bindings to explicitly authorized internal destinations; every item retains source identity. IT handles the work afterward. Conversational customer replies remain later scope.

Retain security-critical work and existing recovery/installer leases (#274/#239 if still active). Sequence shared contracts/migrations through Lead; do not steal ownership or cancel #233. Full wiki/vector/RAG, ERP business adapters, billing and Windows completion are not prerequisites. Initial source/target access configuration and code review/CI still apply; no live account or deployment is activated by this documentation correction.

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
