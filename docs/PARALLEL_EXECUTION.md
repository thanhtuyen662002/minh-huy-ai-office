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


## Concurrency and long-running execution safety

Treat GitHub workstream state plus the active Draft PR as a distributed lease. A scheduled execution may overlap another workstream or the next schedule of the same workstream.

Before every start/resume:
1. Read the worker's `docs/workstreams/<id>.yaml`.
2. Verify `current_issue`, `current_pr`, `current_branch`, and the actual branch HEAD.
3. Inspect all open PRs to confirm the issue is not claimed elsewhere.
4. If the workstream already has an active PR, resume that PR; do not claim another issue or create another primary branch/PR.
5. Running CI does not release the lease. Continue only independent safe work on the same implementation.
6. A recent state checkpoint plus active PR/CI is evidence that the lease remains live.

Each workstream state should maintain at minimum:

```yaml
workstream:
current_issue:
current_pr:
current_branch:
head:
state:
lease_owner:
lease_heartbeat_at:
last_checkpoint_at:
next_action:
```

Valid `state` values are:
`idle`, `claimed`, `in_progress`, `waiting_ci`, `ready_for_review`, `blocked`, `done`.

### Lease and heartbeat

When a workstream claims an issue, set `state` to `claimed` or `in_progress`, record the issue/PR/branch, set `lease_owner`, and checkpoint the current HEAD.

Refresh `lease_heartbeat_at` only when there is a legitimate checkpoint or verified progress. Do not create empty clock-only commits.

Prefer the cycle:

```text
code -> test/verify -> commit -> update HANDOFF/state -> continue
```

Keep checkpoints small enough to understand, revert, cherry-pick, or resume.

### Re-entry protection

A workstream has one primary implementation lease by default. If a later scheduled run sees the existing issue/branch/PR still active, it must resume that same work or perform non-competing review/testing. It must not create a second implementation for the same workstream.

Additional PRs are allowed only when explicitly stacked, review/test-only, and non-competing, or when the dependency graph clearly permits them.

### Stale lease recovery

Do not preserve a lease forever after a dead session. A new execution may take over the **same** issue/branch/PR when there is no new Git/PR/CI progress, the workstream checkpoint is stale, and there is no evidence another execution is still active.

Takeover path:

```text
existing issue -> existing branch -> existing Draft PR -> latest HEAD -> reproduce state -> continue NEXT ACTION
```

Create a replacement branch/PR only when the existing branch cannot be recovered, and document why on the issue/PR.

### Cross-workstream overlap

Different workstreams may run concurrently. Before changing a shared contract, schema, migration, shared package, or CI/release contract, inspect active PRs and identify the authoritative owner. Later work must stack/rebase on the prerequisite rather than invent a competing contract.

### Lead behavior with active workers

Lead must distinguish `in_progress`, `waiting_ci`, `ready_for_review`, and `blocked`.

Lead must not treat an actively progressing specialist PR as failed or take over its implementation. Lead should review, merge ready work, resolve dependency/base conflicts, update `PROJECT_STATE`, and unblock the next worker cycle.

Merge only when:
- the HANDOFF says the PR is ready,
- acceptance criteria are satisfied,
- required CI/evals are green on the exact HEAD,
- dependency merge order is correct.

## CI closure loop

A specialist owns not only implementation but also the exact-head CI result produced by its checkpoint. Do not treat `push -> waiting_ci -> stop` as the normal success path.

Preferred closure cycle:

```text
code -> local test/verify -> commit/push -> capture exact HEAD
     -> observe exact-head CI
        -> pending: do other safe same-PR work, then re-check
        -> real red: inspect logs -> fix SAME branch -> verify -> push -> repeat
        -> infrastructure/zero-step red: retry once -> classify if repeated
        -> green + acceptance remains: continue implementation
        -> green + acceptance complete: ready_for_review
```

Rules:
- Keep the current automation execution alive through CI while execution budget remains; do not voluntarily terminate immediately after a push when CI is expected to settle within the same run.
- Never busy-spin. Use pending time for source review, focused tests, documentation that belongs to the implementation, or the next safe unit on the same PR, then re-query CI.
- A real failing job with actionable logs belongs to the specialist that owns the PR. Fix it immediately on the same lease rather than deferring to the next hourly schedule.
- A zero-step/runner-assignment/infrastructure failure may be retried once. Repeated identical infrastructure failure is evidence, not a reason for retry storms.
- `waiting_ci` retains the lease and is allowed only when the current execution/session limit is being reached, exact-head CI remains non-terminal after useful same-PR work is exhausted, or another genuine blocker exists.
- Before entering `waiting_ci`, HANDOFF/workstream state must include exact HEAD, workflow/run IDs and status, locally verified evidence, and exact NEXT ACTION.
- Lead/Watchdog must inspect active `waiting_ci` leases. If their exact-head CI becomes terminal red after the specialist execution ended, Lead requests an immediate run of that specialist automation. Do not wait for its next scheduled hour.
- If exact-head CI is green and acceptance is complete, Lead should review/merge promptly and immediately unblock dependent work.
- Required exact-head merge/release gates are unchanged; this protocol reduces latency, not assurance.

