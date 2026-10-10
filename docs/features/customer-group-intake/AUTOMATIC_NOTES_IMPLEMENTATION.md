# Automatic notes implementation — issue278

Status: allocationa50 has accepted native/all8 evidence. Remote2199a50 Build38059643216/native114235105757 and quality114239125705 ended RED at the owned source container observation. Six prerequisites/Governance passed; renewed claims/source runtime acceptance remains withheld. Frozen787 private proposal reviewWITHHELD6098710405 identified the missing clarification-field minimum. Both defects are locally repaired on the same288 with original controls retained, full1814 Persistence and58 Python PASS; combined frozen review and renewed exact CI remain required. Automatic note persistence and actual model evaluation remain incomplete/unevaluated.
Dependency: issue277 accepted at merged main `2e620a4856ab65f80706ba6f5b2c3da6b2ea74fd`, independent receipt6096704767; all exact-main gates passed. Full issue233 stays active.

## Product contract

Follow OWNER_CORRECTION_AUTO_IT.md and CODEX_PROMPT.md. Authorized customer source events automatically produce protected SQL work notes or attention notes; no employee Generate/Accept/Approve/Send prerequisite. A later, separately authorized internal IT reporter consumes NotesCommitted. No customer outbound, ERP writes or live account activation.

Customer facts, AI interpretation and IT-confirmed business state remain distinct. Reader/audience grants do not grant editor rights. Runtime completion never implies business resolution.

## First implementation checkpoint: bounded structured Responses

Extend the existing worker Responses adapter and provider-neutral gateway; retain legacy Reasoning wire/output behavior. StructuredGeneration sends a JSON schema object through text.format, strict mode and store=false. Only a completed assistant JSON object can be accepted. Host validation of facts/evidence/authorization remains a later, separate step.

Proposed hard bounds: input128000 UTF8 bytes, schema32000 UTF8 bytes, output64000 UTF8 bytes, response entity262144 bytes, JSON depth32 and schema nesting10. These are byte bounds, not claims about model tokens. Restrict the schema profile to closed objects with all properties required, arrays, primitive/nullable types and typed enums; reject duplicate decoded names and unsupported composition/ref keywords.

Use one total deadline of at most60s. A singleton adapter admits at most4 owned operations. A timed-out caller cannot release capacity while the underlying request/body/disposal/cancellation remains pending. Refuse a fifth call without starting another provider operation. Fixed sanitized errors must exclude response bodies, network details and inner exceptions. Preserve caller cancellation.

Read actual response bytes into a finite private sink before parsing. ContentLength alone is insufficient. Refuse overflow, invalid UTF8/Unicode, duplicate JSON names, malformed/incomplete/refused/tool/multiple responses and inconsistent usage. Use a production handler with redirects and automatic decompression disabled; only explicit owned-loopback configuration may use HTTP. Arbitrary injected handler allocations are outside the accepted-byte guarantee and require separate tests of the real production handler.

Verify the actual public adapter call path, boundary sizes, actual write overloads, noncooperative headers/body/cleanup, capacity retention/recovery, deadline/caller cancellation and owned-loopback redirect denial. Preserve large legacy Reasoning behavior. This checkpoint alone does not satisfy issue278.

## Durable allocation implementation checkpoint

See ADR_GROUP_AUTOMATIC_NOTES.md in architecture and ../../validation/GROUP_BATCH_ALLOCATION.md. Actual SQL store now persists immutable batch/raw ledger with ScheduledThrough atomically under the same ingress source lock, trusted current Extract authorization and extended effective runtime permissions. The new owned native proof is present but unexecuted until approved checkpoint CI.83 local focused controls PASS; InMemory does not prove native SQL. Fenced claims are now locally implemented; native claim proof, context, notes/outbox, worker activation and actual model evaluation remain required.

## Fenced claims implementation checkpoint

See ../../validation/GROUP_BATCH_CLAIMS.md. Frozen88cb review WITHHELD one concrete expiry/clock-rollback P2. Its repair adds a durable first expiry witness, typed no-allocation expired verdict and explicit metadata retirement; old nonce/handle cannot revive and old epoch cannot retire a replacement.133 final focused controls PASS, including45 claim controls; full1661 Persistence controls ran before the final old-receipt guard, covered by final focused controls. Parent a50 allocation all8/Governance/84 markers/140 PASS/2166.NET/all6 original images are closed. New claim native modes remain UNEXECUTED and repair awaits frozen independent approval. No claim is completion or a note. The first protected/result consumer still needs an owned staged-effect rollback/witness-only commit wrapper and actual native proof. Source ordering/terminal frontier and actual protected brain/pipeline remain required.

## Subsequent checkpoints required on this same issue/PR

