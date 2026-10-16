# Immutable terminal key material enrollment — issue 278 / Draft 288

This additive checkpoint supplies metadata storage and a pure material comparison.
The owned signing/advance store, runtime enrollment and retained-key inventory
are still to be implemented. Existing source key provider/configuration/secret
resolver and AI Gateway remain the integration points.

## Material identity

`GroupTerminalAdvanceKeyProof` derives a separate 32-byte HKDF-SHA256 value with
versioned purpose and scope/KeyId salt, then stores its SHA256 fingerprint. Derived
bytes are zeroed. Neither key bytes nor a secret reference enter the new table.
The fingerprint separates tenant/company/source and KeyId, so a public fingerprint
copied from another enrollment cannot establish the expected material identity.

`Require` compares resolved bytes with the original immutable fingerprint using
fixed-time equality, with bounded copied hashes and fixed inner-free errors.
`Stage` records the supplied coherent Extract actor/versions/authority hash and
UTC. This validates metadata shape; fresh actual authority still belongs to the
future SQL directory/store. A later valid authority rotation retains the original
enrollment actor and fingerprint. Missing or changed material refuses whenever
that key is resolved; the helper itself does not read older keys.

The future store must resolve keys outside SQL, then compare the exact reread
enrollments under its owned Serializable source lock. Validating every reachable
older key requires a finite inventory and resolving each; this table alone cannot
detect a changed secret that the bounded call never reads. The configured provider
currently freezes scope/KeyId/reference bindings, not bytes across process restarts.
No fallback or automatic re-enrollment of a changed ID is permitted.

## Versioned metadata expansion

Migration `20261011061648_AddGroupTerminalKeyEnrollment` adds the 14-column immutable
`GroupTerminalAdvanceKeys` table with scoped ordinal KeyId primary key, finite
32-byte material/authority hashes, actor/version/UTC checks and restrictive company,
source-state and service relations. A restrictive advance-to-key FK binds exact
tenant/company/source/KeyId. Existing advance columns and signed envelope format,
terminal/cursor rows, rights and runtime paths retain their contracts.

Before any DDL, the migration refuses any pre-existing signed advances. Their
enrollment cannot be inferred from today's mutable secret material. The expansion
is unmerged and has never been deployed here; fresh empty chains can install both
sequenced migrations. A nonempty older installation needs independently reviewed
historical key mapping/migration. There is no automatic backfill or rebaseline.
Destructive Down refuses; history requires forward repair.

The established operator owns the new table. Runtime has SELECT/INSERT and explicit
table mutation plus all 14 column UPDATE denials. The effective verifier repeats
the entire advance/terminal/work/ingress chain and refuses wrong owner, missing
rights, any effective mutation or writable current/future column.

## Verification and limits

All 3053 private Persistence controls pass: 21 new enrollment/model controls and
3032 retained, zero failures/skips/compiler warnings. Independent RFC extract/expand
checks, cross-scope/KeyId/material rebinding, original actor retention, malformed
hashes/UTC/versions/null/disposed inputs and finite key boundaries are covered.
Actual EF metadata and complete idempotent SQL160 grammar verify physical bounds,
exact restrictive relations, early legacy refusal, no backfill and immutable
rights without opening SQL. EF reports no pending model changes; all 12 affected
C# files pass formatter verification.

The first focused run passed 96 and failed one new test that read an uninitialized
standalone Snapshot relational model. It now uses EF's supported
`HasPendingModelChanges` comparison; the final complete 3053 run passes. Global
Group inventory/rights coverage now includes the 34th Group entity and this migration
while retaining every prior tenant/company/relation/owner/column assertion.

Parent `8d5106f` carrier source is approved in receipt `6106154180`, root full-read
7257 characters, independent 124 controls. Parent remote `013a807` completed scoped
117/all8/Governance/full .NET 3538/quality/six-original-image qualification, recorded
in `GROUP_TERMINAL_RECEIPTS_013_QUALIFICATION.md`. It predates historical SQL reader,
signed schema, carrier proof and this enrollment checkpoint. Physical key/schema
attacks, actual historical reading, runtime enrollment/inventory, latest-leaf and
atomic cursor advancement remain **NOT_RUN_UNQUALIFIED**. Full #278/#279/#233 and
merge acceptance remain incomplete. Actual model stays **NOT_RUN_UNEVALUATED**
through the existing AI Gateway.
