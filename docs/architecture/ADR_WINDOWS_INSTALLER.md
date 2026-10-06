# Native Windows installer and automatic startup

Status: implementation in #238 / draft PR #239. Full product goal #233 remains active.

Setup is a self-contained Windows 11 x64 executable with a pinned Git source archive and SHA-256 manifest. Customer machines do not need Node, npm, Git, Python or a .NET SDK. Docker builds FE, API, bootstrap and worker. Downloads need internet access. The development executable is unsigned and is not a production release.

The native wizard obtains Docker license consent. Only prerequisite preparation requests Windows administrator rights. It enables WSL and Virtual Machine Platform, enables the hypervisor at boot when necessary, prepares WSL without a user Linux distribution, downloads the official Docker installer and validates its Authenticode signature and Docker Inc publisher before execution. Redirects are restricted to the official HTTPS host.

Private LocalAppData stores a versioned installer, source, atomic progress and protected runtime configuration. Progress contains revision, phase and prior license consent only. Credentials are generated once; setup never persists command output or exception text. The owner password is available only in a masked onboarding field. Passwords are never command-line arguments.

Setup registers a per-user reboot continuation before prerequisite preparation. Windows restarts only after the user's action. Completion creates Start Menu and per-user startup shortcuts. Launch starts Docker Desktop in the background, validates the local Linux context and starts the retained compose project. Ready startup reuses the same revision; updates build new source and apply versioned migrations.

Repair quarantines damaged source and extracts the verified bundle again. Identity, configuration and Docker volumes are separate and retained. Setup never deletes volumes. Invalid state fails closed. Readiness requires all eleven services, a successful bootstrap exit, healthy containers where healthchecks exist and successful API/FE HTTP responses.

## Evidence and remaining acceptance

Portable core tests cover extraction, traversal, Windows reserved names, case collisions, links, hash mismatch, repeat extraction, unexpected installed content, retained identity, atomic progress and complete service readiness. Recovery retains malformed progress only under explicit repair and refuses unknown progress protocols. Typed diagnostics export only known service states, a bounded machine summary and failure codes; raw commands, configuration and credentials are excluded. Child-process tests verify removal of inherited Compose and application configuration overrides without modifying the parent environment.

Windows artifact CI publishes a pinned self-contained executable and validates its bundle from a path with spaces and Unicode, with host runtime variables disabled. It does not prepare WSL/Docker on the runner. Linux integration CI separately proves authentication, tenant isolation, read-only SQL, real RabbitMQ work and retained-volume restart.

Clean Windows installation, elevation, reboot continuation, retained owner/data and sign-in startup remain unverified. The development host has HypervisorPresent=false and no ready Docker Linux engine; firmware virtualization is enabled. Isolated temporary-directory shortcut/ACL checks and simulated UI previews are separate evidence and cannot establish clean installation. Signing, resource tuning and complete interruption/rollback recovery require further verification. Do not close #238 or claim full product completion on artifact build alone.

Primary references: [Docker Windows installation](https://docs.docker.com/desktop/setup/install/windows-install/) and [Microsoft WSL commands](https://learn.microsoft.com/en-us/windows/wsl/basic-commands).


## Secured upgrade boundary (2026-10-07)

Progress protocol 2 is saved before runtime work. The reader accepts legacy 1 for upgrade and rejects future 3. Pre-guard helpers only understand 1 and refuse 2. Before bootstrap, the current helper redirects owned startup shortcuts and stops this installation's web/API/worker services. A failed drain prevents migration. Only protocol 2 Ready at the identical revision permits container reuse; other paths rebuild after draining. Identity, secrets, volumes and revocations are retained. No old binary is selected as automatic rollback; manually deleting progress or running old binaries is outside supported recovery.

The published transition head b4c1ba508d64e9fef6bf02ed2cd73d242fc54712 passed all required checks: Build 37510459026 and Governance 37510458992, including the Windows artifact and complete SQL/OIDC/broker stack. Independent transition review and actual clean Windows upgrade/reboot/startup qualification remain required.

## Configuration recovery and Windows PowerShell

If `installation.json` is missing while `local.env` remains, initialization refuses to generate a new identity or overwrite retained secrets. The original manifest must be restored. A missing environment file can be rebuilt from the original manifest; repeated initialization preserves both files byte for byte. Tests exercise the actual script in an isolated path with spaces and Vietnamese characters and verify generated secrets are absent from process output. These are temporary configuration tests, not clean Windows installation evidence.

The command runner removes inherited `PSModulePath` only for Windows PowerShell children. This lets PowerShell 5 discover compatible system modules when the parent uses PowerShell 7. The parent environment and other executables are unchanged. A Windows test supplies an incompatible module path and proves the child can still use `Get-Acl`. All 63 installer-core tests and formatting of the changed C# files passed locally at implementation commit 8e59931f36bbb3160f122961e2b94642ff68787e. Hosted verification of the new published head is still required.
