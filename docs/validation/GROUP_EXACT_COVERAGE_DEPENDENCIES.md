# Exact group coverage dependency snapshot

The private source context previously retained only `HasCoverageGap`. A new
source/account gap, changed reason/time or source reconnection could leave that
boolean true and pass the later source fence. The reader now captures both kinds
within its existing fenced transaction, compares exact metadata before private
decrypt/release and retains it for the locked note-effect dependency check.

Source dependencies include ID, after-sequence, reason, opened and reconnected
timestamps. Account dependencies include listener epoch, the existing closed
SQL reason and opened/recorded timestamps. Selection is scoped by tenant/company
and source or authorized connector account, with unique deterministic ordering.
Each kind reads at most257 rows, accepts the complete maximum256 and refuses the
overflow sentinel. Malformed metadata fails before key lookup. The public
conservative boolean is computed from the private snapshot; neither reason nor
the snapshot becomes model input or permission to close gaps.

26 new local controls exercise exact changes while the boolean remains true,
gap addition during key resolution, material disposal, max256/overflow257 for
both kinds, foreign scope exclusion and malformed fields. The existing account
gap fixture is corrected to the already accepted SQL reason `listener-expired`.
All319 source-reader controls and all2160 persistence tests pass with zero
failures/skips. Changed-file formatting and the full Release solution build pass
with zero warnings/errors. These InMemory controls do not establish native SQL
isolation, concurrent mutation or production runtime acceptance.

No migration, mutable right, AI client, provider configuration, note/outbox,
terminal frontier or gap-completion authority is added. Native gap mutation and
the full automatic pipeline still need qualification. Current automatic stores
continue to deny gaps. The owner-required received-work plus honest coverage
warning, separate durable zero-message gap carrier and bounded gap backlog
handling remain mandatory; this snapshot limit is not full gap acceptance.

The separate remote280 proof checkpoint remains under exact CI107; these local
source changes are excluded until their own frozen review and renewed native
qualification. The original570 failure stays recorded as failed evidence, and
real model evaluation stays NOT_RUN_UNEVALUATED.
