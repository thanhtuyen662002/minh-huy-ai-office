# ADR-0004: Versioned Safe Change

Status: Accepted

Source, schema, skills, workflows, policies and releases are versioned. Long-running tasks are version-pinned. DB/API changes default to expand/migrate/contract, with canary/drain/rollback for runtime upgrades.
