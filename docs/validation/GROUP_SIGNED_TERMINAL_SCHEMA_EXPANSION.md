# Signed terminal advance schema expansion — issue 278 / Draft 288

Status: source preparation. No advance store, cursor contract, key resolution,
actual SQL Server qualification, runtime DI or model acceptance is supplied here.

## Sequenced expansion

Migration `20261011054423_AddGroupSignedTerminalAdvances` adds one append-only
`GroupTerminalAdvances` table and one exact alternate terminal key. Existing
terminal and cursor columns, rows, constraints, rights and runtime paths remain.
There is no data backfill, cursor repair, automatic rebaseline or deployment.
This is the expansion step of the signed frontier decision in
`ADR_GROUP_SIGNED_TERMINAL_FRONTIER.md`.

The later owned store must reject any positive cursor without an authenticated
chain, and any cursor that differs from the latest authentic signed leaf. It must
reconstruct original terminal evidence under fresh current Extract rights,
resolve retained read/write keys outside SQL, reread the exact preflight bytes
under the source lock, then atomically save/read back the advance and cursor.
Existing positive cursors require independently verified historical migration;
they cannot become trusted through their last terminal batch or an unchecked
signature. The restrictive cursor-to-leaf FK is a later sequenced contract after
that migration or explicit refusal of untrusted legacy state. This expansion
does not make today's mutable cursor authoritative.

## Physical model

26 metadata columns store the exact scope/version/terminal interval, original
terminal hash/UTC, predecessor version/through/full-envelope fingerprint/advance
UTC, finite integrity envelope/key ID, advancing actor/authority and advance UTC.
The envelope is legal physical `varbinary(385)` with a 322–385-byte CHECK, version
one `AIOGFAD1` magic and SHA256 fingerprint. All fixed hashes are `varbinary(32)`
with exact logical lengths. Key ID is finite binary-collated `varchar(64)` with
ASCII character and trailing-space checks. The schema does not verify the MAC or
bind every projected field to signed bytes; the future authenticated row proof
and store must perform those checks before trusting or writing this carrier.

The scoped primary key is version; scoped unique After and batch indexes prevent
forked intervals or repeated batch advancement. All five FKs are restrictive:
company, source state, advancing service, exact terminal and exact predecessor.
The terminal FK binds scope/batch/operation/After/Through/manifest hash/commit UTC.
The predecessor FK binds scope/version/Through/fingerprint/advance UTC.

The root explicitly has Version one, After zero and **all four** predecessor
fields NULL. A successor explicitly has Version greater than one, After positive
and **all four** predecessor fields NOT NULL, previous version plus one, previous
Through equal After, a nonzero 32-byte fingerprint, UTC previous advancement no
later than current advancement. Explicit alternatives prevent SQL composite FK
null bypass and CHECK UNKNOWN from admitting a partially missing predecessor.
Each interval remains 1–500 raw revisions; authority values and both terminal
and advance UTC retain their finite positive/nonnegative bounds.

## Effective immutable rights

The migration assigns the established operator owner and grants runtime only
SELECT/INSERT on the new table. Table UPDATE/DELETE/ALTER/TAKE OWNERSHIP and UPDATE
on every one of its 26 columns are denied. The new effective permission verifier
requires the existing terminal/work/ingress chain, correct owner, SELECT/INSERT,
no UPDATE/DELETE/ALTER/CONTROL/TAKE OWNERSHIP and no effective writable column,
including future unknown columns. SQL hash/FK shape is no signature or authority
proof. Destructive Down is rejected; committed history requires reviewed forward
repair.

## Local evidence

All 3012 private Persistence tests pass: five new schema controls plus 3007
retained controls, zero failures/skips/compiler warnings. Actual EF model/FK
inspection and SQL160 parsing cover the expansion and effective permission SQL,
bounded physical columns, exact scopes, explicit predecessor shape, no cursor
mutation/backfill and all-column immutable rights. These keep the connection
closed and tracker empty. EF reports no pending model changes; nine affected C#
files are formatted.

The first broad run passed 3010 and failed two global coverage tests whose fixed
inventory and migration collection omitted the new table. They now include the
33rd Group entity and the new migration, retain every original tenant/company,
restrictive relation, owner and column-right assertion, and check the new Down
refusal. Focused 59 and the final complete 3012 run pass. No gate was removed.

Cold metadata/grammar tests do not execute physical CHECK/FK/column attacks. Those
require owned actual SQL probes for root and sequential successors, every partial
NULL shape, cross-scope/wrong terminal/predecessor/forks/UTC, MAC/carrier mutations
and all 26 UPDATE columns, then latest-leaf/cursor/restart/rotation/key-await races.
Actual schema qualification remains **NOT_RUN_UNQUALIFIED**.

Parent historical SQL reader `f1dc9c0` is independently source-approved in receipt
`6105976421`, ROOT full-read 7578 characters; actual historical SQL remains
unqualified. Parent remote `013a807` owns Build `38115318877`, native `114398953852`
and Governance `38115318868`; ROOT full .NET `114398954048` has 3538 tests passing.
Native 117/all8/quality/six-original-image closure is still owned and pending at
preparation. It excludes this expansion and historical reader. Real model
evaluation remains **NOT_RUN_UNEVALUATED** through the existing AI Gateway;
full #278/#279/#233 and merge acceptance remain incomplete.
