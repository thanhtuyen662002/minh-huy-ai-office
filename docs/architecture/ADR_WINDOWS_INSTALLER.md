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

Progress protocol 3 is saved before runtime work. The reader accepts legacy 1/2 for upgrade and rejects future 4. Earlier helpers reject 3 before starting old binaries, including during explicit repair. Before bootstrap, the current helper redirects owned startup shortcuts and stops this installation's web/API/worker services. A failed drain prevents migration. Reuse requires protocol 3 Ready, retained configuration and a captured installation ID at the identical revision; other paths rebuild after draining. Identity, secrets, volumes and revocations are retained. No old binary is selected as automatic rollback; manually deleting progress or running old binaries is outside supported recovery.

Historical transition head b4c1ba508d64e9fef6bf02ed2cd73d242fc54712 passed Build 37510459026 and Governance 37510458992. Historical recovery head 45714aac54c1d4ae1eb9c3f3e4c4e4714c81f3db passed Build 37526047505 and Governance 37526047373, including the Windows artifact and complete SQL/OIDC/broker stack. These runs do not certify newer changes. Independent transition review and actual clean Windows upgrade/reboot/startup qualification remain required.

## Configuration recovery and Windows PowerShell

If `installation.json` is missing while `local.env` remains, initialization refuses to generate a new identity or overwrite retained secrets. The original manifest must be restored. A missing environment file can be rebuilt from the original manifest; repeated initialization preserves both files byte for byte. Progress also retains the requirement to restore existing configuration and its installation ID through prerequisite preparation, reboot and failure. When both files are lost after Ready, the initializer still refuses to create a new Compose identity. A different manifest ID is rejected before rewriting the environment or starting Docker.

Proven fresh protocol 3 progress can continue before any configuration was created. Legacy or quarantined progress cannot establish that the installation was fresh, so recovery requires an authoritative manifest even when both configuration files are absent. That uncertainty is persisted before a reboot. Explicit repair never infers permission to create new identity or volumes. Tests exercise the actual script in isolated paths with spaces and Vietnamese characters and verify generated secrets are absent from process output. These are temporary configuration tests, not clean Windows installation evidence.

The shared child-process factory removes inherited `PSModulePath` only for Windows PowerShell children. Both ordinary commands and the Docker Authenticode validation path use this factory, letting PowerShell 5 discover compatible system modules when the parent uses PowerShell 7. The parent environment and other executables are unchanged. A Windows test supplies an incompatible module path and proves both `Get-Acl` and actual system-executable Authenticode validation succeed.

Independent re-review of implementation 5836e29abf08807b37cad9ab84051f17fcd57d4a reports both original defects fixed and no new blocker in the diff. Its published head 63f80dc passed native artifact and actual stack in Build 37527413142; the .NET job failed one diagnostic substring assertion because PS7 ConciseView wraps stderr. Test correction 0a5759599cdbc3b40c55af10bd9fc0b2a085c4ac normalizes ANSI colors and pipe-prefixed wrapping while preserving all refusal, file, identity and secret-output assertions. All 76 installer-core tests, including an actual PS7 invocation, and changed-test formatting pass locally. Full current-head hosted verification and actual clean Windows qualification remain required.
