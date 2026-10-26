# Customer request tracker UI (#300)

```text
HANDOFF
Issue: #300
Branch: codex/300-customer-request-ui
HEAD: 0219e40b4c0c6848b55a52ed735a09aa49ba7404 (stacked base; implementation is uncommitted)
STACKED_ON: PR #297 / codex/295-customer-request-bridge
State: ready_for_review (local working tree; operator owns commit/publication)
Completed: Vietnamese desktop/mobile workspace navigation using the shell-selected company and session generation; newest-first ticket table, status/customer filters and offset paging; detail fetched by code with AI/unconfirmed labeling, summary, items, open questions, quoted sender/time evidence, free-text desired due and event history; explicit PATCH status/assignee with RowVersion, omitted-vs-null assignee semantics, exact 409 message and reload; loading/empty/error/forbidden states; session fencing, cancellation, duplicate-save protection and no automatic write retries. Reused fetchCoreApi, same-origin checks, bounded JSON reader, issued-session fences, company-member BFF and parseMemberPage. No backend changes.
Current: local verification complete; ready for operator review.
Remaining: operator review, commit, draft PR stacked on #297, and CI. No deployment or live SQL/OIDC/Zalo proof performed in this frontend-only run.
Tests/Evals: npm test PASS (1,365 passed, 20 existing Redis-gated skips, 0 failed; 51 new tests); npm run typecheck PASS; npm run build PASS. All executed from apps/web using npm.cmd because PowerShell blocks npm.ps1. No backend changes, so .NET checks were not required.
Known risks: Existing /api/company/members is administrator-only. A directory 403 disables assignee selection with an explanation, while members can still read tickets and change status. No authorization policy was weakened. The directory is paged through its existing offset ceiling; an incomplete/unavailable directory disables assignment instead of inventing choices. The backend bridge is off by default and requires #297 configuration/migration for live use. List/detail use the existing workspace panel navigation pattern.
Blocked by: none for local implementation; publication and CI await the operator.
NEXT ACTION: Review the uncommitted changes, commit on this branch, and open a draft PR targeting codex/295-customer-request-bridge. Keep STACKED_ON #297; after #297 merges, retarget/rebase and run exact-head CI.
DO NOT: add/commit/push or switch branches during this run; add a hard-coded company ID; change backend authorization or lead-owned PROJECT_STATE; enable ingestion, modify zalo-intake, send anything to Zalo, describe AI text as staff-confirmed, or turn customer desired due text into a commitment.
```

Final verification: all three required web checks passed on 2026-10-26. Build-generated edits to next-env.d.ts and tsconfig.json were removed to keep the diff scoped. The existing workspace-session lifecycle tests also pass. No git add, commit, push or branch switch was performed; no PR or CI run was created.
