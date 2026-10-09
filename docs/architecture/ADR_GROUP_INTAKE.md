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

The full group workflow is unimplemented at the initial #276 checkpoint. Contract
tests/qualification are not evidence that SQL memory, worker, connector or automatic
notification has shipped. Continue #277–#279 after accepted prerequisites. Full #233
and all62 product requirements remain active; customer ERP and Windows acceptance
remain separate.
