# ADR: durable automatic group batches and scoped SQL brain

Status: accepted design for issue278; implementation and runtime acceptance remain in progress on Draft288.
Base: main2e accepted ingress. Existing .NET/SQL Server/RabbitMQ and AI Gateway remain the delivery stack.

## Decision

Scheduling owns the same transaction-scoped source application lock as ingress. Its input is the committed source cursor, never broker arrival, an identity allocation or MAX(identity). It authorizes the trusted Extract service before scoped metadata access and again before commit. No user Generate/Accept/Approve/Send is a prerequisite.

Persist an immutable allocation receipt and the complete ordered raw revision ledger in one SQL transaction with ScheduledThroughSequence. A trusted scheduler operation nonce resolves lost commit replies to the original batch and ledger. Each allocation contains at most500 raw revisions and100 message IDs, reads at most501 candidate rows including a validated sentinel, and stops at a historical/live boundary. Current-head context selection and model chunking are separate operations. Allocation never means extraction completion.

Compute eligibility from durable pending anchors using the configured quiet period and maximum wait. When a bounded prefix leaves a suffix, anchor that suffix to its first original committed revision timestamp and retain the latest durable pending timestamp, including the existing final-ingress-proof latency. Do not restart maximum wait at allocation time. Clear anchors only when the allocated cursor catches the locked committed cursor. Pending backlogs and receipts survive process replacement.

Registry snapshots include service/credential epoch, Extract grant version, source version/deletion generation and account version. Raw ledger entries retain their original revision versions and digest. Old-generation revisions can be allocated as metadata under current authority; they cannot become current plaintext/model context without a later explicit dependency check and disposition. A changed current registry is always re-authorized; replay returns an original metadata receipt rather than inventing new work.

The following stages are required next and are not implemented by an allocation receipt: fenced expiring worker claims; immutable chunk/dependency manifests; scoped source/current notes/glossary reader; qualified tokenizer/profile; purpose-separated protected note payloads; evidence validation; atomic notes/dispositions/completion/outbox; contiguous terminal frontier; and post-recording issued-session IT APIs/UI. Completion must recheck every contributing source/note/glossary version, rather than compare the unrelated global ingest cursor. No-work, quarantine, media attention, model failure and zero-message coverage gaps have distinct durable outcomes.

Historical content is context, not a live notification trigger. Mixed context needs per-note live provenance. Customer facts, AI interpretation and IT-confirmed state are separate; model text cannot assign IT state, SLA, destination or authority. Reader/audience does not imply editor. A later internal-only reporter consumes committed references in issue279.

## Schema and permissions

Use an additive EF migration. Allocation and raw ledger are append-only, operator-owned tables with full tenant/company/source composite keys and restrictive foreign keys. Runtime may SELECT/INSERT, never mutate or delete these receipts. Existing SourceStates retains only its reviewed mutable columns. New runtime proves the extended effective rights before operating; old runtime remains compatible with the additive tables. Down migration refuses destructive evidence rollback.

## Verification

Focused controls cover durable timing, bounded multi-allocation coverage, mixed history/live, replay, authority revocation/restoration, original-receipt corruption, clean tracker and cancellation. Model metadata and migration SQL checks are mechanical controls. Native SQL concurrency/rollback/lost commit reply/process replacement and least-privilege controls must run in the owned ephemeral CI stack before runtime acceptance. InMemory controls do not prove SQL transactions or races. Actual model evaluation remains separate and pending configured operator provider access.
