# AGENTS.md — Minh Huy AI Office

This file is the operating contract for every coding/reasoning agent working in this repository.

## Prime directive
Continue the project from repository state. Do not depend on prior chat context.

## Resume sequence
1. Read `README.md`.
2. Read `docs/PROJECT_STATE.yaml`.
3. Read `docs/architecture/FOUNDATION.md`.
4. Read `docs/ROADMAP.md` and `docs/RESUME_PROTOCOL.md`.
5. Inspect open issues and open draft PRs.
6. Continue the highest-priority unblocked issue.
7. Before stopping, update the PR handoff and `docs/PROJECT_STATE.yaml`.

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
- `docs/PROJECT_STATE.yaml` reflects the next executable action.
