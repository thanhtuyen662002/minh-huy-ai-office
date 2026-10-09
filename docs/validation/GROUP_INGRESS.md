# Group ingress: implementation and evidence

Issue277/PR283 implements the automatic customer-source ingress accepted by
issue276. It is explicitly stacked on PR282 until that parent is delivered.
The active PR HANDOFF records the exact candidate and workflow IDs.

## First SQL checkpoint

The additive `AddGroupSourceIngress` migration adds the operator-owned service,
connector account, binding, service capability and portal read-grant registry.
Source and internal destination are different roles. A global physical-group
index refuses a second role/account/company alias; identity hashes remain only
indexes and do not replace full ordinal string checks in the runtime.

Group listener leases, source commit cursors, immutable messages/revisions and
receipts, coverage gaps and reference-only outbox rows have tenant/company keys
and restrictive foreign keys. They do not use portal task ownership or expose
private owner-task history. Pending first/last timestamps and a committed cursor
are persisted for the automatic batch scheduler in278; no RAM timer or employee
approval is introduced. Reconnecting a listener does not erase its coverage gap.

Runtime SQL cannot enroll accounts, grant capabilities, change binding roles or
renew qualification. Registry rows are operator-owned and read-only. Original
message/revision/receipt rows are append-only. Mutable tables deny changes to
scope and immutable references, including column-level UPDATE grants. A dedicated
effective-permissions verifier composes with the existing global escalation,
ownership-chain and trigger proof. Destructive migration Down is refused; source
history requires reviewed forward repair.

Original source content uses AES-256-GCM with a random96-bit nonce and128-bit tag.
Tenant/company/source/message/revision/source-version/deletion-generation/key-ID
are authenticated associated data. Key material remains outside SQL source rows
and Git; the stored key identifier supports explicit retained-key resolution.
Text is strict UTF8, bounded to8000 UTF16 units, and plaintext byte buffers are
cleared after protection/decryption. This format does not itself authorize a
read: the runtime must check current authority before key resolution and private
content release. No encryption key or live credential is shipped by this change.

## Verified locally at the first checkpoint

- Nine new tests PASS: cross-scope/revision/deletion/key refusal, tampering,
  exact Unicode/whitespace/empty round trips, adjacent overflow and invalid scalar
  refusal, authenticated malformed/oversized cleartext, relational isolation,
  physical-role uniqueness, cursor/outbox invariants and migration permissions.
- Full869 Persistence tests PASS, zero skipped.
- Changed-file C# formatting, pending-model check and diff check PASS.
- Forward SQL generated from accepted task-intent migration without applying it.

## Service authentication checkpoint

The server verifies bounded strict JSON and an HMAC over the exact request body,
service ID, credential epoch, timestamp and nonce. SQL derives scope from the
current operator registry and requires the separate Ingest capability. A portal
JWT or request-supplied tenant, grant, qualification or secret reference cannot
construct the authenticated identity. Full original UTF16 bytes are checked
after index lookup; the original physical-group catalog independently refuses
role/account/company aliases even if an index is malformed. Secret resolution
and the final authenticated result are fenced by current SQL authority and the
effective-permissions proof on the same pinned transaction connection.

Live policy requires current controlled connector receive qualification. An
explicit owned Development fixture can exercise a synthetic connector and
cannot qualify or activate live use. Edit/recall also require current evidence
for those capabilities. Source content keys resolve only from exact configured
tenant/company/group/key enrollments, with explicit retained read keys and no
cross-source fallback. Key buffers are copied and cleared on disposal.

Thirty-six focused authentication/key controls PASS, including physical-role
aliases, exact case/padding identities, malformed stored UTF16, strict nested
JSON, signature/skew boundaries, current snapshots and private key refusal.
These use owned in-memory fixtures; actual SQL rights and concurrent commit
acceptance remain required. No API, store, listener or sender is exposed yet.

## Transactional source store checkpoint

Only the server-authenticated identity can call the store. A serializable pinned
SQL transaction takes the source application lock before current registry/lease
admission, then commits the protected immutable revision, exact logical-event
receipt, source counter and reference-only outbox together. The counter is read
fresh after the prior commit lock; a tracked snapshot or allocated identity is
not a committed cursor. Current authority, qualification, listener epoch/expiry
and effective rights are rechecked before saving and before committing.

Receipt identity binds the original event metadata and text. Transport signature
nonce/time and listener owner/epoch are excluded from the logical fingerprint,
so an identical protected spool event can reconcile after a listener restart.
A changed logical event under the same event ID is refused. Duplicate ACKs
resolve the original source/revision reference and do not renew pending time,
resolve another content key, create an outbox or allocate another sequence.

Edit/recall history is append-only; an original-unseen gap is persisted when the
first observed event is an edit/recall. A later original can be retained without
erasing that gap. The authorized current-source projection and extractor must
apply edit/recall precedence: a late original is not an undo of an edit or recall.
Historical backfill remains explicit on every revision; later batch processing
must separate it from notification-eligible live input. No current-content or
notification projection is delivered by this store checkpoint.

Twenty-one store controls PASS, including one protected graph, one hundred
sequential replays and changed listener recovery, six changed-envelope refusals,
stale/foreign leases, final authority/key/expiry denial before effects, immutable
edit/recall/late-original gaps, overflow and an independently committed context
against an old tracked cursor. These in-memory controls do not prove actual SQL
concurrent commits or rollback after a failed SQL write; those gates remain.

## Remaining acceptance

This checkpoint has no shipped group API, worker/listener/spool, broker consumer
or private inbox UI. Actual SQL application/effective-rights proof, commit-order
and duplicate100/revision/recall controls, rollback/no-outbox proof, protected
source reads, fenced listener/restart, broker interruption/recovery and issued
session denial/restored positives remain required. No runtime receipt, group
brain, note creation or notification success is inferred from model tests.

277 must finish those paths.278 owns actual scoped SQL source/notes/glossary
context, automatic extraction and atomic business notes.279 owns SQL-driven
internal IT reports and unknown-send reconciliation. Live connector qualification
remains separately unverified; synthetic fixtures must never activate live use.
