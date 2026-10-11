# Terminal receipt schema prerequisite — issue 278 / Draft 288

Migration `20261011025104_AddGroupBatchTerminal` expands the platform with two metadata tables. It does not mark a batch complete or wire an automatic worker.

`GroupBatchTerminalReceipts` stores a scoped immutable receipt, the bounded version 1 `AIOGTRM1` manifest (257–8177 bytes), its SHA256, the original allocated interval and counts, terminal operation, original acquisition identity, authority versions and UTC commit time. Checks preserve 500 raw revisions, 100 selected messages/contributors and 40 notes per contributor, including zero-note NoWork contributors. A digest check establishes stored-byte consistency; it does not establish authorization or complete graph integrity.

The receipt primary key permits one terminal result per tenant/company/binding/batch. Unique scoped terminal-operation and interval-start indexes reject duplicate carriers. Restrictive foreign keys bind the company, binding, allocation, service and original acquisition. A new scoped alternate key on `GroupBatchClaimReceipts` makes the acquisition foreign key include the original batch and operation. This key prevents cross-batch substitution; the owned writer still has to compare every claim/authority field and authenticate original provenance.

`GroupTerminalFrontierStates` separately records the contiguous completed interval, its last scoped terminal receipt, positive version and UTC update time. Its scoped primary key and restrictive company/binding/source-state/terminal-receipt references preserve isolation. Zero progress requires no last receipt; positive progress requires one. The existing scheduled and committed ingress markers and their runtime rights remain in `GroupSourceStates`.

## Runtime permissions

Both tables have the existing independent `aioffice_binding_operator_owner`. The existing runtime role receives SELECT/INSERT on terminal receipts and SELECT/INSERT/UPDATE on the cursor. Receipt UPDATE/DELETE/ALTER/TAKE OWNERSHIP are denied, with an explicit column UPDATE denial covering every original receipt column. Cursor DELETE/ALTER/TAKE OWNERSHIP and updates to its three scope columns are denied. Only `ThroughSequence`, `LastTerminalBatchId`, `Version` and `UpdatedAtUtc` are writable cursor columns.

The dedicated `GroupBatchTerminalPermissionVerifier` chains the existing complete work-note/ingress/binding proof, checks both new tables' ownership and effective rights, and inventories effective column UPDATE rights. Additional columns default immutable in this proof. SQL Server column GRANT can override table DENY, so a table-only permission check is insufficient. Existing ingress and note paths do not call this new verifier yet. Runtime INSERT rights alone are not authorization to construct a terminal receipt; the future owned terminal transaction must enforce that contract.

Destructive Down is refused; committed completion evidence requires reviewed forward repair. This migration alters no existing column or stored data. Its only existing-table schema expansion is the scoped alternate claim key.

## Verification and remaining work

35 focused model/migration/permission controls PASS, zero failures/skips and no warnings on the final serial run, including eight new terminal-schema controls. Actual EF SQL generation and the effective permission proof parse with SQL Server 160 ScriptDom. The snapshot has no pending model changes; test connections remain closed. Seven changed source/test files pass whitespace verification. The first expanded run identified the old 30-table assertion and missing new migration in the cumulative ownership test; the final test checks all 32 group tables and retains every tenant/company/restrictive-FK/ownership/column-denial assertion. Initial concurrent EF/test execution also caused a DLL-copy retry warning; final verification runs serially and has no warning.

This is source/schema preparation only. Native remote67605e1/116 remains separately owned and predates this migration. This schema has not run on native SQL, proved effective permissions as the runtime identity, or tested actual terminal insertion/update/races. There is no owned terminal store, authenticated contiguous SQL advancement, own-input dependency-chain validation, expiry rollback proof, automatic gap/retry attention, DI or no-click application path in this checkpoint. Full278/279/233 and merge remain incomplete. Actual model evaluation is **NOT_RUN_UNEVALUATED**; synthetic and inert design-time controls are not model or production acceptance.

## Mutable cursor permission repair

