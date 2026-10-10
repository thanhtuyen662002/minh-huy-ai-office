# Durable raw batch allocation checkpoints — issue278 / Draft288

The earlier202d935 checkpoint is a metadata planning primitive for the subsequent SQL scheduler. It does not advance a SQL cursor, authorize source reads, freeze current message heads, call a model, complete extraction or create notes/events. The remaining durable store/migration/production-DI/native proof is required on this same issue/PR.

GroupBatchAllocationPrefix requires the exact ordered committed SQL prefix after the scheduled cursor: at most501 rows, including one sentinel when more than500 are available. Every candidate must match the trusted scope, expected contiguous sequence, unique message/revision, UTC metadata, event kind and canonical digest. Missing/gapped/out-of-order/cross-scope/duplicate/corrupt rows fail closed; the planner never sorts corruption into apparent progress.

The selected raw interval stops before exceeding500 revisions or100 distinct message IDs, or before a historical/live boundary. The unallocated first row is retained and the observed committed end is distinct from the allocated end. Immutable copies prevent caller mutation. Raw rows are not current-head model inputs: a later scoped SQL query must choose exact winners as of cutoff and maintain every raw disposition. Historical prior context is allowed later only under explicit authority/provenance; splitting this raw interval does not assert per-note live-trigger proof.

SQL integration must use the existing transaction-owned per-source ingest lock, freshly qualified Extract authority and effective least-privilege permissions. Persist the entire allocation ledger/receipt and ScheduledThrough atomically, retain an independently contiguous terminal frontier and reanchor the unscheduled suffix from actual durable data. Inbox ACK is not batch progress. Read keys/model outside short SQL transactions and fence current authority/dependencies at final note commit.

## Local evidence

13 focused contract controls PASS:500/501 revisions on one message; stop before101st message while preserving previous edits; historical/live boundaries; iterative exact-once coverage of1200 revisions with bounded reads; corruption/missing sentinel/cross-scope/non-UTC refusal; immutable copying and near-long-max arithmetic. Pure synthetic metadata evidence only, no SQL/runtime/model claims.

## SQL allocation checkpoint after reviewed22677

Implemented locally: GroupBatchAllocationStore, immutable allocation/complete raw ledger tables, additive AddGroupBatchAllocation migration and extended runtime effective-rights proof. Ingress and scheduler use the same extracted transaction-owned source lock with the unchanged resource key. The scheduler re-resolves current Extract authority before metadata and at commit; no source ciphertext/key/model/portal task is read or created.

The trusted operation nonce returns the original SQL receipt before looking for new work. Fresh allocations select at most501 metadata rows, validate every original ingress receipt and sentinel, reserve at most500 raw revisions/100 IDs in one historical or live interval, and atomically persist parent/ledger/ScheduledThrough. Replay checks the immutable ledger against original revisions/receipts and current source state. The suffix first anchor is its original committed timestamp; the last anchor retains ingress final-proof latency. Caught-up state alone clears both anchors. Allocation is not a current-head freeze or completion.

83 focused store/model/retained ingress/native-guard controls PASS0skip, including35 new store/model controls: durable30s/120s eligibility,1200 revisions across500/500/200 batches,100-ID and historical boundaries, exact suffix reanchor with later traffic, fresh-context100 replays and new unrelated ingress, current authority/late version fencing, corrupt sentinel/original graph, old-generation metadata-only allocation, immutable return and clean trackers.44 existing Python owned-proof guard controls PASS. EF migration is generated from the model and freezes operator ownership/SELECT-INSERT-only receipts with immutable column DENY and forward-only repair.

Added owned native SQL proof modes for allocation rollback/current revoke/effective-column privilege refusal, four concurrent fresh creators, commit before lost receipt/owned child death, fresh process100 concurrent original replays, no portal/key/model/note effects and session restoration. These modes are UNEXECUTED on the new SQL checkpoint until its approved exact-head hosted CI runs. InMemory/model/static guard checks do not prove native SQL transactions, rollback or concurrency.

Pushed transport22677 has scoped independent approval6097021504. Build38048764985 attempt1 failed in managed Docker image preparation before actual runtime. Attempt2 is active after one retry; Gov38048764960 PASS. This evidence is for22677 transport, not the new SQL work. Actual model evaluation remains UNEVALUATED.

Next: freeze/review the SQL checkpoint, push only after scoped review and close its exact CI. Then implement fenced claims, scoped current-head/brain/context, automatic note commit/outbox and production DI/no-click proof. Issue278 remains open; no merge or full acceptance is asserted.
