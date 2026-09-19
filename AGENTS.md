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
