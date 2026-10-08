# Authoritative company discovery and browser selection

Issue #266 / Draft PR #267, under full product #233. Actual two-company SQL/OIDC/Chromium passed at f03; the latest independent frozen security/runtime review and final PR/main gates remain. Scoped evidence is recorded in `docs/validation/COMPANY_SELECTION.md`.

## Discovery

The authenticated company directory uses signed identity provider/subject claims. SQL Server binary candidate equality and an ordinal stored-key fence preserve opaque identity semantics, including trailing-space aliases. Active users, memberships and companies join on tenant-composite keys. Only company ID/name leave the directory; provider subjects, user/tenant identifiers, credentials and role assignments remain private.

Explicit enrollment of the same signed identity in distinct tenants can yield distinct companies. Duplicate selectable company IDs or multiple users for that identity inside one tenant are ambiguous and deny the whole list. The complete choice set is limited to 100; overflow is refused rather than silently truncated. Malformed names/identifiers also deny without publishing partial choices. No list or permission result is cached.

SQL names are projected as their stored UTF-16 bytes within the same bounded tenant-composite join and decoded strictly, with a 400-byte name limit. Reading `nvarchar` directly through the driver repaired the owned malformed-surrogate fixture before managed validation could reject it. The constant SQL projection has no caller-controlled SQL; provider/subject remain parameterized binary candidate predicates. No second name lookup or permission cache is introduced.

`/api/auth/companies` is the sole endpoint registered with `IdentityCompanyDirectoryEndpoint` metadata. This server-owned marker permits authenticated discovery before selecting a company and produces no company/role context. Headers, URL spellings and client claims cannot supply the marker. All existing company-scoped APIs and hubs remain behind the existing context middleware. Routed trailing-slash/case aliases, success and refusals receive `no-store`.

## Browser scope transition

The BFF directory uses the existing issued-session Core helper and a final shared-session check. The directory response has a shared ten-second deadline and a 128 KiB byte limit, is decoded with strict UTF-8, and must satisfy the complete two-field choice contract before returning it. Both stored names and JSON escaped names must contain valid Unicode scalar sequences: isolated UTF-16 surrogates are refused before a serializer or browser can repair the name. Valid supplementary characters and a literal U+FFFD remain supported. Browser choices are navigation options, not authority. The workspace independently validates current context before accepting them.

Switching clears private chat/source/member/task state and invalidates request generations before beginning the existing Code/S256 provider flow for the target. Cookie mutations settle without being aborted. Core independently resolves target membership at callback; stale cached choices cannot publish an unauthorized target session. Existing Redis binding generations, one-shot transactions, callback/reordered-response and session-expiry protections remain unchanged. The selected company after callback/reload comes from the private issued server session, not a URL or browser storage value. Names come from the authoritative directory.

The private workspace body is keyed by scope/generation. This also clears uncontrolled draft fields when a fast failed transition and restoration are batched into one React render. Ordinary validation of an unchanged session preserves its draft. Supported explicit local development mode keeps its current login semantics.

No schema, identity, membership or role mutation is introduced by selection. Existing durable tasks remain in their original company. Customer ERP enrollment and business capabilities keep their separate authorization and credential requirements.

## Delivery still required

Actual disposable SQL identity/padding/ambiguity/Unicode controls and provider/Chromium switches in both directions, target membership revoke/restore, fresh private chat/draft clearing and source/task/member separation passed at f03. Independent review of the latest fixes and exact final PR/main delivery remain mandatory. This checkpoint does not complete #266, production identity or full #233. Installer #239 remains Draft pending its separate clean Windows/signing evidence.
