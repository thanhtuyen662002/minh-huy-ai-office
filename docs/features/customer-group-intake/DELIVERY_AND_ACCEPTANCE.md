# Delivery v2 — tự động, báo IT, không duyệt từng việc

Epic #275 / #276–#279 giữ ID. [Owner correction](OWNER_CORRECTION_AUTO_IT.md) supersedes manual-reviewed same-group plan from PR #280. [Prompt](CODEX_PROMPT.md) is the code implementation brief.

1. #276: connector capability, source/destination IDs, route disclosure grants and service authority. Missing live credentials do not block reviewed mock contracts; no account readiness claim from examples.
2. #277: SQL ingest/revisions/outbox, authorized source-only filter, coverage and recovery inbox. Preserve one fenced listener and no-loss limits.
3. #278: automatic event batching, bounded brain/context integration, extraction validation, auto-commit notes/attention and NotesCommitted event. No manual acceptance or approval.
4. #279: SQL-driven report stage, per-source internal audience grants, automatic IT-only dispatch, idempotency/reconciliation and pilot. No report to customer group.

Full wiki/RAG/ERP/installer/billing and future customer auto-replies are not MVP dependencies. Preserve current recovery/security/installer leases; roadmap change does not cancel #233. Phase U0 sees data; U1 automatically records IT notes; U2 automatically notifies IT after DB commit. A manual-only demo does not satisfy U1/U2.

## No-click acceptance

After a one-time valid source/IT-route setup and feature enable, no operator Generate/Accept/Approve/Send action may occur in the E2E test. New messages close a batch automatically, notes persist, notification job reads DB and sends exactly one logical report for that dispatch window to the IT destination. Customer-group outbound count must be zero. Each report item identifies its source customer/group and persisted note code.

The golden fixture has two distinct customer groups with the same display name, one technical group, three auto-created notes (including a clarification) and one automatic report. QA scenario records are specifications, not tests already executed. Run actual SQL/RabbitMQ/restarts and shipped DI/API/worker paths, not only in-memory unit tests.

Test model failure/unknown facts creates an attention note automatically notified; no invented deadline/IT assignee/resolution. DB rollback cannot publish NotesCommitted or a success report. Missing route/revoked grant never falls back to customer; gap/media limits are visible rather than silently discarded. Same-name groups, source grants, late commits, continuous input, two senders, deletion/restore, echo loops and unknown send require negative and restored-positive evidence.

Existing targets remain proposals: 100 labeled synthetic windows, 30 independently evaluated windows, request recall ≥95%, precision ≥90%, zero unresolved critical privacy/routing/false-commitment failures. QA evaluation before release is not runtime human approval. Report exact counts, skips, environment and model/config versions; no pass from mocks for live connector.

Pilot: owner-authorized customer source groups and a configured internal IT destination, observable automatic batches/notes/reports without per-item approval. Observe at least ten batches across three working days as an initial target, including restart and bounded failures. This plan change does not log in, enable live traffic, send to any real group or deploy code.

Routine decisions are autonomous under repo AGENTS; actual account grants, data-processing rules and secure source/destination configuration are prerequisites for live operation, not a human gate inside each workflow. Group IDs/routes and real credentials are still to be configured; do not invent them or put them in Git.

## Validation and handoff

`python docs/features/customer-group-intake/validate_plan.py --self-test` checks planning fixtures only. Implementation issues require unit/contract, real SQL/queue, model evaluation, UI observation and controlled connector evidence as relevant. Keep HANDOFF/lease/exact-head CI ownership. No automatic code merge because product runtime is automatic.
