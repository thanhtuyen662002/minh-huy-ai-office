# ADR: durable automatic group batches and scoped SQL brain

Status: accepted design for issue278; implementation and runtime acceptance remain in progress on Draft288.
Base: main2e accepted ingress. Existing .NET/SQL Server/RabbitMQ and AI Gateway remain the delivery stack.

## Decision

Scheduling owns the same transaction-scoped source application lock as ingress. Its input is the committed source cursor, never broker arrival, an identity allocation or MAX(identity). It authorizes the trusted Extract service before scoped metadata access and again before commit. No user Generate/Accept/Approve/Send is a prerequisite.

Persist an immutable allocation receipt and the complete ordered raw revision ledger in one SQL transaction with ScheduledThroughSequence. A trusted scheduler operation nonce resolves lost commit replies to the original batch and ledger. Each allocation contains at most500 raw revisions and100 message IDs, reads at most501 candidate rows including a validated sentinel, and stops at a historical/live boundary. Current-head context selection and model chunking are separate operations. Allocation never means extraction completion.

Compute eligibility from durable pending anchors using the configured quiet period and maximum wait. When a bounded prefix leaves a suffix, anchor that suffix to its first original committed revision timestamp and retain the latest durable pending timestamp, including the existing final-ingress-proof latency. Do not restart maximum wait at allocation time. Clear anchors only when the allocated cursor catches the locked committed cursor. Pending backlogs and receipts survive process replacement.

Registry snapshots include service/credential epoch, Extract grant version, source version/deletion generation and account version. Raw ledger entries retain their original revision versions and digest. Old-generation revisions can be allocated as metadata under current authority; they cannot become current plaintext/model context without a later explicit dependency check and disposition. A changed current registry is always re-authorized; replay returns an original metadata receipt rather than inventing new work.

The following stages are required next and are not established by an allocation receipt: current fenced claims; immutable chunk/dependency manifests; scoped source/current notes/glossary reader; qualified tokenizer/profile; purpose-separated protected note payloads; evidence validation; atomic notes/dispositions/completion/outbox; contiguous terminal frontier; and post-recording issued-session IT APIs/UI. Completion must recheck every contributing source/note/glossary version, rather than compare the unrelated global ingest cursor. No-work, quarantine, media attention, model failure and zero-message coverage gaps have distinct durable outcomes.

Historical content is context, not a live notification trigger. Mixed context needs per-note live provenance. Customer facts, AI interpretation and IT-confirmed state are separate; model text cannot assign IT state, SLA, destination or authority. Reader/audience does not imply editor. A later internal-only reporter consumes committed references in issue279.

## Schema and permissions

Use an additive EF migration. Allocation and raw ledger are append-only, operator-owned tables with full tenant/company/source composite keys and restrictive foreign keys. Runtime may SELECT/INSERT, never mutate or delete these receipts. Existing SourceStates retains only its reviewed mutable columns. New runtime proves the extended effective rights before operating; old runtime remains compatible with the additive tables. Down migration refuses destructive evidence rollback.

## Verification

Focused controls cover durable timing, bounded multi-allocation coverage, mixed history/live, replay, authority revocation/restoration, original-receipt corruption, clean tracker and cancellation. Model metadata and migration SQL checks are mechanical controls. Native SQL concurrency/rollback/lost commit reply/process replacement and least-privilege controls must run in the owned ephemeral CI stack before runtime acceptance. InMemory controls do not prove SQL transactions or races. Actual model evaluation remains separate and pending configured operator provider access.

## Fenced claim implementation checkpoint

Persist an immutable acquisition receipt and a mutable lease state keyed by full source/batch scope. Under the same source lock, reconcile the original nonce before competing new work; fixed original lifetime never renews. An active lease refuses new nonce acquisition even for the same owner. Actual expiry advances a checked monotone epoch. Expiry/replacement never means completed extraction.

Only an internal constructor can create a verified handle from the original receipt and captured full current Extract authority. Retain only a purpose-versioned exact authority digest in SQL. Current registry changes can preserve original metadata while yielding no handle; current revocation denies reconciliation. Protected readers/effect stores must establish the live handle inside their own source-locked SQL transaction and again at final effects. Comparing an unrelated global ingest cursor is not a dependency fence.

