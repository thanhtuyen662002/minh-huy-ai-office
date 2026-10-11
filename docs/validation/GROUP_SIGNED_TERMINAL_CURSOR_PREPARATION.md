# Signed terminal cursor schema preparation

Issue278 / Draft288 remains incomplete. This is a private candidate based on source-approved b67f332be7d4ebad1d7419ca12ddee67ce5b957f. Current exact b67 Build38122945344/native114421254988 remains owned; no candidate is pushed over that run.

## Persisted cursor contract

An empty frontier is represented by no cursor row. A stored cursor must have positive ThroughSequence/Version, a non-null terminal batch and UTC UpdatedAtUtc. Its seven existing fields reference the exact signed advance tuple: tenant, company, source binding, version, through sequence, batch and advanced UTC. Existing scoped company/binding/source/terminal references and the original values CHECK remain. There is no extra payload or secret column.

The versioned migration20261011075456_AddGroupSignedTerminalCursor adds a scoped advance alternate key, cursor index, explicit positive/non-null CHECK and restrictive seven-field FK. The physical batch column stays nullable for compatible CLR/schema shape; the new CHECK closes SQL's partial-null composite-FK escape. No columns, old rows, providers, key references, secret resolution, worker DI or rights are changed.

Before DDL, a transaction-bound UPDLOCK/HOLDLOCK preflight rejects any empty/unsigned or mismatched existing cursor with fixed THROW51048. All seven existing cursor values must already match an advance. Migration does not rewrite, backfill, sign, enroll or infer history from mutable current state. Existing exact rows remain unchanged. Automatic destructive Down is refused.

This FK identifies an existing tuple. SQL does not verify its MAC or select the latest version. A cursor restored to an older valid tuple can satisfy the FK. The shipping frontier store must still authenticate the MAC, enrolled material, latest/immediate predecessor, exact cursor, original historical graph, current authority and locked before/after snapshots. This schema candidate grants no independent chain, latest-state, authorization or provider qualification.

## Verification scope

Six new .NET cases inspect the real SQL Server EF model, generated migration operations, ordinary/idempotent forward SQL with SQL160, complete seven-field FK/principal key, partial-null closure, refusal before DDL, absence of data/rights rewrites and no pending model changes. Retained terminal model assertions include the new fifth FK and additive CHECK while preserving every original constraint. All are cold checks: the design-time loopback endpoint is never opened.

Private focused143PASS covers the new six and retained terminal/advance/key/store/independent root observations, zero failure/skip/compiler warnings. Full3193PersistencePASS = 3187 retained + 6 new, zero failure/skip; final post-format six new cases PASS with zero compiler warnings. Both actual projects pass changed-file formatter verification for all six changed/generated CSharp files. Independent preliminary source review is tracked separately in candidate metadata. No physical SQL result is claimed from these tests.

## Remaining delivery

Close current b67 exact CI first, preserve terminal failure history, then review/freeze this candidate on the same branch. Physical migration preflight and actual cursor CHECK/FK attacks remain NOT_RUN_UNQUALIFIED. Existing root/replay, all original SQL/browser gates, exact all8/quality/Governance and six original image views remain required. Successors, older replay, whole signed prefix, rotations/edit/recall/revocation/snapshot races, continuation/gaps, nonempty brain/multiple contributors/own inputs, worker DI/no-click and full278 remain outstanding. Real model through the existing Gateway remains NOT_RUN_UNEVALUATED; no production activation or merge acceptance follows from this preparation.

## Prepared owned physical cursor phase

The private candidate also adds one closed frontier-cursor-contract mode. The original owned guard runs first. The unchanged root replay authenticates the actual current root/MAC/enrolled material/current authority and independently observes all original fields before attacks. The new helper requires a trusted enabled seven-field SQL FK with exact catalog ordinal mappings and a trusted enabled signed CHECK. It does not query a replacement receipt or fabricate authority.

Four negative actual UPDATE probes require SqlException547 naming the intended new constraint: change Version, ThroughSequence or UpdatedAtUtc by one unit/tick, and set ThroughSequence0/LastTerminalBatchIdNULL together (valid under the old empty alternative). Four positive no-op updates must each affect one row for the original permitted mutable columns. Every probe uses its own Serializable transaction and existing scoped transaction APPLOCK, always rolls back, restores closed SQL/empty tracker/no transaction and compares every original cursor field. SQL errors are never printed.

The root coordinator retains all eight original root/replay children and adds four distinct cursor children, each requiring the original replay success plus the cursor success exactly once. All35 original snapshots are read13 times and all complete prior enrollment/advance/cursor graphs30 times. Old root summary remains exact; a separate required cursor summary follows it. Every existing failure/timeout/ownership/cleanup case remains, with nine cursor-specific faults withholding both summaries. Future complete native acceptance requires all original118 plus the new cursor marker119; actual current b67 remains118 and excludes this candidate.

Final private144.NETPASS = 134 retained root observer/store/leaf + 6 schema + 4 actual rendered SQL/catalog/scope/lock controls, zero failure/skip/compiler warnings. New controls parse the actual uncomposed SQL160 catalog/APPLOCK and eight actual typed, strictly scoped single UPDATE commands; they do not execute SQL. All155PythonPASS = 144 owned guards + 6 terminal frames + 5 root/cursor coordinator methods; inert callbacks qualify orchestration only. Both new actual-project formatter checks PASS, alongside the earlier persistence/test schema formats, covering all9 changed/generated CSharp files across three projects. The new compiled cursor mode refuses unowned at owned-guard before config/SQL/key. The prior full3193 is schema-only evidence; a full3197 run is not claimed here.

Independent preliminary seven-path schema review has no actionable P1/P2, full6983-character report independently ROOT read (SHA25628bfb472b4b70c9fd0a927b3baf963f5548d405f4bc6fc77ff8d88d92c074b19),28 independent controls/two formats. The complete twelve-path private candidate was then independently reviewed without actionable P1/P2:28.NET,155 retained/current Python plus4 own controls, formats and compiled unowned guard PASS. ROOT independently read all9163 characters (SHA256019c65d19302f64acd5b74e0e33d61076be452f843d1b547f7ac0b74fec06177). This preliminary receipt does not approve the promoted Git checkpoint or its durable state.

Physical migration51048 legacy refusal before DDL/unchanged rows and physical119 attacks/root/successor/older tuple/races remain NOT_RUN_UNQUALIFIED. The four prepared negative cursor probes do not qualify every migration-preflight invalid tuple, successor save, stale matching leaf, full prefix or real concurrency. Existing Gateway actual model remains NOT_RUN_UNEVALUATED; full278/279/233/production/merge remain incomplete.

## Parent runtime closure and current promotion

Parentb67 exact Build38122945344/Governance38122945375/all8 closed SUCCESS. ROOT read complete native118/.NET3738/quality and individually viewed all six original images. This qualifies only the original four root/replay scopes and retained strict original-response Chromium gates. New cursor migration/preflight/547 phase is absent from that parent and remains NOT_RUN_UNQUALIFIED. Promoted source plus current durable state requires a complete frozen Git review before push and a fresh exact119 runtime closure. Full278/model/customer/production/merge are incomplete.
