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

## Prerequisites

- The VM exposes hardware virtualization to the guest (nested virtualization;
  on Proxmox set the VM CPU type to `host`). Docker Desktop needs WSL2.
- A static public IP, DNS `A` records for the three host names, and router
  forwarding of TCP 80 and 443 to the VM. Never forward 3389 (RDP), 1433 (SQL)
  or any container port.
- SQL Server reachable from the VM on the private network, with
  `AIOffice_Identity` created per [DEPLOYMENT.md](DEPLOYMENT.md). Keycloak
  connects with `encrypt=true;trustServerCertificate=false`, so SQL Server must
  present a certificate trusted for `AIOFFICE_KEYCLOAK_DB_HOST`.

## 1. Host software (once, elevated PowerShell)

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\infra\windows-vps\Install-Prerequisites.ps1
Restart-Computer
```

After the restart, open Docker Desktop, enable **Start Docker Desktop when you
sign in**, and confirm `docker run --rm hello-world`. Docker Desktop runs only
in a signed-in session: configure automatic sign-in for the service account
and lock the screen, so containers return after Windows Update reboots.

## 2. Code and secrets

```powershell
git clone https://github.com/thanhtuyen662002/minh-huy-ai-office.git C:\AIOffice\app
cd C:\AIOffice\app
pwsh .\infra\windows-vps\New-ProductionSecrets.ps1 -WebHost <web host> -ApiHost <api host> -IdentityHost <identity host>
```

The script creates `C:\AIOffice\secrets` (Administrators, SYSTEM and the
operator only), `C:\AIOffice\secrets\.env.production`, generated passwords and
keys, and the private TLS certificate for the browser-session Redis. It prints
the values that must be pasted in locally (database connection, Keycloak SQL
login files, AI provider). Keep an offline copy of the secrets folder: the
`AIOFFICE_SECRET_ENCRYPTION_KEY` is required to read stored customer ERP
credentials.

## 3. Firewall and IIS (elevated PowerShell)

```powershell
.\infra\windows-vps\Configure-Firewall.ps1
.\infra\windows-vps\Configure-IisReverseProxy.ps1 -WebHost <web host> -ApiHost <api host> -IdentityHost <identity host> -LanPrefix "192.168.68."
# When DNS and port forwarding are live:
.\infra\windows-vps\Configure-IisReverseProxy.ps1 -WebHost <web host> -ApiHost <api host> -IdentityHost <identity host> -LanPrefix "192.168.68." -RequestCertificates -CertificateEmail <operator email>
```

win-acme registers a scheduled task that renews the certificates.

## 4. Deploy and update

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
