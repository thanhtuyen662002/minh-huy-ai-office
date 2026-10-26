# Customer request tracker UI (#300)

```text
HANDOFF
Issue: #300
Branch: codex/300-customer-request-ui
HEAD: see PR (branch rebased onto main after #297 merged as 542221c)
STACKED_ON: none; #297 merged into main, PR targets main
State: waiting_ci (draft PR to main)
Completed: Vietnamese desktop/mobile workspace navigation using the shell-selected company and session generation; newest-first ticket table, status/customer filters and offset paging; detail fetched by code with AI/unconfirmed labeling, summary, items, open questions, quoted sender/time evidence, free-text desired due and event history; explicit PATCH status/assignee with RowVersion, omitted-vs-null assignee semantics, exact 409 message and reload; loading/empty/error/forbidden states; session fencing, cancellation, duplicate-save protection and no automatic write retries. Reused fetchCoreApi, same-origin checks, bounded JSON reader, issued-session fences, company-member BFF and parseMemberPage. No backend changes.
Current: committed, pushed, draft PR open; awaiting CI and review.
Remaining: CI and review. No deployment or live SQL/OIDC/Zalo proof performed in this frontend-only run.
Tests/Evals: npm test PASS (1,365 passed, 20 existing Redis-gated skips, 0 failed; 51 new tests); npm run typecheck PASS; npm run build PASS. All executed from apps/web using npm.cmd because PowerShell blocks npm.ps1. No backend changes, so .NET checks were not required.
Known risks: Existing /api/company/members is administrator-only. A directory 403 disables assignee selection with an explanation, while members can still read tickets and change status. No authorization policy was weakened. The directory is paged through its existing offset ceiling; an incomplete/unavailable directory disables assignment instead of inventing choices. The backend bridge is off by default and requires #297 configuration/migration for live use. List/detail use the existing workspace panel navigation pattern.
Blocked by: none.
NEXT ACTION: Watch exact-head CI on the PR; fix any red on this branch. Follow-up decision: whether non-admin members may list company members for assignment.
DO NOT: add a hard-coded company ID; change backend authorization or lead-owned PROJECT_STATE; enable ingestion, modify zalo-intake, send anything to Zalo, describe AI text as staff-confirmed, or turn customer desired due text into a commitment.
```

Final verification: all three required web checks passed on 2026-10-26. Build-generated edits to next-env.d.ts and tsconfig.json were removed to keep the diff scoped. The existing workspace-session lifecycle tests also pass. Committed and published by the operator after local re-verification (same results).
