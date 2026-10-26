# Issue #302 — Kênh & Bot

HANDOFF

- Issue: #302
- Branch: `codex/302-bot-channel-settings`
- HEAD: `1c31585318fd9983ff780e51e81a7edb602ce04f` (base; implementation deliberately uncommitted)
- State: ready for user review; no PR opened, no push/commit/production operation, per explicit user instruction.
- Completed: persistence, generated EF migration/model snapshot, shared HMAC enrollment/signatures/nonces, shared AES-GCM envelope implementation, bot config/status/commands/ack, company admin APIs, write-only encrypted AI key, connection probe, Vietnamese web/BFF/navigation, unit/HTTP/web tests, configuration/security/connector docs.
- Current: user review of the working tree.
- Remaining: user-owned commit/PR and hosted CI. Actual release/migration, key provisioning/backup and bot connector integration are later authorized work.
- Tests/Evals: Release build PASS with zero warnings/errors. Full solution tests: 2,187 passed (Persistence 1,649, Contracts 507, Configuration 25, Observability 6); one existing opt-in real SQL test skipped. Web: 1,398 passed, 20 existing Redis integration tests skipped. `dotnet format ... --verify-no-changes` PASS; TypeScript PASS. No web lint script exists. New focused coverage is 66 backend and 33 web tests, including maximum Unicode selections/response lists.
- Known risks: no live SQL runtime-role execution, real AI endpoint or actual bot tested; hosted CI is unrun because changes are unpushed. Encryption key must be consistent across API instances and backed up; changing it without re-encrypting SQL values makes old keys unavailable. Existing #295 source allowlists are still independent ingestion grants.
- Blocked by: none for local implementation/review; live integration requires later release/account configuration.
- NEXT ACTION: review the scoped diff and new files, commit/open PR as desired, close hosted CI, then release the migration/feature with protected configuration. Separate `zalo-intake`/WhatsApp work consumes the documented protocol.
- DO NOT: run database updates on AI_Office/shared/production SQL, edit other worktrees/zalo-intake, start/stop bots, enable features or transmit group messages from this change.

## Review map

- `src/Platform.Persistence/Bot*`: six records/configurations, strict DTO parsing/validation, tenant/company services and version/audit/heartbeat/command rules, protected key and safe connection probe.
- `src/Platform.Persistence/AesGcmEnvelope.cs`: extracted common version-one encryption envelope from `GroupSourceContentProtector`; source-content wire format/purpose validation remains compatible. Bot credentials have a separate purpose and tenant/company authenticated data.
- `CustomerRequestAuthenticator`, `CustomerRequestReplayProtection`, `CustomerRequestIngress`, `Core.Api/CustomerRequestEndpoints`: share the established signing/header/bounded-body/nonce mechanism. #295 remains independently disabled/enabled and its original signing bytes remain unchanged.
- `src/Core.Api/BotChannelEndpoints.cs`, `Program.cs`, sensitive response cache/boundary wiring: internal/admin route registration, default-off 404, authorization, bounded errors/no-store.
- `src/Platform.Persistence/Migrations/20261026111913_AddBotChannels{.cs,.Designer.cs}` and model snapshot: generated with the local manifest `dotnet-ef 10.0.12`, using only a dummy loopback port-one design-time connection. Added the existing bounded runtime-role/append-only audit permission conventions. No database update performed.
- `apps/web/components/bot-channel-{panel,card}.tsx`, `local-ai-workspace.tsx`, `lib/bot-channel*`, `lib/bot-channels.ts`, BFF routes: admin/feature-gated desktop/mobile navigation, channel/group/schedule/AI forms, commands/QR/status/audit, same-origin and issued-session fencing, approved response projection. No browser storage of private data.
- Three backend test files and two web test files cover versioning/no-op/stale writes, encryption/tampering/scope, HMAC/replay/refusals, default-off routes, admin/tenant isolation, heartbeat bounds/sanitization/PNG CRC/QR expiry, commands/idempotent ack, exact AI host allowlist, web writes/key clearing/navigation/privacy and QR expiry.
- `docs/CONFIGURATION.md`, `docs/SECURITY_BASELINE.md`, `docs/features/bot-channels/README.md`: configuration, encrypted storage exception, tables/endpoints/canonical signing contract and connector follow-up.

## Endpoint/table inventory

- Internal: `GET /internal/bot-config?channel=zalo|whatsapp`, `POST /internal/bot-status`, `POST /internal/bot-commands/{id}/ack`.
- Admin prefix `/api/companies/{companyId}/bot-channels`: GET root/audit/channel QR; PUT channel/AI settings; POST channel commands/AI test. Web BFF prefix `/api/local/bot-channels` with the existing company selector/session.
- Tables in `aioffice`: `BotChannels`, `BotCustomerGroups`, `BotAiSettings`, `BotStatuses`, `BotCommands`, `BotChannelAudits`.

## Local verification environment

The linked Claude design artifact could not be fetched; the screen follows the existing company-admin UI conventions and all detailed issue requirements. A final parallel verification run hit existing web timing limits under heavy process load; the unchanged full web suite passed when rerun sequentially. No test assertions/timeouts were relaxed.

The repository initially had CRLF C# checkouts despite `.editorconfig` requiring LF. C# files were normalized to LF for the required whole-solution formatter; `git diff` shows no content changes to unrelated files. This sandbox cannot refresh the linked worktree's read-only Git index, so `git status` can temporarily show line-ending-only entries. A normal `git update-index --refresh` outside this sandbox reconciles the stat cache; inspect `git diff` for the actual content changes. No files were staged. No shared Git configuration was changed.

Default NuGet configuration under the user profile is inaccessible in the sandbox. Verification used a worktree-local `.next/build-profile/NuGet/NuGet.Config` with nuget.org, the existing read-only package cache, `RestoreConfigFile` and `NuGetAudit=false` process variables. Package manifest/lock versions were unchanged. `npm.cmd ci` ran at repository root before tests.

Existing tests require `pwsh`, which is not installed on the machine. A **portable, worktree-only** PowerShell 7.5.4 was downloaded into ignored `.next/tools/powershell`, verified against the [official release](https://github.com/PowerShell/PowerShell/releases/tag/v7.5.4) SHA256 `B40D192AE95BA6CCC4CC362FF4E1B18CA6FB5055BEBBCD3920684E12701FA8F6`, and added only to the verification process PATH. No system-wide installation occurred. Existing API-host tests also need process-local `Logging__EventLog__LogLevel__Default=None` because this sandbox cannot write Windows Event Log. These settings change no application source/test assertions. The optional SQL test variable was cleared for the test process; tests used in-memory fixtures only. Ignored `.next/{build-release,dotnet-test,format-verify,web-test}.log` retain the local results.
