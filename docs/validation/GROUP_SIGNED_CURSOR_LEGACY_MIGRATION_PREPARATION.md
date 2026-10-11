# Signed cursor legacy migration preparation

Status: private source preparation on issue278/PR288. Actual SQL migration
results remain **NOT_RUN_UNQUALIFIED**. Real model evaluation remains
**NOT_RUN_UNEVALUATED**. This document supplies no feature, merge or production
acceptance.

## Existing contract

The existing migration `20261011075456_AddGroupSignedTerminalCursor` first
refuses persisted unsigned/empty cursors with SQL51048. It then installs the
nonempty CHECK and restrictive exact seven-column cursor-to-signed-advance FK.
No row is guessed, repaired, backfilled or re-signed by migration.

The new test-only `frontier-cursor-migration` mode reuses the unchanged root
replay before reading an operator credential. That replay independently verifies
the original historical graph, current extraction authority, retained-key
enrollment and MAC, all26 advance/14 key/7 cursor fields and unchanged original
receipts. Copying FK ancestors into a shadow does not establish any of those
authorities for shadow rows.

## Owned database boundary

The coordinator first checks its existing owned CI/directory/API boundary. It
then inspects only the generated installation's Compose default network ID and
labels and the exact unique running SQL container ID, project/service labels,
network ID and `sql` alias. It never requests Docker environment/configuration
contents. Only after those observations may it read the installation's existing
generated SQL operator password.

The original twelve root/replay/cursor children receive their original runtime
identity and common retained synthetic root key. The thirteenth shadow child
alone receives the generated operator connection through one environment
variable; credentials never appear in argv, stdin, stdout, exceptions or source.
This is a test credential channel, not a new application provider/secret loader.

The executable requires fixed `sql`/`AIOfficeLocal`/`sa`, mandatory encryption,
certificate trust and a32–128 ASCII generated password. Alternate principals,
customer catalogs, authentication, attach/failover settings and other connection
keys refuse before connecting. Its normalized connection has no pooling,
retry or MARS. Every mutation/migration connection separately requires the
strict `GroupCursorShadow_` plus32 lowercase nonempty GUID grammar. Source and
master catalogs cannot satisfy the mutation guard.

An independent initial observation must establish absence before CREATE,
which separately refuses an existing name. The generated suffix is also the
ownership token. The database-level property must be nvarchar with maximum
length64 bytes, full DATALENGTH64 and exact nontruncated binary-collation equality.
Observed DB_ID must match the captured identity when available. Lifecycle commands are constructed before
opening, so SqlClient's password redaction after Open cannot weaken the guard.
Each legacy mutation probe has its own fresh unopened normalized connection.
The shared command-construction template stays closed; a connection whose
password was redacted after a prior Open is never reused for another probe.

Once CREATE was invoked, cleanup independently inspects actual DB_ID and the
complete typed token even when CREATE or property-mark replies were lost. A
known exact owned token plus verified initial absence permits recovery of the
lost identity observation. An unmarked, foreign, preexisting or unknown identity
never grants DROP. Such failures withhold the child result; eventual removal
of the explicitly owned disposable CI stack is separate cleanup evidence.

## Actual engine plan

Use real `IMigrator` to obtain predecessor
`20261011061648_AddGroupTerminalKeyEnrollment`, with the existing aioffice EF
history table. No EnsureCreated, copied DDL, NOCHECK, fake Current, clock
replacement, new raw key or migration history forgery is involved.

Copy the original eleven-table physical FK closure in one pinned Serializable
source transaction and the existing exact scoped transaction APPLOCK. Every
guard/copy command independently checks original catalog, one valid transaction,
Serializable isolation and the held Exclusive APPLOCK. All actual/model column
shapes must match; every source selection and target insertion must contain
exactly one row. Five identity values are typed SQL parameters. The closure is
Companies, GroupConnectorAccounts, GroupServices, GroupBindings,
GroupSourceStates, GroupBatchAllocations, GroupBatchClaimReceipts,
GroupBatchTerminalReceipts, GroupTerminalAdvanceKeys, GroupTerminalAdvances,
GroupTerminalFrontierStates. Original claim operation/account/key subqueries
are fully scoped. They are not reconstructed from current claim state.

Prepare five separate physically valid legacy failures: zero Through/null
batch, mismatched positive Version, mismatched positive Through, UTC time+100ns,
and a positive cursor whose original signed advance is absent. Each must produce
the real target migration's exact SQL51048/message; helper refusal51051–51054,
old CHECK547, unrelated transport/cancellation and an unexpected success cannot
count. Restore the complete original shadow row graph between cases.

Separately require Version0/non-UTC updates to fail through the retained old
value CHECK547. These two failures are not migration51048 observations.

Read all69 exact unique tag/SHA256 results before and after each target refusal:
complete table inventory, all54 model rowsets, full history, and13 catalog/
security categories including every extended property. Reject missing/malformed,
duplicate/foreign rows and additional result sets. No raw row, payload,
credential selector, object definition or ownership value is emitted.
History must still end at the predecessor and the entire before/after catalog,
row and security hash set must match after reconnecting.

The restored matching positive case must upgrade through real IMigrator, retain
all non-history rows, append exactly the target history record, and expose the
trusted complete seven-field FK and named CHECK through the existing catalog
observer. Reapplying that target must preserve the full upgraded snapshot.

All original source snapshots and each of the four original frontier graphs
must remain unchanged. Cleanup disposes every connection, independently checks
token/DB_ID, drops only that owned database, and verifies absence. Any case,
protocol, original graph or cleanup failure withholds all three aggregate
summaries. Each operation captures its first exception before independently
attempting rollback, close, postconditions and disposal. Expected51048/547
classification additionally requires clean resource completion, so preserving
the original exception cannot hide a failed cleanup. A later clean operation
cannot erase that failure. Known fixed failure phase labels
may be reported; arbitrary child SQL/errors/data never leave the coordinator.

## Bounds and remaining work

Each SQL command is bounded at30seconds; the new isolated operation has270seconds
and independent shadow cleanup45seconds within the child330seconds. Existing twelve children retain150/170seconds. The
existing whole-stack50minute CI budget is unchanged and still needs measured
runtime for this extension. Snapshot limits are16384 rows/table and8MiB JSON
per command. The JSON byte check occurs after SQL JSON construction and is not
a streaming SQL memory bound.

Only these five migration-negative cases are prepared. Tenant/company/binding/
batch mutations need honest additional FK-valid ancestor fixtures before all
seven tuple components can be physically qualified. The old matched tuple may
still be a stale signed prefix; the shipping store's authenticated latest-leaf,
full-prefix and current-authority checks remain mandatory.

Successor/old replay/rotation/edit-recall/revocation races, continuation/gaps,
nonempty brain, multiple contributors, own-input preservation and actual worker
DI/no-click acceptance remain separate requirements. Actual model/provider
evaluation, issue279, real customer readonly ERP and deployment remain pending.
