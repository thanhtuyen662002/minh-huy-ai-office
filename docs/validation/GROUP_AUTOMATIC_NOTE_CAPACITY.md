# Combined automatic note capacity

Issue278 / Draft288 remains incomplete. This checkpoint prepares the SQL bound
for the sealed combined plan's maximum20 AI interpretations plus20 host
observations, without discarding either list. It does not enable a writer or
claim whole raw-batch completion, actual model semantics or release acceptance.

## Versioned expansion

`20261010194626_ExpandGroupAutomaticNoteCapacity` replaces only four check
constraints: receipt `NoteCount`, request `OriginCandidateOrdinal`, outbox
`NoteCount` and outbox-item `Ordinal` expand from1..20 to1..40. Every other
clause is byte-equivalent to the original migration, including zero-note
NoWork, source selection1..100, business state and publication timestamps.
Columns, keys, tenant relationships, operator ownership, effective runtime
permissions, envelope limits and host/AI/IT provenance remain unchanged.

The forward migration runs through the existing migration mechanism. Its Down
throws: persisted ordinals21..40 require a reviewed forward repair, so a
narrowing rollback cannot silently reject durable data. The current standalone
AI writer retains its original20-note/no-media/no-gap entry guards. The brain
reader retains its20-record bound; future consumers must read larger result
sets through complete bounded chunks and retain exact-current authorization.

## Verification

Two new cold EF controls compare all eight migration operations with the
original clauses, require the current model/snapshot to match, parse generated
forward SQL as SQLServer160 and deny unversioned columns, data or permission
operations. Ten retained model controls also pass, zero failures/skips.
The complete local persistence suite passes2097 tests, zero failures/skips.

Three new inert Python controls execute the shipping disposable probe, require
all20 boundary cases, explicit rollback on both acceptance and failure, exact
constraint-specific547 handling, changed-graph refusal and owned/canonical
scope checks before callbacks. All91 Python controls pass. Inert callbacks are
oracle verification; they do not qualify SQL execution.

After the original102 native oracles and the host-reader oracle, the shipping
probe attempts20,21,40,0,41 for each of the four constraints. It copies only
owned synthetic fixture metadata into uniquely identified temporary rows,
requires exactly one source row, rolls back every attempt, requires the first
three values to succeed and the last two to fail at the named constraint, and
compares the complete retained brain/claim/source/allocation/account/portal
graphs after each probe. It emits one fixed aggregate marker only when all
cases pass; future native acceptance therefore requires104 distinct mandatory
markers and the existing all-eight/Governance/six original Chromium images.

Native SQL boundary execution has not run on this source. The current remote
5ff2855 run still covers its original102 oracles only. Future combined atomic
writer, gaps, raw ledger/frontier, retry exhaustion, context/tokenizer, automatic
DI/UI and actual100/30 model evaluation remain required. Actual model status
is `NOT_RUN_UNEVALUATED`; reuse the existing Gateway/configuration loader.
