# Kênh & Bot (#302)

AI Office stores company configuration for intake bots running on the VPS or another host. Customers continue messaging Zalo/WhatsApp; they do not sign into AI Office. This slice contains no connector, login automation, customer-group send path, VPS move or WhatsApp/Baileys runtime. Configuration defaults off, disabled channels and dry-run on. Existing `config.json` bots continue unchanged until a separate connector change deliberately switches them.

## Persistence and versions

One generated EF Core migration, `20261026111913_AddBotChannels`, adds six `aioffice` tables and updates the model snapshot:

| Table | Company-scoped data |
| --- | --- |
| `BotChannels` | Composite key `(TenantId, CompanyId, Channel)`; `zalo` or `whatsapp`, enabled/dry-run, normalized schedule JSON, tech group, config version, timestamps, SQL rowversion. |
| `BotCustomerGroups` | Composite channel FK plus ordinal external group ID; reported display name and assigned customer name. |
| `BotAiSettings` | One row per tenant/company; base URL, model, encrypted API key, version, updated time, rowversion. |
| `BotStatuses` | Last heartbeat per channel; login state, reported groups JSON, safe error message, summary/received times, PNG QR bytes/type/server expiry, rowversion. |
| `BotCommands` | Channel/company FK, UUID, `summarize-now` or `relogin`, creating user, created/expiry/ack times. |
| `BotChannelAudits` | Append-only tenant/company audit: actor UUID, server time, channel, field, before/after summary. API key changes use `[redacted]`/`cleared`. |

All reads and writes explicitly scope tenant **and** company. Company/channel composite FKs prevent foreign child rows. The runtime database role gets bounded writes, with append-only audit rights. SQL transactions use the existing serializable transaction wrapper and shared company application lock; internal calls also acquire the #295 service lock. Rowversions protect mutable state. No destructive migration or production data operation is part of implementation/testing.

Before the first stored config, reads expose version `"0"`, disabled/dry-run defaults and empty selections. Stored versions start at one. Every effective config edit increments the channel version; no-op saves do not increment or audit. Shared AI changes increment both existing channel versions and create defaults for missing channels. Version values are decimal **strings** in JSON to preserve SQL bigint precision in JavaScript. `expectedVersion` is mandatory on both admin config writes; stale writes return 409. Heartbeats and commands do not alter config versions. Nothing deletes or resets a config version.

Schedules contain up to 1,440 exact `HH:mm` values, de-duplicated and sorted, always `Asia/Ho_Chi_Minh`. Tech/customer IDs must come from that channel's last reported group list. Display names are checked against that list; customers have an explicit nonblank name. Customer IDs must be distinct, at most 100, and cannot equal the tech ID. Enabling requires a tech destination and at least one customer source. An unreported stale selection must be removed/reselected before saving.

## Internal bot contract

