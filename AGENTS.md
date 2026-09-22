# AGENTS.md — Minh Huy AI Office

This file is the operating contract for every coding/reasoning agent working in this repository.

## Prime directive
Continue the project from repository state. Do not depend on prior chat context.

## Resume sequence
1. Read `README.md`.
2. Read `docs/PROJECT_STATE.yaml`.
3. Read `docs/architecture/FOUNDATION.md`.
4. Read `docs/ROADMAP.md` and `docs/RESUME_PROTOCOL.md`.
5. If multiple coding chats/workers are active, read `docs/PARALLEL_EXECUTION.md` and your `docs/workstreams/<id>.yaml`.
6. Inspect open issues and open draft PRs.
7. Continue the highest-priority unblocked issue allowed by your workstream.
8. Before stopping, update the PR handoff and your durable state.

## Work rules
- One implementation issue = one branch + one draft PR unless there is a strong reason otherwise.
- Use small verified commits. Never keep hours of uncommitted work.
- Every material behavior change needs tests or an explicit reason why tests are not yet possible.
- Database schema changes must be versioned migrations.
- Backward-compatible expand/migrate/contract is the default for DB and API evolution.
- Never edit production manually as the primary delivery mechanism.
- Never store plaintext credentials, tokens or customer secrets in Git.
- Tenant isolation, authorization and auditability are non-negotiable.
- Agent/model/provider/session are replaceable workers; durable state belongs to GitHub and the platform.
- Never duplicate an issue already leased by another open Draft PR.
- Do not bypass dependencies merely to keep a worker busy.

## Parallel ownership
When `docs/PARALLEL_EXECUTION.md` is present:
- `docs/PROJECT_STATE.yaml` is lead/integrator owned.
- Specialists update only their own `docs/workstreams/<id>.yaml` plus the PR handoff for their work.
- An open Draft PR linked to an issue is the authoritative work lease for that issue.
- Cross-stream contract/migration races must be stacked or sequenced, not independently reinvented.
- Lead owns normal integration/merge sequencing.

## Autonomy
Agents may make routine technical decisions consistent with repository architecture without asking the user. Escalate only for:
- irreversible/destructive production operations,
- missing external credentials/account access,
- a product/business decision not covered by existing policy,
- legal/compliance constraints,
- a conflict between accepted architecture decisions.

## Definition of done
- Acceptance criteria met.
- Relevant tests/evals pass.
- Security/tenant boundaries preserved.
- Docs/ADR updated when architecture changes.
- PR contains a current HANDOFF block.
- Durable project/workstream state reflects the next executable action.

## CI closure ownership
A pushed implementation checkpoint is not the end of a coding run. The specialist that owns the branch/PR also owns the exact-head CI closure loop.

After every meaningful code/test push:
1. Capture the exact HEAD and associated workflow/run IDs when available.
2. While the current execution still has budget, keep the run alive through CI. Re-check exact-head status after doing other safe same-PR work; do not voluntarily stop just because CI is pending.
3. If CI reaches a real terminal failure, inspect the failing job/log immediately, fix the defect on the SAME branch/PR, run focused local verification, push a new HEAD, and repeat the loop.
4. If CI fails before executing steps because of runner/infrastructure/capacity noise, retry once. If the same zero-step condition repeats, record the evidence and continue other safe executable work without retry spam.
5. Green CI is not completion when issue acceptance work remains; continue implementation.

`waiting_ci` means an active owned lease, not finished work. A worker may stop in `waiting_ci` only when the execution/session limit is actually being reached, CI remains non-terminal after useful same-PR work is exhausted, or a genuine external/dependency blocker prevents further safe progress. Before stopping, persist exact HEAD, workflow/run IDs/status, local verification, and the precise next action in HANDOFF/workstream state.

Lead/Watchdog must immediately re-enter the owning specialist when exact-head CI becomes terminal red after that specialist's run ended, instead of waiting for the next normal hourly schedule. Push is a checkpoint; CI closure is part of the unit of work.

