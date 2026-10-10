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

### Host observations and private attention payloads

Use a separate closed `group-brain-host-attention-v1` private metadata payload for unsupported media, versioned secret quarantine and exhausted extraction. It has exact source revision refs and a coverage-gap observation, without source quotes, model/exception text or any commitment. Parsing is format validation only. A fixed host consumer must fence original source dispositions/current authority and establish bounded retry exhaustion before writing.

Extend revision origin/verification through a later versioned expand migration with an explicit HostAttention/HostObserved pair. Existing AI and IT pairs remain exact; readers must understand the added pair before new writes activate. Host attention never claims AI understood a media body or IT confirmed a business state. Current primitive preparation neither changes the schema nor admits that new pair.

Partial listener gaps must not stall received known work: record valid interpretations with honest incomplete coverage, and retain gap attention under its own durable identity. Do not terminally discard known text to produce only a gap warning. Media captions and unknown media need separate provenance; zero-message gaps do not invent message IDs. These combined commit/completion semantics remain to be implemented and natively qualified after this payload preparation.

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

## Current private brain read boundary

Use a sealed live Extract claim to read only bounded host-selected exact-group current request/glossary revisions. Snapshot current heads, business metadata, glossary publication/enablement, exact evidence and current winning source metadata; reject changed/recalled contributors. Resolve scoped keys outside SQL, then refence every contributing dependency before decrypt and final private return. Empty selection still requires authority. The fixed read-only unit can commit witness-only expiry retirement; it cannot establish staged-effect rollback or future release authority.

Returned private payloads remain opaque. A qualified context must decode the closed format, quarantine, select/budget bounded relevant same-group work and pin complete selection dependencies before release through the existing Gateway. An explicit selected-set reader does not prove relevance/completeness, semantic grounding or a real model result. See `docs/validation/GROUP_BRAIN_CURRENT_READER.md`.


### Reader bound and final verdict ordering

Bound the actual copied selection with per-list overflow sentinels and reject a combined size above20 before SQL or keys, independently of caller Count. In each read unit perform the effective work permission proof before computing the final live-claim verdict. A permission await cannot reuse a verdict computed before its completion; observed expiry remains witness-only retirement followed by refusal. Retain independent counterexamples and qualify actual SQL reads separately.

### Host observation provenance expansion

Keep host metadata attention distinct from source-backed AI interpretations and IT edits through the explicit3/3 HostAttention/HostObserved revision pair. Expand only the origin check while preserving the old1/1 and2/2 clauses and existing effective permissions. Readers expose request provenance separately from business confirmation, require an unconfirmed attention head and metadata evidence, and match a closed protected host payload to exact evidence refs/reason. Preserve legacy opaque AI/glossary content; qualified model context remains a separate release boundary. Freeze actual selected dependencies through direct bounded enumeration to avoid IList size fast paths. Do not enable host writes until fixed consumer authority and native persistence qualification exist. See `docs/validation/GROUP_HOST_ATTENTION_READER.md`.

### Exact coverage dependency snapshot

The source reader's initial private context must retain both source and account
coverage metadata. A boolean cannot detect a second gap or a reconnection when
coverage is already incomplete. Compare the exact scoped snapshots around key
awaits, at context release and within the locked effect dependency fence. Read
both kinds independently; retain all fields and deterministic unique ordering.
Bound each kind to256 rows plus one overflow sentinel and refuse malformed or
overflowed dependency sets before keys, rather than release a partial snapshot.

This is read-boundary hardening, without new permissions, schema, providers or
gap-completion authority. Existing automatic consumers continue to refuse gaps
until the required durable gap carrier and honest incomplete-coverage workflow
are implemented. That later workflow must also resolve bounded gap backlog
selection; permanent overflow refusal is not acceptance for received known work.
Zero-message gap attention, raw completion/frontier and native SQL mutation
qualification remain required. See `docs/validation/GROUP_EXACT_COVERAGE_DEPENDENCIES.md`.

### Immutable selected-head raw accounting

Automatic note and automatic NoWork transactions record metadata-only immutable
links from every allocated raw revision of each selected message to its sealed
selected head/outcome and operation. Validate the full freshly fenced dense
allocation (at most500) and replay every original field with a501 sentinel.
Unselected messages remain unaccounted. Use the existing transaction/savepoint;
raw links must roll back with receipts, notes and outbox on final fence failure.
Legacy entry paths do not add raw links. The additive scoped table has restrictive
FKs and effective runtime SELECT/INSERT-only rights, including column UPDATE denial.

Different-from-selected-head accounting does not assert every raw body was model
input or that a batch is terminal. Whole-batch completion/frontier requires all
raw coverage and current validation of every contributing source/note/glossary
dependency. Native atomicity/replay/permission/capacity/race qualification remains
required. See `docs/validation/GROUP_RAW_SELECTED_HEAD_ACCOUNTING.md`.

### Immutable committed chunk dependencies

Expand the original work receipt with versioned bounded metadata of every
explicit contributing source/current-note/glossary reader dependency. Fresh
automatic effects stage the complete format1 bytes in the same receipt/savepoint,
and replay compares those exact original bytes. Existing legacy version0/null
receipts remain replayable; missing manifests never qualify terminal advancement.
Keep all existing current-authority/source/brain/expiry fences and immutable
effective column permissions. Store only scoped identities/revisions and exact
metadata/cipher fingerprints, with no decrypted source/brain body or credential.

Source and brain reconstruction checks must run inside the future consumer's
owned serializable SQL unit under a current sealed claim. They return expiry
observations without writing/committing; the effect consumer owns rollback and
witness retirement. This checkpoint enables no terminal/frontier mutation. Full
raw coverage, every committed chunk's contributors, selection completeness and
provider/tokenizer/context/retry/gap/automatic pipeline qualification remain
separate requirements. See `docs/validation/GROUP_WORK_DEPENDENCY_MANIFEST.md`.