Enable/enroll through [configuration](../../CONFIGURATION.md#bot-channel-administration-302). Reuse the exact #295 `X-AIOffice-Request-Service`, `X-AIOffice-Request-Timestamp`, `X-AIOffice-Request-Nonce`, `X-AIOffice-Request-Signature` headers and canonical UUID/time/uppercase HMAC encoding. The signature domain stays `aioffice-customer-request-v1`. Sign these ASCII bytes separated by LF, without a trailing LF:

```text
aioffice-customer-request-v1
<service UUID>
<HTTP method>
<exact path including the canonical query, if any>
<Unix timestamp>
<nonce UUID>
<uppercase SHA256 hex of exact UTF-8 body bytes; empty bytes for GET>
```

The **method, path and query are signed**, preventing a Zalo signature being replayed for WhatsApp or an ack being replayed for another command. Clock skew is at most five minutes. Every HTTP attempt, including GET polls and idempotent ack retries, needs a fresh nonce. The durable nonce namespace is shared with #295, retained for ten minutes and cleaned per locked service. No company/tenant selector in a body/query can change the enrolled scope. Missing/inactive companies, unknown services, bad/stale/replayed signatures return 403. Flag off returns 404. Internal and admin responses, including errors, are `Cache-Control: no-store`.

### `GET /internal/bot-config?channel=zalo` (or `whatsapp`)

No body. The query must be exactly `?channel=zalo` or `?channel=whatsapp`; reject extra/duplicate/escaped aliases. Response:

```json
{
  "config": {
    "channel": "zalo", "enabled": false, "dryRun": true,
    "summarySchedule": ["08:00", "17:30"], "timeZone": "Asia/Ho_Chi_Minh",
    "techGroupExternalId": "reported-tech-id",
    "customerGroups": [{ "externalId": "reported-customer-id", "displayName": "Khách A", "customerName": "Công ty A" }],
    "configVersion": "3", "status": null
  },
  "ai": { "baseUrl": "https://ai.example/v1", "model": "configured-model", "apiKey": null },
  "commands": [{ "id": "11111111-1111-4111-8111-111111111111", "kind": "relogin", "createdAt": "2026-10-26T01:00:00Z", "expiresAt": "2026-10-26T01:10:00Z" }]
}
```

`ai.apiKey` contains the decrypted key when configured; never log/store this response in connector business logs or expose it in the browser. Bots should compare `configVersion`, apply shared AI changes, and fail safely on incomplete/disabled configuration. Returned pending commands are unacknowledged and unexpired, ordered by creation/ID. Repeated admin clicks while the same kind is pending return the same command ID. Commands expire after ten minutes; bots must check expiry and deduplicate by UUID before effects. Config pull/ack does not claim exactly-once external execution.

### `POST /internal/bot-status`

`application/json`, no query, at most 524,288 UTF-8 bytes:

```json
{
  "channel": "zalo",
  "loginState": "logged_in",
  "groups": [{ "externalId": "reported-customer-id", "displayName": "Khách A" }],
  "lastSummaryAt": "2026-10-26T01:00:00Z",
  "lastError": null,
  "loginQrContentType": null,
  "loginQrBase64": null
}
```

`loginState`: `logged_out|awaiting_qr|logged_in|error`. At most 500 distinct groups; opaque ID ≤200 UTF-16 units, no padding/control characters. Display input ≤1,000, control/bidi formatting stripped, capped at 200, nonblank. Summary time is UTC and cannot be over five minutes in the future. Arbitrary error input ≤8,000 becomes a fixed safe Vietnamese error message; raw errors/keys/URLs never enter the status table or response.

QR is optional, only in `awaiting_qr`: canonical Base64 of PNG raster content, decoded size ≤65,536 bytes, valid PNG signature/structure, dimensions 1–2,048. SVG/HTML and invalid image/type/Base64 are refused. Expiry is server receipt time plus two minutes, not a bot-supplied timestamp. Every heartbeat replaces the old QR; omitted QR clears it. `relogin` also clears the previous QR. Expired QR is never returned by the admin QR endpoint, and the web removes displayed QR at expiry. Response: `{ "accepted": true }`.

### `POST /internal/bot-commands/{canonical UUID}/ack`

`application/json`, no query, at most 1,024 bytes: `{ "channel": "zalo" }`. Scope comes from enrollment; foreign/missing command returns 404. Ack sets the first acknowledgement time; subsequent fresh-signed ack attempts succeed without changing it. Ack after expiry is allowed for reconciliation but expired commands are never delivered again. Response: `{ "acknowledged": true }`.

Both JSON APIs reject duplicate properties, malformed UTF-8, unknown fields, null/missing required fields, excessive nesting and invalid values. Size violations return 413, validation 400, stale config 409, bounded storage/key/timeout failure 503. Responses never include exception details or raw upstream errors.

## Admin API and web

Use existing issued-session/BFF identity, company selection header matching URL, and **fresh exact `admin` role** from SQL, checked before/after asynchronous sensitive reads and before committing mutations. Other members and foreign/revoked scopes get 403. The Vietnamese screen is available on desktop/mobile only to admins after successful enabled API discovery.

Prefix: `/api/companies/{companyId}/bot-channels` (web BFF `/api/local/bot-channels?companyId=…`).

| Method / suffix | Contract |
| --- | --- |
| `GET /` | `{ enabled: true, channels: [...two cards with status...], ai: { baseUrl, model, apiKeyConfigured, version } }`. No key or hint, no QR bytes. |
| `PUT /zalo` or `/whatsapp` | `{ expectedVersion, enabled, dryRun, summarySchedule, techGroupExternalId, customerGroups }`; response updated channel card. |
| `PUT /ai` | `{ expectedVersion, baseUrl, model, apiKey?, clearApiKey? }`; omitted/null key keeps existing; explicit clear allowed, clear+new key rejected. HTTP(S) base URL only, no URL credentials/query/fragment. Response contains only the AI view/boolean. |
| `POST /ai/test` | Empty body; tests **saved** settings through an injectable HTTP client at `{baseUrl}/models`, five-second timeout. `{ success, code }`, code `connected|host_not_allowed|upstream_rejected|connection_failed`; no upstream body/key. |
| `POST /{channel}/commands` | `{ kind: "summarize-now" | "relogin" }`; response command UUID/kind/created/expiry. |
| `GET /{channel}/qr` | Fresh `{ contentType: "image/png", base64, expiresAt }`, otherwise 404. |
| `GET /audit?offset=0&limit=50` | Newest-first field summaries; limit 1–100, offset 0–100,000. |

The BFF checks same-origin mutations and issued-session freshness, bounds/parses request and response bytes, projects approved fields and strips upstream headers/extra secrets. Web state is scoped to company/user/session generation; old responses are discarded, password fields clear after writes and unmount, QR expires locally, and private data does not enter browser storage.

## Connector follow-up

In the separate `zalo-intake` change, implement canonical HMAC polling/status/ack, version application, command UUID deduplication/expiry, QR heartbeat and no-secret logging. Keep file configuration as an explicit fallback until switched. Preserve #295 ingestion service source-group grants; UI selection is not an ingestion grant. AI Office never invokes a send API. Dedicated WhatsApp personal-number/Baileys support, media ingestion, reporting to the existing Zalo IT destination and `whatsapp` request provenance remain separate work.

Verification uses the existing in-memory persistence fixtures, HTTP integration host and web test framework; migration SQL/model checks do not connect to a database. Live SQL migration/runtime-role execution, real AI services and actual bot accounts are not exercised in this worktree.
