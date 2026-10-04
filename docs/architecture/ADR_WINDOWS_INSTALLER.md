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
