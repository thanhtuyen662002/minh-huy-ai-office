# HANDOFF — issue #310

Issue: #310, one-click Windows VPS installer.
Branch: `codex/310-one-click-installer`.
STACKED_ON: PR #309 / base `7694ab5a0074f798df07cca109093e4883da51b1`.
HEAD: branch tip of the Draft PR (implementation committed on top of the PR #309 base).
State: local implementation complete and committed; analyzer and Pester pass locally; hosted CI and clean-VM acceptance pending.

Completed:

- Root CMD entry with UAC, conditional PowerShell/Git installation and standalone clone path.
- Vietnamese WinForms wizard with a background worker, masked credentials, per-phase progress, fixed fix-it messages and final links to existing product/account flows.
- Preparation/reboot before credential collection, RunOnce recovery tied to a changed boot timestamp, ordered atomic phase-only state, installation lock and idempotent retries.
- WSL2/hello-world, TCP plus actual TLS SQL login for both DBs, DNS/public IPv4 and external two-node port-80 gate before certificates.
- Reuses existing prerequisites/secrets/firewall/IIS/migrations/deployment/bootstrap scripts. IIS delegates to Windows PowerShell for the WebAdministration provider. Optional signed Microsoft Autologon GUI, Docker sign-in startup and desktop open/update shortcuts.
- Secrets stay in protected files or in-process input/private child environments; diagnostic streams are discarded at the phase boundary. Redis now uses a secret configuration mount instead of password argv, with an additive PR309 upgrade helper retaining its password. Env quoting is shared with migration/bootstrap/production validation; migration restores its original process environment.
- Windows CI job enforces PSScriptAnalyzer and parsing for all eight VPS scripts, parses three shared scripts, runs pinned Pester and setup/update dry runs; the job is required by the existing quality aggregator.
- One-click-first deployment runbook with manual fallback and explicit external dependencies, recovery, secret handling and VM qualification limits.

Tests/Evals:

- Offline Pester 3.4.0 on the available portable PowerShell 7: **24 passed, 0 failed, 0 skipped**. Same suite uses Pester 3/4 assertion syntax; hosted runner pins Pester 4.10.1.
- Includes state prefix/refusal/retry/reboot tests, env encoding edge cases and SQL validation, retained HTTPS/host/file production-validator refusals, metadata allowlist, required secret/AI validation, PR309 Redis upgrade, SQL argv exclusion and sentinel-secret scans of logs/state/returned exceptions.
- All eight `infra/windows-vps/*.ps1` plus three shared scripts parse. CMD's embedded PowerShell is parsed in Pester without executing the entry point.
- `Setup-Wizard.ps1 -WhatIf`: 13 planned setup phases. `-WhatIf -Update`: 4 planned update phases. Tests use absent roots/repos and verify no directory is created; action/callback traps remain uncalled.
- `git diff --check`: passed.
- `tests/windows-vps/Invoke-Checks.ps1` with pinned Pester 4.10.1 + PSScriptAnalyzer 1.24.0 (run outside the Codex sandbox): **8 VPS scripts parsed and analyzed, 24 Pester tests passed**. The first analyzer run found 9 warnings; fixed by renaming `Write-SetupOptions`/`Wait-SetupHttps` to singular nouns, not shadowing `$sender`/`$eventArgs` in WinForms handlers, and excluding `PSReviewUnusedParameter` (false positives for parameters used only in nested functions, closures and runspace scriptblocks).

Current: branch pushed and opened as a Draft PR against `main`, stacked on PR #309. Hosted CI results live on that PR.

Known risks / remaining acceptance:

- Real GUI, UAC, reboot/RunOnce, Docker/WSL/IIS/firewall/registry, SQL/migration/Keycloak, certificates, shortcuts and Autologon lifecycle are **not executed or qualified** here. The user expressly prohibits those operations on this machine. Operator qualification belongs on an approved VM.
- Automatic sign-in requires the operator to enter the Windows password and click Enable in Microsoft's verified Autologon dialog. The setup code never receives that password.
- External check-host.net availability is an installation dependency; failure stops certificate provisioning. SQL databases/logins/certificates, nested virtualization, DNS and router forwarding require their administrators.
- The legacy pilot ERP environment requirement is satisfied by a randomly credentialed, unused loopback port-1 endpoint, with no ERP grants; actual ERP configuration remains in the application. Production validators and Compose ingress boundaries are retained.

Blocked by: PR #309 merge (this branch is stacked on it); approved-VM qualification by the operator.

NEXT ACTION: confirm hosted CI on the Draft PR, merge PR #309 first, then have the operator run `Setup-AIOffice.cmd` on the approved Windows 11 Pro VM and record the lifecycle result. Do not claim issue/full-product completion from pure tests alone.

DO NOT: run real installer/wizard/CMD/bootstrap/migrations or machine/DB actions
on this development machine; read real secret files; log configuration; edit
lead-owned PROJECT_STATE or overwrite another workstream's lease.
