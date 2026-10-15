# Current admission for a restored original attempt

Issue278 / Draft PR288. This is a production preparation component, pending independent source review and journal integration. It does not activate extraction, qualify a model or complete the feature. The production released catalog remains empty. Original854 CI and its failed predecessors retain their own evidence; local tests of this component cannot qualify those runs.

## Preparation outside SQL

`GroupModelAttemptAdmissionPreparer` receives the existing fixed `IAiStructuredRequestAdmission`, the owning context and clock. It derives a request from the exact restored model chunk and creates a new host nonce. It accepts a decoded V3 plan/sidecar as structural data, without upgrading either to persisted authority. Host-only/legacy/mismatched chunks refuse. Historical input and schema byte lengths and SHA256 must match the complete reconstructed request before calling the gate.

The same existing gate prepares and describes the request. Its current Gateway, released profiles, complete-string tokenizer and every route's capacity checks apply. The returned snapshot must independently match the exact original input/schema. Current models/routes/tokenizer/evidence/windows may differ from historical metadata; the historical route does not authorize or select the current model. No new AI client, options loader, credentials or profile entry is added.

The wrapper uses an original30second preparation budget, capped by claim expiry, and bounds the carrier to two minutes from its own start, also capped by claim expiry. UTC and monotonic time, original caller cancellation and an idle owning DbContext are checked around gate preparation and metadata capture. Active/ambient transactions and dirty contexts refuse. Intermediate encoded input/schema buffers are zeroed. Failure text is fixed and has no private inner exception.

The carrier retains the exact original binding, copied closed sidecar, chunk ordinal, fresh nonce, current immutable snapshot and opaque prepared handle. Issuer identity and the exact model-context reference are required. No provider-execution API is exposed before the journal companion and committed permit exist. A carrier is not a journal start, persistence receipt, current SQL authority or successful extraction.

## SQL inspection and remaining journal integration

`InspectIssuedLocked` checks issuer/context/lifetime without gate, tokenizer, provider, key or database calls. If it observes claim expiry, it returns the exact authenticated handle and observed UTC timestamp. The journal owner must carry that observation through savepoint rollback and retirement, including a later UTC rewind; it must not rediscover expiry. Before this component is used by the Worker, the journal must:

1. Authenticate the actual stored original and complete sidecar in its existing owned Serializable transaction, using the same context and current source/brain/claim/permission fences. Structural metadata is insufficient.
2. Atomically append the new start and an immutable versioned companion binding the exact start, original plan/sidecar/chunk and current snapshot. Use an expand migration and restrictive exact foreign keys; keep legacy starts/results readable without a companion and never retrofit them.
3. Return a provider permit only for a newly committed start. Replayed/lost-reply nonces cannot return a second permit. Cap the start deadline by prepared-carrier expiry as well as claim expiry.
4. Fence current SQL authority again before release, then execute the same opaque handle through the existing gate outside SQL. The gate must recheck current release and consume its one-use handle. Recheck current dependencies before protected result/effect persistence.
5. Keep accepted-result recovery first. Existing legacy result reads must not depend on the sidecar table or present-day model eligibility. Then wire the one-batch coordinator, durable scanner and shipped Worker, with the required no-click SQL/queue/restart evidence.

These are remaining implementation steps, not acceptance evidence or a newly introduced product gate. No journal, migration, coordinator, scanner or DI registration is changed by this component.

## Verification scope

The new unit controls cover decoded recovery data, fresh request identity, exact input/schema, changed current profile metadata, incompatible legacy/host/foreign history, dirty/ambient context, post-await drift, final cancellation, fixed private failures, issuer/context forgery, UTC/monotonic limits and exact expiry observation across rewind. Two controls use the actual existing Gateway/admission class: its production catalog refuses preparation; a test-only synthetic catalog measures the exact complete strings without invoking a provider. These controls are not authentic model evaluation or SQL runtime qualification.

Actual model evaluation remains `NOT_RUN_UNEVALUATED`. Existing protected deployment environment loading requires `AIOFFICE_AI_PRIMARY_BASE_URL`, `AIOFFICE_AI_PRIMARY_MODEL` and `AIOFFICE_AI_PRIMARY_AUTHORIZATION` together. Scoped inspection found no available values; the deployed secret store was unavailable for inspection. Preserve the approved configuration path and continue tests that need no real model. No-click278, internalIT279, readonly Medcom/Novo, Windows, deployment and full233 remain incomplete.
