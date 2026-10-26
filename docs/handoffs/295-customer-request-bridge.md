# Customer request bridge — handoff

HANDOFF

- Issue: #295 (`thanhtuyen662002/minh-huy-ai-office`).
- Branch: `codex/295-customer-request-bridge`, based on `ad747d0` (origin/main at start).
- State: implementation committed and pushed; draft PR open to `main`; awaiting CI and review.
- Completed: independent default-off summarized-request bridge (`AIOffice:CustomerRequests:Enabled=false` → 404), dedicated-domain HMAC (`aioffice-customer-request-v1`) with 5-minute skew and SQL nonce replay protection, configured service → single company + source-group allowlist (otherwise 403, nothing stored), atomic idempotent `YC-0001` numbering and request graph (items, evidence, open questions, events), `needs_clarification` when open questions exist, active-member scoped list/detail/PATCH with SQL rowversion and audited status/assignee changes, self-contained additive migration `20261026090633_AddCustomerRequestBridge` with runtime permissions, configuration and JSON contract docs.
- Tests/Evals (local, .NET SDK 10.0.402): solution `dotnet build` 0 warnings/0 errors; solution `dotnet test` 2,121 passed, 0 failed, 1 skipped (Persistence 1,583 + 1 skipped, Contracts 507, Configuration 25, Observability 6). Skipped: opt-in native SQL proof `CustomerRequestSqlTests.RealSqlMigrationRuntimePermissionsConcurrentIngestionAndRowVersionUpdates` (needs `AIOFFICE_CUSTOMER_REQUEST_TEST_SQL`; creates/drops its own disposable database). Forward migration SQL generation and pending-model check passed.
- Known risks: the native SQL proof has not been executed locally (no disposable SQL admin connection available on the dev machine). No migration was applied to any existing database. HMAC signature hex is uppercase per the documented contract; the bot must match.
- NEXT ACTION: let CI run (including the native SQL proof if CI provides SQL); review; when integrating with #294, regenerate/sequence the model snapshot if both branches add migrations.
- DO NOT: introduce UI/bot/model/outbound Zalo behavior, mark AI content human-confirmed, convert desired due text into a commitment, or enable the endpoint by default.
