# Restored batch SQL proof checkpoint

## Scope

Adds two owned runtime phases after every original-admission, corruption,
permission, accepted-guard and retained-expiry oracle has completed. It uses
the existing synthetic accepted output; it does not evaluate a model.

`restored-batch-commit` reopens the exact epoch-four claim left by the expiry
proof and invokes the fixed `GroupClaimedBatchProcessor` composition. The
actual stored original is restored, the accepted result is read before any
admission factory, and the existing independent SQL stores own note/evidence,
NotesCommitted, terminal and signed frontier writes. The proof requires one
note/revision/evidence/work/outbox/item, two selected/raw dispositions, and one
terminal/advance/enrolled key/cursor. It preserves original plans, sidecars,
attempts, accepted results, failures and claim receipts/state.

`restored-batch-recover` is a separate executable invocation. It reconstructs
the same claim handle, then moves its owned clock past claim expiry. With
SELECT denied on original plans, sidecars and admission companions, the actual
terminal-first recovery must succeed without original/accepted-result reads
or initializing the existing admission factory. Complete note, terminal,
key, signed advance and cursor graphs must remain byte-identical.

The parent checks every retained scope and relevant permission row, restores
all three SELECT permissions even on failure, and compares complete owned
graphs after each independent container/image cleanup. Its aggregate marker
is emitted only after both cleanup checks. The old original-admission oracles
finish before adding any note; none is replaced by the new completion check.
The runtime records only closed mode/phase diagnostics and fixed PASS lines.

## Verification and limits

Local build: zero warnings/errors. Full .NET: 5,350 tests
`[6,584,25,4735]`. Ten inert driver controls include missing effects, original
mutation, private output, failed completion/recovery child and changed signed
history; these controls do not execute SQL. Eight actual executable guard
cases for the two modes refuse before stdin/configuration/SQL when an owned
flag is missing. Final format verification is required before checkpointing.

**Actual SQL for these two phases: NOT_RUN_UNQUALIFIED.** A future exact-head
CI capture must bind this complete source and include the prior 144 required
markers plus the new restored-batch aggregate. The previous remote e8 run
cannot qualify this source. Its active CI lease remains owned independently.

The independently reviewed integration `3f44daf` has bounded source/durable
clearance (review6198638748, receipt68ada769). That review covers sources24–30
against repair31; it does not cover this new proof or newer state documents.

This proof covers an existing immutable original with an empty selected brain
and one accepted model chunk. First host/model original creation, multiple
chunks, own-history invalidation, source edit/recall, durable invalidated-original
continuation, claim scanner, shipped Worker DI, queue/restart/no-click and
internal IT notification still need implementation or separate positive SQL
evidence. Preserve all current source/brain/claim/permission refusals.

The production released catalog stays empty. Real model evaluation remains
NOT_RUN_UNEVALUATED. No new AI client/provider configuration, customer output,
ERP write, production activation, issue completion or main merge is implied.
