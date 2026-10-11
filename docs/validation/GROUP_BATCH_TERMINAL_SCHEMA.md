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
