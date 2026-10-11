# Whole batch coverage prerequisite — issue 278 / Draft 288

## Scope

`GroupWholeBatchCoverage` is an internal, bounded structural prerequisite. It is not wired to a terminal consumer or frontier, opens no connection, and decrypts nothing. Passing this kernel does not establish current authorization, dependencies, SQL isolation, effect completeness or production completion.

The future terminal SQL unit must supply the freshly reconstructed original allocation, every contributing receipt, all selected dispositions and all raw dispositions from one caller-owned serializable transaction. The kernel requires a dense prefix of at most 500 raw revisions, at most 100 cutoff heads, and every contributing operation. It validates scoped batch/operation/cutoff identity, exact manifest source IDs/revisions, complete raw-to-selected links and known outcomes. Missing, duplicate, partial, legacy version0/null or foreign contributions fail closed. Parsed manifests own copied bytes and are returned in deterministic operation order for subsequent revalidation of **every** contributor.

Deletion generation zero is valid for the existing initial schema. The existing 40-note limit is **per automatic chunk**, not a whole-batch product cap. Whole results above40 remain intact; a future notifier must batch/backlog within its own200-item budget rather than discard notes. At most100 bounded receipts imply at most4000 aggregate note count, but this count is not proof that the effect rows exist.

## Local evidence and limits

40 focused C# controls PASS, zero failures/skips; two-file formatter verifies. The structural fixture contains500 revisions/100 heads/five20-head contributors and observed pending suffix501. It checks all contributors, copy isolation and order independence, per-chunk note bounds and38 adjacent omission/identity/relationship failures. Its manually constructed manifest digests are synthetic metadata, not actual source/brain fingerprints or native SQL evidence. Current native113 runs the separately reviewed shipping manifest observer and history admission repair on exact5fc; this later structural kernel is not in that run.

## Required next work

Before terminal advancement: bounded actual SQL queries and effective permissions; fresh authority and every chunk's complete original source/current-note/glossary/coverage fingerprint under locks; explicit input-to-post-effect head chains for own updates; full note/evidence/revision/audit/outbox ledger; all500 raw relationships, physical scoped FK attacks, partial chunks, restart and independent-process races; and contiguous frontier without skipping a prior pending batch. Neither the manifest input snapshot nor the structural note count alone permits completion.

Any source/account gap remains explicit. Honest known-work/attention/backlog and zero-message carrier, provider/profile/tokenizer/context budgeting, bounded retry/extraction-failed truth and shipped no-click DI/API/UI remain incomplete. Existing Gateway/configuration is reused; actual-model100/30 evaluation is **NOT_RUN_UNEVALUATED**. Full278/279/233, customer production and merge acceptance remain open.

## Current exact CI ownership

Remote5fc5b87782f6ab06245a8d757c4e81e0cb670216: Build38096849478/native114344400101 and .NET114344400163; Governance38096849367 SUCCESS at dispatch. Root FULL read and persisted source9d approval6103383218, native794 approval6103421671, repair5fc approval6103509982. These are scoped source approvals. Own113/all8/quality/Governance through terminal, read full logs and original browser images, and repair any real red on the same branch. Do not supersede meaningful active native CI or use prior head evidence for this head.

Prior exactec3 remains terminal RED: Build38093894476/native114335702045/quality114341455901, six other prerequisites and Governance38093894474 SUCCESS. Root full native112:19 missing,144PASS/four failure patterns; full.NET2774 zeroFailSkip. History Core Edit ACK status/body/iteration was unretained; exact hosted cause remains unproven despite the reproduced empty-observation contract defect. This record supersedes earlier active ec3 dispatch prose without changing historical proof.