- Durable SQL quiet/max-wait scheduling, claims and immutable allocation receipts. Bound raw revisions independently of distinct message IDs and model context bytes/tokens. Separate scheduled allocation from the contiguous terminal frontier; restart/continuous traffic/late commits must not lose work.
- Freeze exact source IDs/revisions as of cutoff. Recall/edit precedence, per-message dispositions and complete coverage ledger remain durable. New source changes invalidate dependent context without starving completed work on unrelated traffic.
- Trusted service authorization and exact tenant/company/source/account/epoch/deletion generation; no forged portal user. Read only original protected source, current same-group notes and authorized glossary. No owner archive/DM/cross-group context.
- Resolve sources and keys through production DI. Build bounded context using an operator-qualified model/tokenizer profile. No fabricated token ratio or unknown-model budget. Prepare encryption outside short SQL transactions; recheck current authority and all contributing revisions at final commit.
- One message may produce several notes; several messages may support one note. Link only unambiguous source codes or verified same-group replies. Ambiguity/unknown media/deadline becomes clarification; model retry exhaustion becomes host extraction_failed attention. Preserve separate customer deadline and IT SLA/status.
- Versioned secret quarantine before provider context and after output. Persist protected original refs without leaking quarantined text through notes, logs, errors or provider metadata.
- Note/revision/evidence/disposition/completion receipt and NotesCommitted outbox commit atomically. Rollback emits no successful note event. Lost-commit-reply replay returns the original receipt, IDs and graph. No-work and zero-message gaps use honest separate outcomes.
- Scoped, audited read/IT handling APIs and UI after automatic recording; current permission and optimistic business version fences. Introduce tables through versioned expand migrations with immutable records and least-privilege native SQL controls.
- Prove shipped DI, no-click pipeline, actual SQL/broker/process restart and browser privacy/default-off behavior. Keep fake controls separate from actual provider and production evidence.
- Freeze normalized input bindings, policies, prompt/schema/provider profile and independent labels before actual outputs. Proposed120 primary/40 independent windows are unevaluated; score >=100 actual model-eligible labeled windows and >=30 independent windows, recall>=95%, precision>=90%, zero critical scope/false-commitment failures. Do not count host-only quarantine/recall/media-empty exclusions as model success or relabel after outputs.

## External dependencies

Existing Gateway configuration discovery found the worker environment loader and protected deployment input. Required primary base URL/model/authorization values are absent in checked local scopes/default files; a separately deployed server secret store is inaccessible. Reuse that loader/Gateway, never borrow coding-agent credentials or add an independent AI configuration. Dedicated ERP read-only accounts and production hosting remain separate full-product gates. Existing installer239 is a separate unqualified lease. No live connector/model/customer/deployment evidence is asserted by this plan.

## Scoped protected source context checkpoint

The trusted service reader now reads host-selected allocated source heads as of the immutable cutoff, validates original receipts/UTF16/AEAD metadata, resolves existing scoped keys outside SQL and fences exact current dependencies after every key await and before release. Recall, changed-after-cutoff and obsolete generation are metadata-only dispositions. This is a bounded private context slice, not complete raw coverage or note completion. See [verification and remaining gates](../../validation/GROUP_BATCH_SOURCE_READER.md).

No independent AI client/config was added. Read-only [existing Gateway configuration discovery](../../validation/AI_GATEWAY_CONFIGURATION_DISCOVERY.md) found the actual environment loader/deployment path but no usable values in the checked local scopes/default files. Real model evaluation remains UNEVALUATED. Native source modes and the PID1 claim fixture repair are pending renewed exact-head SQL CI; automatic notes/glossary/quarantine/token profile/effect rollback/outbox/DI/UI and278/279/full233 remain.

## Versioned preflight/output quarantine checkpoint

The sealed source preparation calls group-secret-quarantine-v1 before selecting provider candidates. Every selected message retains a host preparation disposition; known credential-like text is excluded with metadata-only reason and protected originals remain unchanged. Provider candidates contain host evidence IDs and original nonquarantined text, not raw external identities. The decoded-JSON gate now preserves key/value assignment relationships after the five original counterexamples in cecee reviewWITHHELD6098304670; repaired2199a50 review6098494666 approved the bounded checkpoint. NoMatch is only a conservative known-form result. See [verification and limits](../../validation/GROUP_SECRET_QUARANTINE.md). Renewed native/preparation assertions remain pending; this does not qualify model tokens, activate a worker/model or commit SQL notes/attention.278/279/full233 and proposed120/40 evaluation remain outstanding/UNEVALUATED.

## Private grounded proposal/schema checkpoint

The existing Gateway StructuredGeneration schema and concrete sealed proposal parser now validate exact host source IDs/revisions, original literal quotes, one model disposition per eligible candidate and bounded many-to-many notes. Tenant/destination/IT business authority remain host-owned; customer deadline wording retains evidence and is not a committed SLA. Interpretations remain explicitly AI-generated, and literal matching does not prove semantic accuracy. The result is private and conveys no future release or SQL effect authority. Local41 new controls plus five retained original reviewer controls and full1814 Persistence passed. Frozen787 reviewWITHHELD6098710405 was repaired by requiring nonempty missing_fields for needs_clarification; combined frozen independent review, protected same-group brain, qualified context/tokenizer, effect rollback/notes/outbox/DI/UI and actual model evaluation remain. See [boundaries and evidence](../../validation/GROUP_GROUNDED_WORK_PROPOSAL.md).

## Latest native source fixture repair

Remote2199 reached prior listener/claim/source-read and current-denial controls, then failed the held-key owned-container identity assertion. Root collected73/93 required markers,124 PASS and4 terminal failure markers; held-key revocation, expiry and browser completion remain unaccepted. Both name-based ownership lookups now use Docker container type because the image and container share a name. Full64hex ID/exact label and no exec/kill before validation remain. Inert controls reproduce image fallback, absent-container waiting, valid-container progress and retained foreign-label/bad-ID denials. Actual failed inspect output was not captured; do not claim a unique observed runtime cause.58 Python controls pass; native acceptance requires a renewed exact-head successful run and all original artifacts.
