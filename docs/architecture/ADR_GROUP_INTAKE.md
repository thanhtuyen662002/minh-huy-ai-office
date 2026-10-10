# Group intake and automatic internal IT workflow

Issue #276 establishes executable contracts for epic #275; #277 supplies SQL ingress,
#278 supplies scoped SQL memory and automatic notes, and #279 supplies the IT reporter.
The user attachment was read only after PR274 merged and exact main09b7 passed.
Its product requirements supersede the manual approval/customer-return plan in PR280.
It does not authorize account login, real messages, production activation or schedules.

## Scope and authority

Retain C#/SQL Server/RabbitMQ/Redis/Next.js and the existing AI Gateway. External
participants are opaque connector identities, never portal users. Source group,
internal technical destination, service principal, per-capability grant and audience
route are separate typed records. Ingest, extract and notify require separate grants.
Missing notification routing must not prevent safe ingestion/notes; it must prevent
notification. SQL enrollment must enforce source/destination role exclusivity, and
notification SQL must resolve the complete current customer-role collision set,
including customer bindings not present in a particular report. These are mandatory
#277 integration requirements; a contract alone cannot discover registry roles.
The MVP requires the same tenant/company for source and destination;
cross-company disclosure needs a later explicit reviewed contract.

Backend service authentication resolves a fresh principal and credential epoch; SQL
resolves current bindings, grants and route revisions. Contract validators cannot
authenticate a transport or make a caller-supplied snapshot trustworthy. The later
SQL/HTTP/worker slices must use them after trusted resolution, with final fresh
authority before private reads, notes commit and sending. A model never supplies
authority, source enrollment or notification destination.

External account/group/message/revision/sender/reply IDs retain their exact Unicode,
case and whitespace. Strict UTF8 rejects malformed scalars; bounded length-prefixed
SHA256 keys distinguish trailing spaces and avoid SQL Server padded equality. SQL
must compare the full ordinal identity after lookup; a hash alone is not authority.
Names are display metadata. A technical destination cannot be one of the known
customer source groups on the same provider, even through a different account.

## Workflow contracts

Internal broker messages carry scoped SQL references and immutable revision/hash
metadata, not whole customer messages, credentials or model-selected destinations.
Freeze exact committed revisions/cutoff under a per-source transaction lock; never
use an uncontrolled MAX(identity). Backfill is explicitly no-notification. Ordinary
new source text needs no mention or operator button. Event-driven debounce has a
SQL due checkpoint; configurable initial tuning is 30s quiet/120s maximum, not an
owner-mandated schedule. Gaps/media/model failures produce attention notes with
honest coverage after bounded attempts, followed by automatic IT notification.

Source revisions, old notes and optional authorized glossary are stored in SQL and
resolved within one source scope before budgeted context assembly. Context manifest
references do not replace protected source content. Runtime task history remains
owner-private and is not group memory. CustomerRequest is a business record; worker
completion is not IT resolution. No human approval is a runtime prerequisite.

Notes and NotesCommitted outbox references commit atomically. The reporter reads
those committed note revisions, uses deterministic projection and current per-source
audience grants, and binds destination/routes/revisions/content digest in a durable
send intent. Missing/revoked routes never fall back to customers. Possibly accepted
timeouts remain unknown and require reconciliation; they cannot be blindly retried.

## Connector qualification

