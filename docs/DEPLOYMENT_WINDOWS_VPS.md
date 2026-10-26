# Single Windows Host Deployment (IIS + Docker Desktop)

This runbook deploys the whole internal stack on one Windows 11 Pro / Windows
Server VM. IIS is the only public entry point and terminates HTTPS; every
container publishes on `127.0.0.1` only. SQL Server stays on its own machine.
It complements [DEPLOYMENT.md](DEPLOYMENT.md), which remains the contract for
migrations, identity bootstrap and pilot gates.

```text
Internet ──443──> router (forward TCP 80/443 to the VM)
                    │
                    v
            IIS on the VM (Let's Encrypt via win-acme)
              <web host>       -> 127.0.0.1:3000  web
              <api host>       -> 127.0.0.1:8080  core-api
              <identity host>  -> 127.0.0.1:8081  keycloak (/admin LAN only)
                    │
            Docker Desktop (WSL2): web, core-api, agent-worker, rabbitmq,
            redis, session-redis (TLS), otel-collector, jaeger, keycloak
                    │ private network
                    v
            SQL Server: AI_Office (platform), AIOffice_Identity (Keycloak)
```

## One click: Setup-AIOffice.cmd

On the **target Windows 11 Pro VM**, double-click the repository-root
[`Setup-AIOffice.cmd`](../Setup-AIOffice.cmd) and accept UAC. A standalone copy
of this file also works: it installs Git/PowerShell 7 through winget when
needed and clones this repository to `C:\AIOffice\app`. The selected release
must contain `infra/windows-vps/Setup-Wizard.ps1`; while #310 is stacked on
#309, use the complete reviewed checkout rather than a standalone file that
would clone an older `main`. App Installer (winget) and Internet access are
required. Keep using the same local administrator/operator account, including
after reboot; do not elevate with a different person's credentials.

1. Click **Chuẩn bị máy và khởi động lại**. Save other work first. The wizard
   calls the existing `Install-Prerequisites.ps1`, registers an administrator
   RunOnce entry and reboots. It collects **no credentials before reboot**.
   After signing in, accept UAC again: the wizard verifies a new Windows boot
   before completing the reboot phase. If RunOnce is blocked by policy, run
   the same entry file again. Cancelling UAC does not lose completed phases.
2. Enter the Vietnamese form: web/API/login hosts (defaults `ai.bms79.com`,
   `api.bms79.com`, `login.bms79.com`), LAN prefix (detected from IPv4),
   Let's Encrypt email, the masked `AI_Office` SQL connection, Keycloak SQL
   host/port plus masked login/password, optional AI HTTPS URL/model/API key,
   and company code/name/first-administrator email. Supply all three AI fields
   together, or leave all three empty. Use an explicit SQL login and trusted
   TLS, for example the **shape** `Server=sql.office,1433;Database=AI_Office;User ID=...;Password=...;Encrypt=true;TrustServerCertificate=false`.
   The installer creates neither database nor SQL login: the SQL administrator
   must provision them and the trusted certificate first.
3. Click **Cài đặt / tiếp tục**. WSL2 and `docker run --rm hello-world` must
   actually work. TCP and a real SQL login are tested for **both databases**,
   including certificate validation for `AIOffice_Identity`. The wizard reuses
   `New-ProductionSecrets.ps1`, firewall/IIS scripts, the versioned migration
   script, `Update-AIOffice.ps1` and `infra/bootstrap-company.ps1`.
4. Watch the progress list. DNS is checked against IPv4 from
   `https://api.ipify.org`; all addresses must match (remove incorrect AAAA
   records). Before requesting certificates, **two external check-host.net
   nodes** must connect to port 80 for each host and report that public IP.
   This discloses only the public host/IP to the probe service. A local NAT
   loopback connection does not satisfy this gate. Service outage or blocked
   probes stop this phase with instructions; there is no silent bypass.
5. After HTTPS health checks and company bootstrap, browser OIDC is enabled
   and the application redeployed and checked again. **Mở AI Office** and
   **Cập nhật AI Office** appear on the operator's Desktop. Update opens the
   same progress window, calls `Update-AIOffice.ps1 -Migrate` in its migration,
   Keycloak and deployment phases, and checks HTTPS. Back up SQL before an
   update and schedule its expected interruption.