The claim expansion gives runtime INSERT/SELECT-only receipts and reviewed lease-field UPDATE only. No source keys, protected payload, portal user/task, provider calls, completion outcome or notes/outbox are introduced by claims. Local mechanical controls and guarded native proof source exist; native claim execution and full automatic brain pipeline remain required.

Independent frozen88cb review reproduced a cross-call clock-rollback defect: expiry denial/replay did not retain a durable witness, allowing the same epoch to become current again. The additive expiry fence retains the first committed UTC expiry observation within each epoch; replay keeps original receipt/null, backward time cannot reacquire before that witness, and only a new epoch clears it. Public metadata fencing commits witness-only retirement before denial. Internal fencing now returns a typed expired verdict without allocation capability; it cannot imply that a rolled-back caller transaction preserved retirement. The first protected/result consumer must own a source-lock/savepoint wrapper that rolls back all staged effects before witness-only commit, with native SQL/MARS failure controls. This wrapper is not implemented or proven by the claim checkpoint.

## Source reader boundary

Use a sealed claim and host-selected IDs from the immutable allocation to construct scoped private source context. Source reads retain cutoff head precedence and exact original receipts/envelope; current contributing dependencies are fenced around scoped key awaits outside SQL. No portal identity or model-selected SQL is accepted. Initial/final expiry in this fixed read-only unit persists only the lease witness before denial. A later note-effect transaction must separately own source-lock-before-savepoint rollback/retirement and fail closed where savepoints/MARS cannot support it. Configuration and provider calls continue through the existing worker AI Gateway environment loader and deployment injection; no new client/secret store is introduced.

## Quarantine and source preparation

Use versioned, bounded host quarantine before source candidate selection and on decoded structured output before grounding/persistence. Keep original protected source intact; only metadata reasons and original references may enter quarantine attention. Inspect a separate normalized/control-filtered view without changing original UTF16 evidence. The known-form NoMatch result is not proof of arbitrary secret absence. Preparation exposes host evidence IDs/text without raw external identifiers and preserves every selected item's host disposition; it does not establish complete raw coverage, model token budget or future release/effect authority.

## Grounded proposal boundary

Interpret structured output only against the sealed host preparation: exact decoded closed shapes, current candidate GUID/revision and original literal evidence quotes, complete candidate dispositions, finite note/reference/field/output bounds and immutable private results. Reuse the existing StructuredGeneration Gateway schema; add no independent provider/config. Model output has no IT status/SLA/assignee/destination/scope authority. Customer deadline text requires literal evidence and is never a business commitment; relation text is only an unverified hint. Titles/problems/outcomes remain AI interpretations, and exact quote matching cannot establish their semantic truth. Qualified token/context assembly, current same-group notes/glossary, exact release/effect fences, protected SQL persistence and frozen real-model evaluation remain separate required gates.

## Private brain content protection

Request and glossary revision payloads use a versioned brain AES256-GCM purpose distinct from original source protection. Authenticate tenant/company/group, kind, record/revision, source version/deletion generation and key ID; bound strict UTF8 before encryption and zero temporary private bytes. Reuse the existing scoped source-key provider through authorized stores, with resolution outside SQL and exact dependency/claim checks before release or effects. The primitive itself grants no authority and introduces no provider/client/secret configuration. Durable tables/migrations, exact original evidence metadata, store-owned staged-effect rollback/expiry witness retirement and native proof remain separate required implementation.

## Scoped work note SQL expansion

Version the ten-table request/revision/evidence/chunk receipt/selected disposition/NotesCommitted outbox/item/editor/glossary expansion. Separate immutable protected interpretations and exact source evidence from mutable IT-confirmed status/SLA/assignee; new unconfirmed records cannot silently acquire those business commitments. Request/glossary current heads have no circular reverse FK: the store must atomically validate the exact immutable chain. Selected chunk completion does not imply complete raw allocation coverage or a terminal frontier.

Freeze operator registry ownership and literal runtime permissions in the migration, rechecking effective column rights through the existing runtime proof. Reader/audience is not Editor; runtime cannot publish or enable glossary. Outbox contains metadata/exact revision references only and grants no delivery authority. Require actual owned SQL escalation/refusal/exact-restore positives before native schema acceptance. First note-effect consumer still owns source-lock-before-savepoint rollback of already flushed effects and witness-only expiry retirement with native unsupported-savepoint/MARS controls. See `docs/validation/GROUP_WORK_NOTE_SCHEMA.md` for checkpoint limits.