Upstream `RFS-ADRENO/zca-js` package2.2.0 at
`d22c28fcabd70375c144980e9e310c38f8142590` was inspected on2026-10-10.
This is a reference candidate, not an installed dependency or a qualified account.
[Upstream](https://github.com/RFS-ADRENO/zca-js) documents personal-account browser
integration, group receive/send and a single web listener; account restrictions are
possible. [Message](https://zca-js.tdung.com/en/listeners/message) and
[send](https://zca-js.tdung.com/en/apis/sendMessage) documentation are documentation
evidence, not controlled runtime acceptance. Keep all live capability observations
unverified until an authorized controlled account test records exact version,
environment and evidence. No account login or live sends occurred.

The executable qualification policy copies observations, binds exact tenant/company/
account/provider/version/commit, requires explicit recent controlled evidence for
each mandatory capability, and refuses all synthetic qualification for live profiles.
Initial freshness ceiling is30days; runtime membership/grant/route checks remain
mandatory immediately before ingest/send. Optional edit/recall/reply support may stay
unverified; consumers must record the resulting coverage/invalidation limits.

Required observations cover group text, provider message/revision/sender/reply IDs,
self/echo correlation, listener collision/gaps, edit/recall, membership, text sending,
provider acceptance and unknown-send reconciliation. Unsupported/unverified optional
features are explicit; never assert complete reconnect history, recipient delivery or
provider exactly-once from a library API signature. Synthetic qualification can
exercise core contracts but can never enable a live receive/send profile.

## Delivery status

### Durable listener ownership and interrupted coverage

Listener transport uses a separate fixed HMAC domain. Its sealed command carries
the authenticated service/credential epoch, server-derived account scope, nonce
and SHA256 of the exact captured command bytes. Caller OwnerId is a process fence,
not authentication. Current qualified account and enrolled source/Ingest authority
must be resolved again while consuming the command; an administrator/portal JWT
or model cannot create listener authority.

Serialize the service/credential-epoch/nonce lock before the account lock, both
transaction-owned, before reading current registry and retained lease. Persist the
lease transition, account coverage marker and append-only command receipt in one
SQL transaction. Save the lease mutation before staging account-gap/receipt
inserts to keep lease-before-gap lock order explicit across listener, source read
and ingress; both saves remain in that one owned transaction. Replaying the same nonce/body reconciles its original ACK without
extending expiry or creating coverage. Conflicting nonce reuse refuses. Expired,
stopped or superseded live ownership cannot be resurrected by an old Acquire/Renew;
fresh operations need fresh signed command identities. Stop replay only reconciles
that exact retained stopped row. Fresh final authority/qualification/signing time
and lease expiry checks precede commit and ACK.

Account interruptions affect all sources on the same tenant/company/account.
`GroupAccountCoverageGaps` preserves fixed startup/expiry/stop markers and the last
confirmed heartbeat without iterating every source lock or silently claiming
complete history. A startup lease does not prove a provider connection. These
append-only markers have no automatic reconnected/complete flag; source private
reads/head catalogs conservatively include them. Future batch memory must preserve
both source and account uncertainty.

The additive `AddGroupListenerOwnership` migration creates account coverage and
typed command receipts with scoped restrictive FKs, operator ownership and runtime
SELECT/INSERT only. Receipts contain command digests and ACK metadata, never raw
commands, provider content or credentials. Apply migrations before upgrading the
group runtime: the new permission verifier refuses missing/unsafe tables. Existing
tables/permissions are unchanged; older runtime can coexist after the expand.
Rollback is forward repair, not deletion of replay/coverage evidence.

This is a persistence design checkpoint. Local policy/auth/store tests and generated
migration SQL do not establish a running listener, native atomic rollback/locking,
provider qualification, spool/broker recovery or full #277 acceptance.

The full group workflow is unimplemented at the initial #276 checkpoint. Contract
tests/qualification are not evidence that SQL memory, worker, connector or automatic
notification has shipped. Continue #277–#279 after accepted prerequisites. Full #233
and all62 product requirements remain active; customer ERP and Windows acceptance
remain separate.

### Reference publication and durable worker inbox

The committed group outbox publishes only a strict2048-byte metadata reference to
a separate durable tenant/company/service RabbitMQ queue. It does not publish
source plaintext, encrypted content, keys, portal identities or batch decisions.
Current SQL Extract authorization is independent of Ingest. The trusted worker's
tenant/company/service/credential epoch comes from host configuration; broker or
model fields cannot change it. Consumer acceptance appends one immutable scoped
inbox receipt, after validating the committed SQL revision/outbox graph and current
authority under the source lock. ACK follows that durable receipt. Neither broker
confirmation nor inbox arrival advances the SQL batch cursor.

Publication commits a five-second reservation and retries the original EventId
until SQL has its inbox receipt. Fresh no-tracking reads and owned attachment
prevent a previous EF identity-map entry from overwriting a newer reservation.
Publish/ACK/NACK waits are bounded at ten seconds. Reconnection and cancellation
retire only the original channel attempt; late callbacks cannot settle a replacement.
The producer traverses at most32 source candidates in identity-hash order per pass,
wraps its in-memory traversal index, and uses SQL for all durable backlog/retry state.
Each candidate is independently reauthorized; one unavailable source leaves its
original SQL backlog pending and does not erase other sources' work.

The pipeline is default-off behind the exact host value
`AIOffice:GroupIntake:PipelineEnabled=true`, plus group intake enabled and a platform
database. Its `Worker` section requires canonical TenantId/CompanyId/ServiceId and
positive CredentialEpoch. Core hosts publication; Agent.Worker hosts consumption.
One process owns one configured company/service binding. Additional companies use
separately configured processes. Shared registrations may reuse exactly the same
binding and must refuse replacement authority. Startup/per-operation effective SQL
permission proof remains required. Apply the additive inbox migration before this
runtime; rollback preserves immutable receipt evidence through forward repair.

### Account-owned capture and spool recovery session

The connector recovery session keeps one generated process OwnerId for both new
captures and retained replay. A ten-second caller/deadline boundary includes its
serialization wait, current backend enrollment, actual Acquire/Renew, key access
and authenticated event receipt. Waiting cancellation cannot clear another active
operation's lease. Restart replaces the owner, which must wait for the old SQL
fence to expire; the backend records the resulting coverage uncertainty.

Host configuration fixes the account, service, dedicated spool key and at most256
source scopes/opaque identities. Validate the open spool's immutable ownership
before file/key/HTTP access. DM, self, known report echoes and unknown group
identities refuse before capture work. The backend remains the source of fresh
qualification and authorization; source versions are never derived from a payload.

Capture persists encrypted content before sending, returns only an authenticated
SQL receipt and retains the exact original envelope after an unknown result.
Replay re-fetches metadata and renews the current lease before each private load,
key access or decryption. Only the exact original event ACK deletes that capture.
The returned actual renewed lease replaces the earlier RAM snapshot.

Recovery traverses at most32 retained event hashes per pass and wraps its RAM
position, so a refused item does not permanently starve later captures. Counts are
metadata only. An empty pass rotates enrollment heartbeats without claiming a
provider connection or complete history. This library adds no provider, hosted
service, deployment activation or customer sender. Owned runtime recovery proof
and separately authorized live qualification remain required.

### Default-off Worker recovery host

`AIOffice:GroupIntake:ConnectorRecoveryEnabled=true` explicitly registers an
account-owned recovery runtime and background loop in Agent.Worker. Group intake
and the platform database must already be configured; startup performs the group
effective-permission proof. Absence or any other flag value registers nothing.
DI resolution is inert: the runtime opens its volume only on an enabled capture
or recovery operation. The recovery loop and future qualified provider bridge use
the same runtime/session owner; no provider connection is added here.

The `Connector` section fixes canonical TenantId, CompanyId, ConnectorAccountId,
ServiceId and positive CredentialEpoch; BackendOrigin is HTTPS with no path,
credentials, query or fragment. PrivateRoot must be an absolute existing private
volume, not a filesystem root. SigningSecretRef and SpoolSecretRef are separate
purpose references, SpoolKeyId is bounded, and Provider/ExternalAccountId plus
contiguous `Sources:0..255` SourceBindingId/ExternalGroupId entries are cloned.
Configuration refresh cannot replace the binding. Reference producer/consumer
and recovery registration refuse mismatched company/service/epoch in either order.
Production permission and private-volume qualification remain operator release gates.

An owned Development fixture may explicitly select the existing synthetic policy
and loopback HTTP. Production cannot select that profile; no synthetic capability
qualifies a live account. Stop cancels shared capture/recovery and disposes owned
client/volume without deleting pending captures. The loop waits one second after a
successful pass and five after refusal; logs use a fixed message without payload,
private paths, IDs, keys or dependency exceptions. Heartbeats still retain coverage
uncertainty and never claim a provider connection or complete history.

This registration is an implementation candidate. Local DI/query/SDK controls do
not qualify actual SQL concurrency, RabbitMQ interruption/restart, provider
connection, live account membership or a production deployment. Mandatory owned
native delivery proof and full277 acceptance remain outstanding.
