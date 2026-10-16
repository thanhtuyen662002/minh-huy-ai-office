# Owned cursor snapshot: canonical empty JSON

## Exact terminal failure

Parent `653b07ae2533c024262b04c4cae79ff0ba33ca14` has terminal Build
`38144853642` failure and Governance `38144853646` success. Six prerequisite
jobs passed, including 3,859 .NET cases (`6 / 520 / 25 / 3308`, zero failures
or skips). Native job `114485958046` failed before shadow creation:
`original-source-snapshot`, SQL `51054`, zero-based snapshot ordinal `5`.
The compiled original plan maps that ordinal to
`rows:CustomerSlaPolicyRevisions`. Quality job `114493461321` failed because
`LOCAL_RUNTIME_RESULT=failure`; its other six results passed. No browser
artifact was produced. The complete frozen 120-marker parser found 99 once,
21 absent, no duplicates and five failure lines. This is not runtime acceptance.

Full native log: 83,554 bytes, SHA256
`ca39a88ded152a676349c7dc44fa646d08a34f53efd1c74a146f20720b7cf6e4`.
Full quality log: 3,528 bytes, SHA256
`931f0c213841ac96585418fe9ccec141f414ed5b967449941add0e21b36a60b2`.
Root ran complete-byte native/.NET parsers, read the actual native failure and
cleanup sections, and read the entire quality log. Entire native and .NET
verbatim consumption is **false**.

SQL `51054` previously combined three refusals: excess rows, NULL serialized
payload and excess serialized bytes. The hosted log contains no discriminator
between them. The original fixture does not seed SLA policy revisions, so
legitimate-empty serialization is a compatible source defect; the unique hosted
branch remains **unproven**.

## Repair and required engine controls

The existing fixed `FOR JSON PATH,INCLUDE_NULL_VALUES` scalar is wrapped in
`COALESCE(...,N'[]')`. This supplies the canonical empty array when that scalar
has no row. A nonempty result, including a row with SQL NULL values, retains
its original complete serialization. SQL execution errors still fail; there
is no exception-to-empty conversion, wildcard projection, truncation or table
filter. The 16,384-row and 8,388,608-byte limits and the final NULL refusal
remain. SHA256 still covers the complete SQL UTF16 bytes, with the original
tag, column projection and primary-key order.

[Microsoft's SQL Server example](https://learn.microsoft.com/en-us/sql/relational-databases/json/use-for-json-output-in-sql-server-and-in-client-apps-sql-server?view=sql-server-ver17)
handles a `FOR JSON` reader with no rows by supplying an empty array. This
supports the encoding policy; documentation and cold parsing do not establish
the cause of this hosted failure.

Every actual snapshot transaction now runs two fixed read-only encoding
controls through the same serializer and strict two-field/tag/hash reader:

| Control | Original query | Independent expected UTF16LE SHA256 |
|---|---|---|
| Empty | Zero rows, no table access | `5EE9173B1B564E61AFD2359A897C180B5EA83875A91A28537DF4202AAD4B3AA7` |
| NULL-valued row | One row containing `Value=null`, no table access | `2C97B823D50ADDBA75158963C5368B8FEB50109D843D8255CF653AA8C1D6C8B4` |

These correspond to `[]` (4 UTF16LE bytes) and `[{"Value":null}]` (32 bytes),
without a BOM. They must not substitute for one another. Their commands and
readers use the same owned Serializable connection/transaction, obey the
existing deadline/cancellation, dispose through the existing first-failure
completion helper, and expose no JSON, SQL, connection or customer data in
diagnostics. They run before the complete original 72-template plan and do
not replace or add a stored snapshot tag.

All 58 table-row snapshots, including the three migration-owned tables and
history, strict inventory and 13 catalog snapshots remain. All 32 typed
unmapped columns, original primary keys, eleven seeded original rows, actual
migrations, permissions, ownership/cleanup, five SQL51048 preflights and
original source/effect graphs remain required. No fixture data is changed.

## Verification and delivery boundary

The added cold controls parse both complete actual snapshot plans with SQL160,
check the scalar-only fallback and retained bounds, pin the compiled ordinal,
and reject empty/NULL-row hash interchange, a foreign tag, lowercase hash and
NULL hash. These do not execute a SQL engine. The prepared two engine controls
and complete native 120 proofs are **NOT_RUN_UNQUALIFIED** on this candidate
until its fresh exact-head CI closes successfully.

Root verification passed 123 focused .NET cases (the 12 added encoding cases
and 111 retained cursor/template/guard/protocol cases), zero failures/skips and
no compiler warnings. Two actual project whitespace format/apply and verify
pairs passed. Focused log: 26,349 bytes, SHA256
`8bf66fc9de238db360cb7fcb157b6794b2b879cd9ee7f0900764194d39757f81`.
All Python coordinators and fixtures are unchanged. The expected full suite
is 3,871 (`6 / 520 / 25 / 3320`); it has not run on this checkpoint.

The complete six-path Git checkpoint, including both whole durable states,
requires independent SOURCE review before one same-PR #288 push. Root then
owns fresh native 120, the full .NET suite, all eight Build jobs, Governance,
the complete quality log and individual inspection of the six original
browser images through terminal status. Real terminal failures require repair
on the same branch/PR.

Private context17, journal20 and runtime8 preparations remain unpromoted.
Their later source integration and native121/122 gates must follow repaired
120 acceptance. Existing AI Gateway/provider configuration is reused; actual
model and tokenizer evaluation is **NOT_RUN_UNEVALUATED**. Worker coordinator
DI, all-contributor/provenance/race acceptance, full278/279/233, qualified ERP,
installer/deployment, production readiness and merge remain incomplete.
