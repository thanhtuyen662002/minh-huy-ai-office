# Sealed automatic no-note dispositions

`GroupNoWorkCommitStore.CommitAutomaticAsync` consumes the same sealed
`GroupAutomaticNotePlan` used by the combined note writer, only when the plan
has no AI or host notes. It retains one typed disposition for each selected
source: NoWork, Recalled, ObsoleteGeneration or ChangedAfterCutoff. Empty plain
text does not require invented model output. Model-eligible text still requires
the parser's exact preparation/proposal association and explicit NoWork verdict.

The original public `CommitAsync` input guards are retained: all selected input
is model text, no media/gap, zero proposal notes and all NoWork verdicts. Both
entries use the existing fixed atomic receipt/disposition engine. The automatic
entry checks exact same current brain/claim receipt, 1–100 selected sources,
complete unique source/revision matching and no host-attention/work outcome.
Media, quarantine or work must use the note writer. Gaps still refuse until a
durable coverage carrier is implemented.

The current-authority/source/brain fences, source transaction lock before the
effect savepoint, staged-effect rollback, exact witness-only retirement, clean
detach, monotone clock checks and duplicate-source refusal remain in the shared
engine. Replay checks every original source revision AND typed outcome; it
cannot silently replace a recalled/obsolete/changed disposition with NoWork.
No note, NotesCommitted, business assignment or IT state is created by this
entry. It does not declare the raw allocation or runtime job completed.

## Evidence

19 new actual-entry controls plus 8 retained legacy entry controls pass with
zero failures/skips. Positive sealed greeting/empty/recall/obsolete/changed
preparations reach exactly one deliberately refused SQL connection before any
effect or witness. Invalid storage provider, MARS, operation, notes/attention,
gap, foreign worker/dependencies, cancellation, dirty tracker and ambient
transaction deny at the earlier gate. Counts, keys, tracker and all seven effect
tables remain unchanged. These controls do not execute automatic SQL commit.

The full persistence suite passes 2,134 tests with zero failures/skips. Changed
C# format checks pass. Independent frozen review and a future native
automatic no-note transaction/replay/expiry/current-authority proof remain
required. Retained standalone NoWork runtime evidence establishes that original
entry's behavior only; it cannot qualify this new entry by itself. No schema or
gateway/provider configuration is introduced.

Issue278, model evaluation, UI/DI/retry/coverage/full-product delivery remain
active and incomplete. Real-model quality is `NOT_RUN_UNEVALUATED`.
