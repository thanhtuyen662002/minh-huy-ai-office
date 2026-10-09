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
