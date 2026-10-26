# HANDOFF

Issue: [#307](https://github.com/thanhtuyen662002/minh-huy-ai-office/issues/307)

Branch: `codex/307-keycloak-member-provisioning`

HEAD: `a130bf8420cc6953d149b4852bf617966429ebe4` (merged #294 base; implementation is in the working tree)

State: Local implementation and verification complete; ready for review. No push, PR mutation or service activation. Commits are blocked by Git metadata permissions.

Completed:

- Default-disabled confidential `aioffice-member-provisioner` realm client with only `realm-management/manage-users` in the service-account role assignment and client scope mapping. No realm roles, broad roles scope, unrelated client roles or browser/password flows. Explicit subject, admin audience and filtered client-role mappers preserve Keycloak 26 token authentication without broad default scopes.
- Shared protected secret file mounted only into identity/Core; runtime environment substitution in realm JSON, production env template, preflight checks and deployment instructions. Standalone Core also supports `secretref://env/VARIABLE_NAME` instead of a mounted file.
- Injectable Keycloak admin interface and typed HttpClient using client credentials, HTTPS, disabled redirects/cookies/loggers, bounded responses and timeouts. Exact-email lookup; missing accounts are enabled/email-verified with a cryptographically random 52-character temporary password and `UPDATE_PASSWORD`. Existing and concurrently created accounts are never reset.
- Invitation conflicts and current admin authorization checked before provisioning; final authorization rechecked before the database commit. Password returned only on successful creation, absent from DB/audit/list/revoke and redacted from result `ToString`. Provisioning audit rows contain only fixed outcomes; failures emit fixed company-scoped warning events. Upstream error bodies and inner exceptions never escape.
- Existing `/api/local` BFF passes only a bounded valid creation credential with `no-store`. Vietnamese panel displays it in memory with copy/dismiss controls and a sign-in link. Reload, later actions, authority failure and remount discard it. Delayed clipboard validation cannot copy a dismissed credential.
- Fake/in-memory unit/API tests, intercepted HttpClient tests, realm/scope/config tests and web one-time-display/BFF tests. No schema change or migration required.

Tests/Evals:

- `dotnet build --no-restore`: PASS, zero warnings/errors.
- `dotnet test --no-build --no-restore`: PASS, 2,216 passed / one existing opt-in SQL skip / zero failed (Persistence 1,635; Contracts 507; Configuration 68; Observability 6).
- `npm test` in `apps/web`: PASS, 1,406 passed / 20 existing Redis-gated skips / zero failed; 76 passed files / one skipped file.
- `npm run typecheck`: PASS.
- Both identity and merged production Compose configurations rendered offline using temporary synthetic config: PASS with provisioning `false` and `true` (four configurations). No Docker daemon or real secrets used.
- Changed C# whitespace formatting and `git diff --check`: PASS.

Verification environment:

- Cached NuGet restore used a temporary config containing no remote feeds and a process-only temporary `APPDATA`, because the user NuGet config is sandbox-denied. No dependency manifests changed.
- Existing portable PowerShell under `%TEMP%/aioffice-296-pwsh` was added only to the test process PATH for the repository's preflight tests. Process-only `Logging__EventLog__LogLevel__Default=None` avoids sandbox-denied Windows Event Log writes in child-process tests. No product logging behavior was weakened.
- `AIOFFICE_CUSTOMER_REQUEST_TEST_SQL` was removed only from the test process environment; the existing opt-in real SQL proof remains skipped. No real database/server was contacted.

Known risks / remaining:

- Keycloak and SQL cannot commit atomically. A user can remain after invitation commit/authorization failure or a lost upstream/201 response. No password is returned by a failed invitation or recovered on retry; existing accounts are never reset/deleted as compensation. Verified identity-operator recovery is documented in `DEPLOYMENT.md`. Known invitation conflicts are checked first under the existing transaction lock.
- Startup realm import skips existing realms. Existing #301 deployments need a reviewed realm configuration migration for this client/account/scope/mappers and coordinated secret rotation before enabling Core. No live realm was changed.
- No disposable or live Keycloak/SQL runtime proof was executed, respecting the instruction not to connect to a real database/server. Realm import/token-role enforcement and first-login password change remain deployment acceptance work.
- `gh issue view 307` and issue/PR CLI lists returned HTTP 401. Connected read-only GitHub access retrieved the complete issue. No GitHub messages were sent.
- Local staging and commit both failed with `Unable to create 'D:/Projects/AI Office/minh-huy-ai-office/.git/worktrees/wt-307/index.lock': Permission denied`. No commit or staging was created. No permissions were changed or bypassed. All implementation files remain in this worktree.
- No remote CI runs were triggered because pushing is prohibited. No Draft PR was created.

NEXT ACTION: Review this local diff; once normal Git write access permits, commit infrastructure/config/tests, API/service/tests, then web/docs in small local checkpoints on this branch. Keep everything local until a later explicit authorization to push. Run the disposable Keycloak acceptance proof in an authorized environment before deployment.

DO NOT: Push, touch `apps/web/app/page.tsx`, any `zalo-intake` folder, `.env.production`, `docs/PROJECT_STATE.yaml`, production services/databases or existing passwords. Do not discard the uncommitted implementation.