The final links open the existing bot/ERP/member settings in the application
and the Keycloak administration console (LAN only). Bootstrap records the
company's administrator invitation; create/use that email's Keycloak identity
through the existing account workflow, then sign in and accept the invitation.
The installer does not duplicate #303/#306/#298/#308 or create a Windows/SQL
administrator account for the application.

**Docker after Windows Update:** the wizard registers Docker Desktop to start
when this operator signs in. The optional **Autologon** checkbox opens the
downloaded, signature-verified Microsoft Sysinternals dialog at the end; enter
the Windows credentials **in that dialog** and click Enable. No Windows
password is passed through command arguments or written by these scripts.
Autologon stores the password as an LSA secret; administrators can recover it,
and console access can use the signed-in session. See
[Microsoft Autologon](https://learn.microsoft.com/sysinternals/downloads/autologon).
Leave it off and sign in manually after each reboot if this trade-off is
unacceptable; Docker Desktop cannot start containers without its user session.

The installer can diagnose but cannot configure the VM host's nested
virtualization, public DNS, router forwarding, or SQL's trusted TLS certificate.
Its virtualization guidance is: **Nhờ quản trị máy chủ bật nested virtualization / CPU type host**.
The external TCP probe follows the [Check-Host API](https://check-host.net/about/api).

### Recovery and secrets

Phase order is fixed:
`prerequisites → reboot → docker-ready → secrets → firewall → iis → certificates → migrate → keycloak → deploy → health → first-admin → open-browser`.
`C:\AIOffice\setup-state.json` is an atomic JSON array containing completed
**phase names only**. An interrupted phase is retried, successful phases are
skipped. Reboot completion requires a changed boot timestamp, held separately
in `reboot-request.json`, never a premature "reboot completed" checkpoint.
Only one wizard may hold `setup.lock`. Updates do not overwrite setup state.

Credentials pass in memory as SecureStrings to `New-ProductionSecrets.ps1`.
The protected `C:\AIOffice\secrets` directory contains the env file, Keycloak
credential files, TLS files and Redis configuration. `setup-options.json`
contains only whitelisted non-secret options and stable tenant/company IDs,
so bootstrap retries cannot create a second company. Completed env files are
never regenerated. An incomplete secrets phase publishes the env file only
after TLS and the options are ready; re-enter credentials if creation failed.
Do not delete secrets or edit state to skip a failing phase.

Logs in `C:\AIOffice\logs` contain **only phase/status pairs**. The UI gives
Vietnamese fix-it messages for the failing phase. Child stdout/stderr and all
PowerShell diagnostic streams are discarded, with no raw exception/stack dump.
SQL probes pass credentials only in a private child environment, not argv.
Redis reads its password from `session-redis.conf`, not process arguments.
When updating a PR309 installation, the existing update script creates this
protected file from the retained Redis credential and adds its path to the
env file without rotating any key or password.
Keep the secrets directory offline with your backups; the encryption key is
needed to recover stored customer ERP credentials.

The wizard does not collect a pilot ERP connection. To preserve the existing
production validator/Compose requirements without granting ERP access, it
sets the legacy `PILOT_ERP_CONNECTION` to an unused, randomly credentialed
loopback port-1 endpoint. It cannot perform ERP reads. Configure actual customer
ERP credentials through the existing in-app workflow; validators, tenant
authorization and production Compose loopback boundaries remain enforced.

### Safe verification (development and CI)

These commands require PowerShell 7 and never run a real installer action:

```powershell
pwsh -NoProfile -File tests/windows-vps/Invoke-Checks.ps1
pwsh -NoProfile -File infra/windows-vps/Setup-Wizard.ps1 -WhatIf
pwsh -NoProfile -File infra/windows-vps/Setup-Wizard.ps1 -WhatIf -Update
```

The test runner downloads pinned test-only modules into the OS temporary
directory. The suite also runs offline with the inbox Pester module through
`tests/windows-vps/Invoke-OfflineTests.ps1`. CI uses `windows-latest`, parses
and runs PSScriptAnalyzer on **every** `infra/windows-vps/*.ps1`, runs Pester
(including sentinel-secret log/state/exception checks), and walks all 13 setup
and 4 update phases with `-WhatIf`. The dry-run return is before admin checks,
GUI loading, state/config access, network, process launch and all machine
actions. No real SQL/Docker/IIS/WSL/firewall/registry operation runs in this job.
Clean-VM GUI/UAC/reboot, certificate issuance, SQL/Keycloak and application
lifecycle acceptance must be qualified separately on an approved VM.

## Manual fallback

### Prerequisites

- The VM exposes hardware virtualization to the guest (nested virtualization;
  on Proxmox set the VM CPU type to `host`). Docker Desktop needs WSL2.
- A static public IP, DNS `A` records for the three host names, and router
  forwarding of TCP 80 and 443 to the VM. Never forward 3389 (RDP), 1433 (SQL)
  or any container port.
- SQL Server reachable from the VM on the private network, with
  `AIOffice_Identity` created per [DEPLOYMENT.md](DEPLOYMENT.md). Keycloak
  connects with `encrypt=true;trustServerCertificate=false`, so SQL Server must
  present a certificate trusted for `AIOFFICE_KEYCLOAK_DB_HOST`.

### 1. Host software (once, elevated PowerShell)

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\infra\windows-vps\Install-Prerequisites.ps1
Restart-Computer
```

After the restart, open Docker Desktop, enable **Start Docker Desktop when you
sign in**, and confirm `docker run --rm hello-world`. Docker Desktop runs only
in a signed-in session: configure automatic sign-in for the service account
and lock the screen, so containers return after Windows Update reboots.

### 2. Code and secrets

```powershell
git clone https://github.com/thanhtuyen662002/minh-huy-ai-office.git C:\AIOffice\app
cd C:\AIOffice\app
pwsh .\infra\windows-vps\New-ProductionSecrets.ps1 -WebHost <web host> -ApiHost <api host> -IdentityHost <identity host>
```

The script creates `C:\AIOffice\secrets` (Administrators, SYSTEM and the
operator only), `C:\AIOffice\secrets\.env.production`, generated passwords and
keys, and the private TLS certificate for the browser-session Redis. It prints
the variable and file names that must be populated locally (database connection, Keycloak SQL
login files, AI provider). Keep an offline copy of the secrets folder: the
`AIOFFICE_SECRET_ENCRYPTION_KEY` is required to read stored customer ERP
credentials.

### 3. Firewall and IIS (elevated PowerShell)

```powershell
.\infra\windows-vps\Configure-Firewall.ps1
.\infra\windows-vps\Configure-IisReverseProxy.ps1 -WebHost <web host> -ApiHost <api host> -IdentityHost <identity host> -LanPrefix "192.168.68."
# When DNS and port forwarding are live:
.\infra\windows-vps\Configure-IisReverseProxy.ps1 -WebHost <web host> -ApiHost <api host> -IdentityHost <identity host> -LanPrefix "192.168.68." -RequestCertificates -CertificateEmail <operator email>
```

win-acme registers a scheduled task that renews the certificates.

### 4. Deploy and update

```powershell
pwsh .\infra\windows-vps\Update-AIOffice.ps1 -Migrate
```

`Update-AIOffice.ps1` checks out `origin/main`, applies migrations when
`-Migrate` is given, starts Keycloak when `AIOFFICE_KEYCLOAK_ENABLED=true`, and
runs `infra/deploy-backend.ps1` with [compose.vps.yaml](../compose.vps.yaml).
The overlay adds:

- `session-redis`: TLS-only Redis for browser sessions (no persistence, so a
  restart signs everyone out), trusted by the web container through
  `NODE_EXTRA_CA_CERTS`.
- `extra_hosts` mapping the three public names to the host, so server-side
  calls (token exchange, JWKS, Core origin) reach IIS directly instead of
  relying on router NAT loopback. Certificates are still validated.

Set `AIOFFICE_BROWSER_OIDC_ENABLED=true` only after Keycloak is healthy behind
IIS and the first company administrator is bootstrapped.

## Backups

The VM holds no business data. SQL Server backups (both `AI_Office` and
`AIOffice_Identity`) are taken on the SQL host and copied off that machine.
Back up `C:\AIOffice\secrets` separately and offline.
