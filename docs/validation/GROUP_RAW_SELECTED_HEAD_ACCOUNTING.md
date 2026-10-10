# Selected-head raw allocation accounting — issue278 / Draft288

## Implemented boundary

`GroupWorkRawDispositions` records immutable metadata for every allocated raw
revision of each message selected by an automatic note or automatic NoWork plan.
The freshly fenced complete allocation is dense, scoped, and bounded to500 rows.
Unselected message IDs receive no row. A row links the original allocated
sequence/revision to the selected head revision, sealed outcome and operation.
All11 fields are checked against the recomputed expected rows on original replay,
ordered by sequence with a501 sentinel. No source text, secret, digest or model
output is stored in this ledger.

Raw rows are staged with selected dispositions, the immutable work receipt and,
when present, protected notes/evidence/outbox in the existing SAME transaction
and effect savepoint. They participate in the existing detach/rollback path.
Legacy proposal entry paths retain their behavior and add no raw rows.

Relation1 means the raw revision equals the selected head. Relation2 means it is
different. Selection can prefer a Recall or a cutoff-compatible head, so relation2
does not imply the selected revision number is larger. Accounting does not say
each raw body reached the model, or every allocated message has been processed.

## Additive schema and immutable rights

Versioned migration `20261010213659_AddGroupWorkRawAccounting` creates one table,
four indexes and its explicit permission batch. The scoped PK includes batch and
committed sequence. Four restrictive scoped FKs require an allocated row,
selected disposition, batch/operation receipt and original message revision.
The sealed factory and replay also enforce exact copied head/outcome values;
those values are not physically equated by the selected-disposition FK.

Positive sequence/revisions and closed outcome/relation checks are enforced.
The operator retains object ownership; the runtime has SELECT/INSERT and denied
UPDATE/DELETE/ALTER/TAKE OWNERSHIP, including UPDATE on every column. The runtime
permission verifier includes this table. No existing table is altered. Down is
deliberately unsupported; any production correction requires a forward migration.

## Local evidence

- 29 new focused cases PASS, zero failed/skipped. Two use actual existing InMemory
 ingress/allocation/source-read paths for500 and501 revisions: revision501 stays
 unallocated;499 superseded rows and the500th selected head are accounted.
- One selected-message history plus an unselected message proves selective raw
 accounting. 11 malformed allocation/selection cases refuse without effects.
- 12 original-replay corruption/missing-row controls refuse every changed field
 after a positive InMemory replay. Operator corruption is simulated; this is not
 SQL runtime write permission evidence.
- Three model/migration controls require all scoped restrictive FKs, metadata-only
 fields, closed checks, SQL160 forward parsing, actual runtime permission proof,
 no old-table mutation and no pending model change; no database connection.
- Full Persistence2189 PASS, zero failed/skipped; full Release solution0 warnings,
 0 errors; changed CSharp whitespace verification and `git diff --check` PASS.

The maximum fixture initially repeated NewText, correctly violating existing
ingress. It was repaired to one NewText followed by Edits; production ingress was
unchanged. Two existing schema gates now include the30th Group table/new migration
while retaining all original scope/owner/column-denial assertions.

## Remaining required evidence

Native SQL accounting, same-transaction partial-effect refusal, postflush rollback,
original full-graph replay, runtime immutable rights, physical FK checks, max500,
claim/key/source/editor/current-authority races are NOT_RUN_UNQUALIFIED. Existing
owned profiles must retain every prior predicate/count and add explicit raw-table
observations before this shipping change can be qualified. Exact parent30a108 CI
is running separately and must close before a later push.

Whole-batch terminal receipts and a contiguous terminal frontier are not added.
They require complete raw coverage plus fresh validation of every contributing
source/note/glossary dependency. Selected count or ScheduledThroughSequence cannot
prove completion. Honest gaps/backlog/zero-message carrier, bounded retry truth,
qualified context/tokenizer, automatic DI/API/UI and issue279 remain incomplete.
Real-model evaluation is NOT_RUN_UNEVALUATED; existing AI Gateway/config are reused.
No full278/279/233, production or merge acceptance is claimed.
