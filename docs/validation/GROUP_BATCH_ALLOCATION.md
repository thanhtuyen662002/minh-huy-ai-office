# Raw batch allocation foundation — issue278 / Draft288

This checkpoint is a metadata planning primitive for the subsequent SQL scheduler. It does not advance a SQL cursor, authorize source reads, freeze current message heads, call a model, complete extraction or create notes/events. The remaining durable store/migration/production-DI/native proof is required on this same issue/PR.

GroupBatchAllocationPrefix requires the exact ordered committed SQL prefix after the scheduled cursor: at most501 rows, including one sentinel when more than500 are available. Every candidate must match the trusted scope, expected contiguous sequence, unique message/revision, UTC metadata, event kind and canonical digest. Missing/gapped/out-of-order/cross-scope/duplicate/corrupt rows fail closed; the planner never sorts corruption into apparent progress.

The selected raw interval stops before exceeding500 revisions or100 distinct message IDs, or before a historical/live boundary. The unallocated first row is retained and the observed committed end is distinct from the allocated end. Immutable copies prevent caller mutation. Raw rows are not current-head model inputs: a later scoped SQL query must choose exact winners as of cutoff and maintain every raw disposition. Historical prior context is allowed later only under explicit authority/provenance; splitting this raw interval does not assert per-note live-trigger proof.

SQL integration must use the existing transaction-owned per-source ingest lock, freshly qualified Extract authority and effective least-privilege permissions. Persist the entire allocation ledger/receipt and ScheduledThrough atomically, retain an independently contiguous terminal frontier and reanchor the unscheduled suffix from actual durable data. Inbox ACK is not batch progress. Read keys/model outside short SQL transactions and fence current authority/dependencies at final note commit.

## Local evidence

13 focused contract controls PASS:500/501 revisions on one message; stop before101st message while preserving previous edits; historical/live boundaries; iterative exact-once coverage of1200 revisions with bounded reads; corruption/missing sentinel/cross-scope/non-UTC refusal; immutable copying and near-long-max arithmetic. Pure synthetic metadata evidence only, no SQL/runtime/model claims.

Next: freeze/review this exact primitive before push, then integrate versioned durable allocation stores and claims plus native SQL concurrency/rollback/restart proof. Do not count the primitive as issue278 acceptance.
