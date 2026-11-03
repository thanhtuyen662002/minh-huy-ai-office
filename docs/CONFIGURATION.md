# Environment, Configuration and Secret References

Minh Huy AI Office treats configuration metadata and secret values as different classes of data. Git, normal application configuration, application tables, prompts and audit events may contain **secret references**; they must not contain resolved credentials.

## Supported deployment environments

Runtime components accept exactly three environment classes:

- `Development`
- `Staging`
- `Production`

Unknown environment names fail closed at startup so a misspelled production environment cannot silently inherit the wrong policy.

## Configuration precedence

ASP.NET Core components use the standard provider order. Later sources override earlier sources:

1. committed `appsettings.json` for non-secret defaults and configuration shape;
2. committed `appsettings.{Environment}.json` when environment-specific **non-secret** defaults are needed;
3. Development user-secrets where appropriate for developer-local metadata;
4. process/container environment variables;
5. command-line overrides.

Production deployment automation selects the environment and injects references. A higher-precedence source may replace a reference or non-secret setting, but must not turn a reference field into a plaintext credential.

Web/client configuration follows the same separation: only explicitly public values may be exposed to browser bundles. Secret references and secret values are server-side only.

## Secret reference contract

Secret-bearing settings use an opaque URI:

```text
secretref://<provider>/<resource>
```

Examples:

```text
secretref://env/AIOFFICE_DB_CONNECTION
secretref://azure-key-vault/minh-huy/production/sql/aioffice
secretref://vault/customer-123/erp/read-only
```

Rules:

- scheme is always `secretref`;
- provider is a logical resolver name, not a credential;
- resource identifies the secret inside that provider;
- user-info, query strings and URI fragments are rejected because they can accidentally carry credentials;
- plaintext connection strings, API keys and passwords are invalid where a `SecretReference` is required;
- reference strings are safe metadata, but resolved values are sensitive and must be kept out of logs, prompts, normal database columns and audit payloads.

The first runtime adapter is `env`, which resolves a portable environment-variable name. Future providers such as Vault or cloud secret managers plug into `ISecretResolver` without changing stored reference syntax or business modules.

## Platform SQL Server

The API reads:

```text
AIOffice:PlatformDatabase:ConnectionSecretRef
```

Environment variable form:

```text
AIOffice__PlatformDatabase__ConnectionSecretRef=secretref://env/AIOFFICE_DB_CONNECTION
```

The process receives the actual `AIOFFICE_DB_CONNECTION` value from an ignored local environment file, service manager, container secret injection, or an approved secret-manager adapter. The API resolves it in memory and passes it to the persistence layer; it is not written back to configuration or the platform database.

## Summarized customer request bridge (#295)

This backend bridge is independent of `AIOffice:GroupIntake:Enabled`. It performs no model calls or Zalo sends. Apply the versioned `AddCustomerRequestBridge` migration before enabling it; its seven new tables are in `aioffice`. The migration grants the existing `aioffice_binding_runtime` role bounded ticket writes and append-only evidence/events. Original summaries, source identity, origin and customer desired due text cannot be changed by the runtime role.

| Key | Default / meaning |
| --- | --- |
| `AIOffice:CustomerRequests:Enabled` | Off unless exactly `true`; bridge routes return 404 while off or without platform SQL. |
| `AIOffice:CustomerRequests:Services:<n>:ServiceId` | Nonempty service UUID, unique across all entries; a service maps to exactly one company. |
| `AIOffice:CustomerRequests:Services:<n>:TenantId` | Existing tenant UUID from the platform company scope. |
| `AIOffice:CustomerRequests:Services:<n>:CompanyId` | Existing active company UUID; never selected from the request body. |
| `AIOffice:CustomerRequests:Services:<n>:SecretRef` | `secretref://env/AIOFFICE_CUSTOMER_REQUEST_HMAC`; resolves through `ISecretResolver`. The environment variable contains a canonical Base64-encoded, randomly generated 32-byte signing key. |
| `AIOffice:CustomerRequests:Services:<n>:AllowedSourceGroupExternalIds:<m>` | Exact, case-sensitive source group IDs; at least one per service unless `GrantBotChannelGroups` is on. No wildcard. |
| `AIOffice:CustomerRequests:Services:<n>:GrantBotChannelGroups` | Off unless exactly `true`. When on, the customer groups an administrator selects in **Kênh & Bot** for the service's own tenant/company are also allowed, checked per request after the signature, so adding or removing a group needs no API restart. Removing a group there also hides its tickets from the digest and status commands. Matching is exact (`wa:` prefix included for WhatsApp). |

Use double underscores for environment variable configuration, for example `AIOffice__CustomerRequests__Services__0__CompanyId`. Provision the actual signing key through the service environment; never put it in appsettings, Git, logs, payloads or responses. Unknown services, unknown/disallowed groups, absent/inactive companies and invalid signatures return 403 without storing anything. Invalid enrollment fails closed at startup. Configuration changes take effect on API restart.