Review of frozen739d01a identified the mutable whole-object UPDATE=1 prerequisite. Retained native42cc94dc evidence had already refused that pattern with immutable scope-column DENYs; accepted cd17377db0ba28667b77dec562746c961d2668ba removed it from the original ingress proof. The new cursor now follows the same effective per-column pattern: require all four writable columns and inventory every remaining column as immutable. Receipt whole-object UPDATE=0 and all original migration DENYs remain unchanged. A regression explicitly refuses the unsafe whole-object prerequisite while checking every retained column condition. Final focused verification is36 PASS, nine new27retained, zero failures/skips/warnings, and two changed-file formatting checks pass. There is no new native cursor-rights execution from this repair.

Parent WITHHELD review6104920534 was independently refetched/read in full by root:5299 characters, SHA256 `5b4d608bc1e5d219cc7ecdd650ae9e069a0b667626fe5f8d222a6e2210a5caa5`. Complete repaired source review6105046515 was then ROOTFULL refetched/read/persisted:5040 characters, SHA256 `0b53a960b904bfd52807372177727abbb68c8b94118b403e3d576f164f387ba1`. All41 controls pass, including the unchanged original permission regression and four other independent controls. This closes that source finding; it did not establish SQL engine execution.

Separately, remote676 native116 is now CLOSED SUCCESS/all8/Governance with the complete scope and remaining limits recorded in `GROUP_ORIGINAL_EFFECT_116_QUALIFICATION.md`. It still excludes this migration and repaired prerequisite. Unfinished own-input source work is held privately outside this repair checkpoint and has not been tested or approved.

## First SQL engine execution — failed464 and repair

Reviewed head `464607ac961d409ef02d658f9379fc56e39316ce` reached terminal
failure on Build38108833612. Native114379946799 failed during bootstrap:
SQL Server rejected `varbinary(8177)` because fixed `varbinary(n)` has an
8,000-byte maximum. The additional `ACTION` syntax messages are retained in the
full log; fresh native execution is required to establish closure. ScriptDom
syntax parsing and EF snapshot agreement had not checked that engine limit.
Only two shipping preflight PASS lines ran; the required116 profiles and all
six original browser artifacts did not run.

The unmerged, unapplied terminal migration now uses physical `varbinary(max)`
in the model, migration, Designer and snapshot. Its logical model maximum8177,
the `DATALENGTH BETWEEN 257 AND 8177` constraint, magic/digest checks, all other
checks/FKs/indexes, ownership/DENYs and forward-only Down are preserved. This
repairs the original CREATE before it can execute; no deployed schema or data
is edited. A new regression checks actual generated numeric binary declarations
against the engine's8,000-byte limit while retaining the8177 manifest bound.

Dotnet114379946773 independently failed four retained
`StructuralCoverageAcceptsActualReaderPriorityHeadsInsideAndBeforeAllocation`
cases (Recall/Edit, before/inside allocation). Their structural fixture omitted
`SourceSetSha256`, leaving an empty value rejected by the unchanged canonical
guard. The fixture now computes the same single-source SHA256 as the shipping
commit store from the actual selected message ID/revision. All four existing
cases and all original positive/negative assertions remain. The production
guard and source/brain/effect validation are unchanged.

Full failure evidence is retained: native99,427 bytes SHA256
`68a85cf346cc6998df6356ee8afcf257b2ea27a6292a6d5f87e85d50690e27a7`;
.NET1,141,786 bytes SHA256
`2e4a6c41417f981bd23228ceb980bfced9a8b3b8e819b81e115de7332bf84214`;
quality6,150 bytes SHA256
`56ca7d930e7ba0c6e3e077e0754a447e193bddb630c2338119b36b2c892b58e0`.
The four .NET runs attempted `[6,520,25,2776]`:3323 passed and four failed.
Governance and the five other prerequisite Build jobs succeeded; the aggregate
quality gate correctly failed for .NET and local runtime. Frozenc82e634 own-input
source review is separate and its ACTIVE464 state is a historical snapshot.

New exact-head CI closure, actual terminal runtime permissions/insertion/cursor,
own-input SQL chains and full278/279/233 acceptance remain pending. Actual model
evaluation remains **NOT_RUN_UNEVALUATED**.
