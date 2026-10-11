# Owned shadow failure diagnostics

Exact commit 70699683df796a2f6183e5d13b96da32bf88b7b3 completed with six prerequisite
jobs and Governance successful, including 3,843 passing .NET cases. The native
job 114457110296 failed with `phase=operator-configuration`; quality job
114463182232 failed accordingly. No browser artifact was produced. The complete
native log is 83,488 bytes with SHA256
`152868a79fbec95b9735e3dd3d3ab7f8ba868053890963cf001966a67df45da2`.

The bounded 120-marker scan found 99 required markers once and 21 absent, with
five failure markers and 151 overall PASS lines. The prior twelve root children
ran before the failing owned shadow child. The root, signed-cursor and migration
aggregates were withheld. This is not 120-marker qualification.

The old phase covers connection validation, generated shadow identity, connection
derivation, and the initial complete source snapshot. It cannot identify which
operation failed, and the original exception message/SQL error was intentionally
not retained. The unique root cause is **UNPROVEN**. Successful cold templates,
network checks and operator normalization do not establish actual snapshot SQL
success.

Independent source inspection found a deterministic blocker: all thirteen
catalog templates expected `catalog:<name>`, while their generated SQL returned
the unprefixed `<name>`. The unchanged strict result reader rejects this.
Two new regression cases feed the result tag extracted from every actual
generated source/shadow SQL template into the actual result reader; both failed
against the original producer. The producer now returns the original expected
prefixed tag. This fixes a proven source defect consistent with the hosted phase;
the unique first hosted exception remains unproven because its original ordinal
and error were not retained.

This repair adds a distinct original-source-snapshot phase, a bounded snapshot
ordinal (-1 before execution, 0 through 68 while reading the fixed 69 templates
and until that snapshot completes successfully),
and fixed exception categories. SQL exceptions expose only their numeric error
number within -2 through 65535; other SQL numbers use a fixed category. No error
message, inner exception, SQL text, table tag, connection string, password or
source content is printed.

The coordinator recognizes only the exact failure envelope, known phase,
canonical bounded code/ordinal and final failure line. Unknown or extra data
falls back to the original fixed error without echo. Diagnostic output cannot
qualify a successful child or replace either original required success receipt.
Existing legacy failure envelopes remain accepted as failures.

Only the thirteen catalog result labels change; selected catalog columns and
hash payloads remain identical. No snapshot inventory, digest, transaction, migration,
authentication, ownership, create/drop rule, credential boundary, success
receipt or final cleanup is changed. No shipping provider or product path is
changed. Native execution of this diagnostic revision remains **NOT_RUN** until
its own exact CI run. Source/root-cause and model qualification are separate.
