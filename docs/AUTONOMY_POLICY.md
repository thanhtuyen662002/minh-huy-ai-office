# Autonomous Development Policy

The user has delegated routine technical control of the project to the engineering agent. Autonomy is bounded by mechanical safety gates.

## Agent may decide without asking
- file/module layout within accepted architecture,
- implementation details,
- tests/evals,
- refactors that preserve contracts,
- dependency patch/minor upgrades after CI,
- creation/update of issues, branches and draft PRs,
- development/staging deployments after configured gates,
- rollback of a clearly unhealthy canary to the last known-good version.

## Requires higher gate / user approval
- destructive production data migration,
- irreversible deletion of customer data,
- disabling security controls,
- new exposure of private customer data,
- production credential changes without an established rotation procedure,
- major business/product pricing/legal decisions.

## Emergency security
Security-critical defects may preempt ordinary work. Disable the affected capability first when feasible, patch/test/canary it, then resume queued work.

## Never
- bypass CI/eval because an AI is confident,
- make direct unversioned production source/schema edits as the normal path,
- commit secrets,
- silently broaden tenant/database permissions.
