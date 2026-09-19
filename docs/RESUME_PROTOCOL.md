# Resume / Handoff Protocol

## Goal
Any new ChatGPT/Codex/Claude/AI session can continue without access to prior chat history.

## On every resume
1. Read `AGENTS.md` and `docs/PROJECT_STATE.yaml`.
2. Inspect open draft PRs; prefer continuing an active PR over starting new work.
3. Read the linked issue, PR body/comments, latest commits and CI status.
4. Reproduce the current failing/passing state before changing code when practical.
5. Continue the `NEXT ACTION`.

## Checkpoint discipline
Checkpoint after each meaningful verified unit:
- commit implementation,
- push/update branch,
- update draft PR handoff,
- update project state when ownership/next action changes.

## Required PR handoff block
Every active PR should contain:

```
HANDOFF
Issue:
Branch:
HEAD:
State:
Completed:
Current:
Remaining:
Tests/Evals:
Known risks:
Blocked by:
NEXT ACTION:
DO NOT:
```

## Interruption handling
Unexpected quota/session termination must be recoverable from the latest commit + PR + CI. No essential decision may exist only in an ephemeral chat.

## Priority selection after resume
1. SECURITY-CRITICAL open work.
2. Production incident/blocker.
3. Existing active draft PR.
4. Highest roadmap-priority unblocked issue.
5. Create new work only when no executable tracked work remains.

## Stale worker rule
A previous agent's ownership is advisory. If no active checkpoint progress exists, a new worker may take over after reading the state and preserving the branch history.
