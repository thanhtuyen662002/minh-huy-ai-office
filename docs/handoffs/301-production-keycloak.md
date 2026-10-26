# HANDOFF — issue #301

Issue: [#301](https://github.com/thanhtuyen662002/minh-huy-ai-office/issues/301)

Branch: `codex/301-production-keycloak`

HEAD: `ad747d0daf71be73e1e2af17e8bb3c4c0b4b234b` (base; implementation remains in the working tree)

State: Local implementation of Scope 1–4 complete; review and disposable runtime acceptance pending. No push or remote PR mutation.

Completed:

- Separate `compose.identity.yaml` project, pinned Keycloak 26.8.0, production `start --import-realm`, UID 1000, loopback ingress, strict HTTPS proxy hostname, private database readiness, dropped capabilities.
- Fixed MSSQL `AIOffice_Identity` JDBC database and validated TLS; four file-backed database/bootstrap secret references with runtime-only loading. Versioned initial database provisioning script; Keycloak owns its table migrations.
- Parameterized `aioffice` realm with the existing browser public Code/S256 client, basic/profile/email scopes, access-only API audience and `idp=company-keycloak`, truthful email verification, password length/history, brute-force controls, default first-login password update, optional TOTP.
- Production env template, explicit browser issuer wiring with compatible fallback, self-hosted preflight, and 38 added configuration/realm cases (63 total Configuration tests).
- Deployment instructions for SQL provisioning, proxy, initial import, permanent Keycloak operator, first staff/company administrator identity, backups/restore and disposable acceptance procedure.

Tests/Evals:

- `docker compose --env-file <temporary-dummy.env> -f compose.identity.yaml config --quiet`: PASS.
- `docker compose --env-file <temporary-dummy.env> -f compose.yaml -f compose.production.yaml config --quiet`: PASS. Dummy env preflight also PASS.
- `dotnet build --no-restore --nologo`: PASS, 0 errors; 12 NU1900 warnings from sandbox TLS failure fetching NuGet vulnerability metadata. Cached package restore succeeded with `--ignore-failed-sources` and an isolated temporary NuGet config/APPDATA; no dependency files changed.
- `dotnet test --no-build --no-restore --nologo`: PASS, 2,065 total / 0 failed / 0 skipped (Persistence 1489, Contracts 507, Configuration 63, Observability 6).
- `npm ci --ignore-scripts --no-audit --no-fund`, then `npm test -- --reporter=dot` in `apps/web`: PASS, 1,314 tests / 20 existing Redis-gated skips; 70 files passed / 1 skipped.
- Changed C# whitespace check, two Bash syntax checks and `git diff --check`: PASS.

Local verification environment:

- Installed .NET SDK 10.0.402; portable PowerShell 7.5.2 downloaded only to `%TEMP%/aioffice-301-tools/pwsh` and prepended to the test process PATH because `pwsh` was missing.
- Temporary APPDATA and NuGet.Config avoided the sandbox-denied user NuGet.Config. Packages were read from the existing cache. No persistent machine configuration change.
- Initial full tests failed because Windows Event Log writes are denied in this sandbox. Re-run with **process-only** `Logging__EventLog__LogLevel__Default=None` passed all 2,065 tests. No product logging/test code was changed to bypass the restriction.
- Docker Compose validation used temporary dummy configuration and secret files, with temporary `DOCKER_CONFIG`; no `.env.production` read or modification and no real SQL/server connection.

Known risks / remaining:

- Docker daemon is unavailable (`//./pipe/docker_engine` absent), so a real disposable Keycloak + SQL Server import, first-login/password policy/OTP/lockout, retained UUID restart, and backup/restore were **not executed**. `docs/DEPLOYMENT.md` has the manual release-gate procedure, not a claimed passing proof.
- Retained local-stack integration and exact-head CI are unexecuted; no workflows were triggered because the user prohibits pushing.
- `gh issue view 301` and CLI issue/PR lists returned HTTP 401. Read-only connected GitHub app access retrieved #301 and open issues/PRs; no competing #301 PR was found. No GitHub messages sent.
- `git add` and `git commit` failed: `Unable to create 'D:/Projects/AI Office/minh-huy-ai-office/.git/worktrees/wt-301/index.lock': Permission denied`. No commit was made. Do not discard the working tree.
- The realm seed applies only on first import. Existing production realms require reviewed versioned administrative changes, never destructive re-import. The selected image and identity SQL database must be backed up/upgraded together.

NEXT ACTION: Review the local diff. When Git access permits, commit infrastructure/realm and config/tests/docs in small checkpoints. Keep all changes local until pushing is newly authorized. Execute the documented disposable identity proof and retained local-stack CI before production acceptance.

DO NOT: Touch `apps/web/app/page.tsx`, PR #298 invitation files, any `zalo-intake` directory, `.env.production`, production servers/databases, or `docs/PROJECT_STATE.yaml`. Do not implement automatic Keycloak account creation from **Thêm thành viên**; that follow-up is outside #301 Scope 1–4.
