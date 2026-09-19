# Minh Huy AI Office

Production-oriented multi-tenant AI Office for Minh Huy's ERP, accounting, customer support and operational workflows.

The repository is intentionally designed so development can continue across different ChatGPT/Codex/AI sessions without relying on chat history.

## Start here

1. Read `AGENTS.md`.
2. Read `docs/architecture/FOUNDATION.md`.
3. Read `docs/ROADMAP.md`.
4. Read `docs/RESUME_PROTOCOL.md`.
5. Find the highest-priority open GitHub issue that is not blocked.
6. Continue its linked branch / draft PR, or create them if they do not exist.

## Source of truth

- GitHub Issues: development backlog and acceptance criteria.
- Pull Requests: implementation checkpoint and handoff state.
- Git commits: verified incremental checkpoints.
- CI: machine-verified state.
- Repository docs/ADRs: architecture and policy decisions.
- Runtime tasks: later stored in the AI Office SQL Server database.

## Product principles

- Task state belongs to the platform, not an LLM context window.
- Agents depend on capabilities, not specific models, vendors, tables, forms or databases.
- Tenant/company/user/database context is resolved dynamically and isolated.
- Schema, source, workflows, skills, policies and releases are versioned.
- Updates are backward-compatible by default and support drain/canary/rollback.
- Self-improvement means propose -> test -> evaluate -> release, never arbitrary production mutation.
- A failed model/session/worker must not lose project or runtime state.
