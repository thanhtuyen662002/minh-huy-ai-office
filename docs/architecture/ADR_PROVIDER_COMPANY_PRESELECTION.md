# Provider authentication before company selection

Issue #273, following accepted #266, #270 and #272. Status: accepted routine
implementation design for this issue; implementation and security/runtime
acceptance remain pending. This ADR precedes behavioral code.

## Problem and boundary

Fresh Home has no issued SID and displays the configured company. Ordinary
provider sign-in currently requires that company's membership before the user can
reach the signed-in company picker. An identity enrolled only elsewhere cannot
complete onboarding. No artificial default membership or customer grant change
is permitted. A browser company, URL or token company/role claim is never authority.

## Protocol

1. Ordinary browser sign-in starts a version2 discovery transaction. It retains
   the existing Code/S256 state, nonce, encrypted HttpOnly transaction cookie,
   registered binding generation, one-shot callback claim and five-minute
   absolute transaction deadline. Explicit signed-in company switching retains
   version1 and its exact selected-company authority checks.
2. After verified provider exchange, the BFF calls the existing identity-only
   `/api/auth/companies` directory with the signed access token. It supplies no
   company selector. Existing ordinal opaque identity, tenant-composite joins,
   complete100-company bound, ambiguity and malformed stored-name refusal remain
   authoritative. Neither this list nor preselection grants roles/company APIs.
3. Zero choices returns a useful fixed denial without issuing a company SID.
   One choice is freshly authorized through `/api/auth/context` and published
   using the existing provisional-session delivery acknowledgment protocol.
4. Multiple choices create one encrypted private Redis preselection payload in
   the same binding row. It contains verified tokens and the bounded directory
   choices, expires no later than the original transaction/token lifetime, and
   cannot be read through the issued-session method. The browser receives only
   company IDs/names on a dedicated no-store chooser, never tokens, tenant/user
   IDs, roles or workspace state. No private workspace is mounted for it.
5. Choice POST requires configured Origin/raw Host, the original binding and
   transaction, and a one-shot selection claim. The requested ID must occur in
   both the retained authenticated list and a fresh directory result; a fresh
   target `/api/auth/context` check must then succeed before SID publication.
   Revocation, ambiguous identity, changed/deactivated user/company, expiration,
   replay, cancellation or any unknown store reply refuses issuance.
6. New login/logout invalidates selection by generation. Callback and selection
   never rewrite the binding cookie, preserving later logout/login ordering.
   Failed, stale or lost completion does not clear or overwrite an existing
   valid issued SID. Only presenting a valid replacement SID acknowledges its
   delivery, as before. The eventual selected company on reload is read only
   from that private issued session.

## Compatibility and storage

No platform SQL schema or company/identity data mutation is required: the
identity-only directory and fresh context endpoint already exist. Redis adds
versioned encrypted preselection fields/phases inside the existing bounded
binding key. Existing version1 transaction/session payloads and scripts retain
their supported behavior; old workers encountering version2 refuse it rather
than reinterpret it. Rollout must drain in-flight version2 transactions before
rolling back to a version1-only worker. Redis incarnation/TIME, absolute expiry,
generation CAS, per-operation deadlines and no process-local authority cache
remain mandatory. Encryption uses the existing configured private key with a
distinct preselection purpose/AAD, not a second configuration or credential store.

## UX and acceptance

The ordinary button does not ask users to type a company ID. Multiple-company
users see a bounded authorized list and choose one; cancellation can return to
Home without a new SID. No-choice displays a fixed enrollment/access message.
The existing default-member and explicit company-switch flows stay supported.
Untrusted labels render as text. No role/private data is painted before a fully
authorized issued session passes existing current-session fences.

Required tests cover only-nondefault, single/multiple/no choices, identity
case/padding/tenant ambiguity and overflow, malformed names, replay/expiry,
callback/selection/logout ordering, lost replies/no cookie clobber, revoked and
restored target authority, default login and company switching. Required owned
SQL/Chromium uses ordinary enrolled identities without default membership,
actual provider sign-in, chooser, nondefault reload/history/source navigation,
old/default/cross-user denial and target loss/restoration; all unrelated owned
graphs and existing security/Origin/PKCE/session controls remain. Local mocks do
not qualify SQL/provider/browser or complete #233. Lead owns review/merge.
