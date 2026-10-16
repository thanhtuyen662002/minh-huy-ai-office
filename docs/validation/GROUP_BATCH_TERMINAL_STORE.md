# Owned terminal receipt consumer — issue 278 / Draft PR 288

## Source scope

`GroupBatchTerminalStore.CommitAsync` owns a finite two-minute Serializable SQL
unit. It uses the same trusted extraction worker, context, clock, source reader
and brain reader; caller transactions, ambient transactions, dirty tracking,
MARS, mismatched readers and foreign workers are refused before opening SQL.
No API, worker DI or automatic application path is added in this checkpoint.

The initial whole-batch read takes the transaction-owned source APPLOCK before
the effect savepoint. Every original acquisition, expected effect graph, raw and
selected disposition, source dependency, selected brain dependency and
same-batch own-input prerequisite is reconstructed under current Extract
authority. Terminal permissions additionally chain the complete existing proof
and require the new append-only receipt/cursor rights.

The consumer reads original acquisition metadata using the complete scope,
batch and operation with a two-row refusal bound. The new receipt proof binds
all receipt scope/count/range/version fields, manifest bytes/digest, original
claim operation/owner/epoch and all service/authority versions. Original commit
UTC must lie inside that original acquisition's lifetime, follow every
contributor and not exceed current observed UTC. A current live lease and the
same authority digest are required too. All original contributor claims must
match the sealed effect set; actual enumeration is bounded to100.

The fixed terminal receipt is staged inside the effect savepoint and saved to
SQL. The complete whole-batch reader is then repeated, original manifest bytes
must remain equal, and current permissions/claim/clock are checked again before
transaction commit. The actual stored terminal row is reread with no tracking;
its provenance, manifest and exact prepared commit UTC must match, and the
returned result comes from that validated row. A well-shaped replay carrier or
the tracked object's contents alone cannot prove this write. The store does not manufacture notes or another
`NotesCommitted` outbox. Existing note publication remains separate.

## Expiry and replay

Every store clock observation follows one monotonic floor. An observed expired
lease, including expiry followed by wall-clock rollback, enters the existing
claim retirement path. If effects reached SQL, the savepoint is rolled back and
all staged entries are detached first. Only then may the expiry witness be
persisted and committed. Authority or permission failure causes full transaction
rollback. Caller cancellation remains cancellation. Staged entities are detached
in the final cleanup; no caller-owned entity is added to that list.

Replay selects scoped receipts matching either the batch or operation with
`TOP 3`, refuses collisions and requires the exact batch/terminal operation.
It authenticates the original terminal acquisition and unchanged complete graph
under a current live lease. A later acquisition epoch can replay the same
original receipt; it cannot restage it using a substituted original acquisition.
The returned DTO contains completion metadata and grants no future write/send
capability. Digests or well-shaped rows alone do not authorize insertion.

## Local controls and limits

353 selected tests pass in the private frozen-parent prototype and the exact
promoted shared sources:49 new receipt/entry controls plus304 retained
own-input/coverage/effect/reader/manifest controls, zero failures/skips.
Four changed C# whitespace checks pass. Cases include complete100-selected/
500-raw/five-contributor zero-note shape, detached manifest bytes, all receipt
scope/count/authority substitutions, recomputed malformed carriers, original
and live expiry/time limits, later-lease replay, missing contributor graphs and
claims, exact written-row UTC/manifest beyond valid replay provenance,
actual bounded/scoped replay SQL generation and unsafe entry refusal.
Cold EF SQL parses with SQL160 and leaves the connection closed.

One initial guard-test assertion expected `InvalidOperationException` for a
foreign worker; the retained authorization policy correctly returned
`UnauthorizedAccessException`. Only that assertion was corrected, keeping the
exact expected authorization exception. Final verification has no warnings.

These controls do **not** execute a terminal INSERT, transaction/savepoint
rollback, runtime permission proof, replay race or expiry witness on SQL Server.
Those are **NOT_RUN_UNQUALIFIED** and require fresh native adversarial proof.
The terminal frontier is not advanced here. Authenticated contiguous SQL cursor,
gap/carrier/retry handling, nonempty-brain/multiple-contributor chains, automatic
DI/API/UI and full278/279/233 acceptance remain incomplete. Actual model
evaluation remains **NOT_RUN_UNEVALUATED**; only the existing AI Gateway/provider
configuration may be reused. This checkpoint is not merge or production
acceptance.