The [bridge JSON and signature contract](features/customer-group-intake/README.md#summarized-customer-request-bridge-295) specifies headers, HMAC bytes, limits and portal calls. Portal access uses the existing BFF/session-backed bearer identity plus `X-AIOffice-Company-Id`; membership is resolved from SQL and must match the company in the URL. There is no new browser authentication mechanism.

For optional native SQL tests in `Platform.Persistence.Tests`, set `AIOFFICE_CUSTOMER_REQUEST_TEST_SQL` to an operator-provided SQL Server admin connection in the test process environment. The test creates, migrates and removes only its own randomly named `AIOffice_CustomerBridge_Test_<hex>` database, exercises the existing runtime role using a disposable database user, and never migrates the connection's existing database. Without that variable this native proof is explicitly skipped; the HTTP/service/contract tests still run.

## Bot channel administration (#302)

The internal team configures Zalo and WhatsApp in **Kênh & Bot**. This feature only stores configuration/status/commands; it never sends group messages. Existing file-configured bots are unaffected. Apply the generated `20261026111913_AddBotChannels` migration through the normal release process before enabling it. No migration is applied by API startup.

| Key | Default / meaning |
| --- | --- |
| `AIOffice:BotChannels:Enabled` | Off unless exactly `true`; all bot/internal/admin routes return 404 while off or without platform SQL. |
| `AIOffice:BotChannels:EncryptionKeySecretRef` | Required when enabled; `secretref://env/AIOFFICE_BOT_ENCRYPTION_KEY`. The resolved secret must be a canonical Base64 random 32-byte AES key. Use a **different key** from HMAC enrollment. |
| `AIOffice:BotChannels:AllowedAiHosts:<n>` | Empty by default. Exact operator-approved DNS/IP hosts for the server-side AI connection test, e.g. `api.openai.com`, or `api.anthropic.com` for Claude (base URL `https://api.anthropic.com/v1`; the test then also sends the key as `x-api-key`). No wildcards. Redirects, proxy forwarding and cookies are disabled; timeout is five seconds. Explicitly allow only the intended AI service hosts, including any private on-premises AI host. This allowlist does not enable arbitrary URL probes from the browser. |
| `AIOffice:CustomerRequests:Services:<n>:…` | Reuses the #295 service enrollment, headers, HMAC key reference, tenant/company mapping and durable nonce cache. Enrollment is loaded if either bridge is enabled; `CustomerRequests:Enabled` may remain off. Existing enrollment validation and source-group allowlists remain required. |

Use double underscores in environment variables, for example `AIOffice__BotChannels__Enabled=true`. These are infrastructure settings; the AI base URL/model/key, schedules, tech destination and customer selections are managed through the admin screen. The API reports enabled availability only to current company administrators. No new public browser flag is necessary.

`BotAiSettings.ProtectedApiKey` is an explicit encrypted-secret storage exception to the reference-only default below: AES-256-GCM with a fresh nonce and tenant/company/purpose authenticated data. Its encryption key remains outside SQL/Git in the secret resolver. Back up the encryption key with the database through the protected recovery process; all API instances serving this database must resolve the same key. Losing/changing it without re-encrypting the stored values makes the existing keys unreadable (bounded 503). Rotation requires a separately reviewed re-encryption procedure; changing the environment key alone is not a rotation procedure. API keys are write-only in administration: omission/null keeps the current key, `clearApiKey: true` clears it. There is no last-four hint. Only a freshly HMAC-authenticated enrolled company service receives decrypted keys through a no-store config response.

See the [bot channel tables, endpoints and connector contract](features/bot-channels/README.md) for canonical signed targets, limits, versions, QR/command expiry and follow-up integration guidance. The #295 source-group enrollment remains an independent ingestion authorization boundary: selecting a bot customer group does not grant that service permission to ingest requests from it.

## Trợ lý ERP queries (#344)

| Key | Default / meaning |
| --- | --- |
| `AIOffice:ErpQueryAgent:Enabled` (worker) | Off unless `true` (production compose: `AIOFFICE_ERP_QUERY_AGENT_ENABLED=true`). Off: Trợ lý only receives the bounded table catalog. On: Trợ lý runs a bounded agent loop on the selected read-only customer ERP source: search table/column names, describe tables, and run up to 5 model-proposed queries per question, then answer from the rows. |

Guardrails when enabled: the read-only login proof runs before and after the whole session; every query must parse as one current-database `SELECT`/CTE (no comments, `;`, `INTO`, data-changing or external-access keywords, 3-/4-part names, `NEXT VALUE FOR`) and is rejected before execution otherwise; queries run in a `READ UNCOMMITTED` transaction that is always rolled back, with a 20 s timeout and results capped at 50 rows, 20 columns and 200 characters per cell. Connection strings never reach the model. **Customer row data returned by these queries is sent to the configured AI provider** ("AI dùng chung" or the server fallback). Each query (purpose, SQL, row count, error) is stored in the task checkpoint as `erpQueries` and shown under the answer.

## Storage contract for future tables

Any table that needs a credential stores a reference field such as `CredentialSecretRef`, never a password/token/connection string. Resolver/provider configuration is infrastructure metadata. Rotation changes the underlying secret or reference under controlled release policy; agents continue to request logical resources rather than credentials.

## Operational rules

- Separate dev/staging/prod identities, secret namespaces and databases.
- Never commit a populated `.env`, certificate private key or secret export.
- Never log resolved secret values or include them in exception messages.
- Do not send resolved values to an LLM context.
- Secret-manager adapters must fail closed when a provider is unavailable or a reference is invalid.
- Production credential rotation follows the release/rotation mechanism; no ad-hoc source edits.
