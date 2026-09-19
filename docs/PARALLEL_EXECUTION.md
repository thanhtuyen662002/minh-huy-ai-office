# Parallel Execution Protocol

## Objective
Maximize verified throughput across multiple autonomous coding chats without duplicating work or causing unsafe merge races.

The goal is not to keep every worker busy. The goal is to maximize **ready, mergeable, verified work**.

## Workstreams

| ID | Responsibility | Owns |
|---|---|---|
| lead | Lead / Integrator | roadmap priority, integration, PROJECT_STATE, merge sequencing, cross-stream conflicts |
| backend | Backend Platform | ASP.NET Core domain/application/API, identity, policies, persistence boundaries |
| frontend | Frontend Product | Next.js, shadcn/ui, UX, client contracts, realtime UI |
| runtime-ai | Runtime / AI Platform | task engine, workers, queues, AI Gateway, model/context runtime |
| erp-data | ERP / Data | SQL/ERP adapters, Data Source Registry, schema/catalog/evolution |
| qa-release | QA / Security / Release | CI, tests, security checks, migrations verification, observability, deployment/release review |

## Source-of-truth ownership

- `docs/PROJECT_STATE.yaml` is **lead-owned**. Specialist branches must not edit it.
- Each specialist owns only its file under `docs/workstreams/<id>.yaml`.
- Every implementation still has a GitHub Issue + branch + Draft PR.
- An open Draft PR is the authoritative work lease for its linked issue.
- CI and merged Git history are stronger evidence than chat summaries.

## Before starting work

Every worker must:
1. Read AGENTS.md, FOUNDATION, ROADMAP, RESUME_PROTOCOL and this document.
2. Inspect all open PRs and the target issue.
3. Confirm no existing open PR already claims the issue.
4. Confirm dependencies needed for the proposed slice exist on the PR base, or explicitly use a stacked-PR base.
5. Record the issue/branch/PR in its workstream state file.

Do not start the same issue from two workstreams.

## Dynamic parallelism

Do not force six active implementation branches when the dependency graph exposes fewer than six safe slices.

A worker with no safe executable work should:
- review/test an active PR in its scope,
- improve coverage/evals/security evidence,
- prepare a narrowly scoped prerequisite issue,
- or remain idle.

It must not bypass dependencies merely to appear busy.

## Branch ownership

Preferred branch naming:
- `lead/<issue>-...`
- `backend/<issue>-...`
- `frontend/<issue>-...`
- `runtime/<issue>-...`
- `erp/<issue>-...`
- `qa/<issue>-...`

A worker should normally modify files in its own responsibility. Cross-stream edits are allowed only when necessary for the issue and must be called out in the PR handoff.

## Stacked work

When parallel speed requires code that depends on an unmerged prerequisite:
- branch from the prerequisite branch,
- open a Draft PR targeting that prerequisite branch,
- clearly declare `STACKED_ON: <PR/branch>`,
- do not merge the child before the parent,
- rebase/retarget to main after the parent merges.

Prefer independent PRs when possible.

## Merge authority

The lead workstream owns normal merge sequencing.

Specialists may make a PR ready and update its handoff, but must not merge a PR if:
- it conflicts with another active workstream,
- its prerequisite PR is unmerged,
- required CI/evals are incomplete,
- the change is security-sensitive and required review evidence is absent.

Low-risk isolated changes may be merged by a specialist only when AGENTS/AUTONOMY policy already permits it and no integration sequencing decision is involved.

## Conflict resolution

If two branches touch the same contract or migration:
1. stop the newer dependent implementation,
2. identify the authoritative prerequisite,
3. stack/rebase the dependent branch on it,
4. let lead record the integration order.

Never solve a contract race by independently inventing two incompatible versions.

## Checkpoint

Each worker must leave:
- small verified commits,
- a current PR HANDOFF,
- its own workstream YAML updated,
- exact NEXT ACTION,
- CI/test evidence.

A replacement chat/session should be able to continue without conversation history.
