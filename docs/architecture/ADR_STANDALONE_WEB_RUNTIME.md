# Standalone web runtime for automatic setup

Status: accepted for the FE runtime dependency in issue #234.

## Context

The backend deployment currently requires a separate Node/npm frontend startup. A Windows setup must start the actual FE and BE from versioned resources, while supplying generated company settings after images have been built. Static generation of the root page freezes those settings into an image.

## Decision

Build Next.js standalone output with the repository as its tracing root and a tracked portable npm dependency lock. Ship a Node 24 Alpine image containing the traced server, static assets and public assets. Run it as the image's non-root `node` user, with a read-only root filesystem and bounded writable cache/temp mounts. Publish port 3000 on host loopback in production Compose.

Render the root page dynamically so server-side company settings are supplied at runtime. Company selection remains display/input configuration: authorization still comes from Core.Api's validated identity and membership context. The local-only password-grant BFF remains disabled in production Compose. Production OIDC is still a required product flow in the completion matrix.

Require a real container smoke that starts the same image twice with different company settings, fetches HTML and referenced static assets, verifies non-root/read-only/security settings and checks that disabled local sessions/login stay fail-closed with no-store responses. Make this job a dependency of Required quality gates.

## Operational consequences

The deployment command builds and starts FE alongside API/worker and waits for FE HTTP readiness. FE process/HTML readiness does not prove identity, database, broker, AI or business workflow readiness; installer and product acceptance must verify those separately. No host Node/npm installation is required to run the FE container.

One-click Windows setup, automatic SQL/OIDC provisioning, startup/reboot recovery and full product completion remain work in #233. Container CI supplies Linux runtime evidence and does not prove a clean Windows installation.
