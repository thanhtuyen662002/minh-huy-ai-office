# Owned cursor shadow lifecycle diagnostics

## Exact preceding failure

PR288 parent `ba66c1b840fb9c39325e1f8d44772109112cd7c2` completed all eight
Build38149003034 jobs. Six prerequisites and Governance38149002949 succeeded;
native114498290468 and quality114506787518 failed. The full .NET byte parser
proved3871=[6,520,25,3320], zero failures/skips and four successful test runs.
The frozen native120 parser observed99 required markers once,21 missing,
zero duplicates and five failure markers. No browser artifact was produced.

The actual failure was `shadow-create; invalid-operation; snapshot=-1`.
The original72-template snapshot and both fixed encoding controls returned
before this phase. This differs from preceding653's SQL51054 ordinal5.
The unique hosted lifecycle branch remains **UNPROVEN**. This checkpoint
adds diagnostic detail; it does not claim a database creation repair.

Native log83556bytes SHA256
`0f8801da8e57e893b8c810d2e6de12253d0accfb400ba71c445d565d6cfb603b`.
Quality log3529bytes SHA256
`438576e790f997344c7daf3ee50061ed6061cb8fc79fd67dd7bb15bcc442fc7c`.
Root read the failure/cleanup sections and the complete quality log.
Native/.NET complete-byte parsing is distinct from an entire verbatim read.
Full terminal closure and preserved previous ACTIVE paragraphs are public
PR288 comments6110748286 and6110747365 respectively.

## Closed observations

The initial ownership inspection uses five fixed phases: `shadow-absence-command`,
`shadow-absence-open`, `shadow-absence-execute`, `shadow-absence-result` and
`shadow-absence-check`. CREATE uses `shadow-create-command`, `shadow-create-open`,
`shadow-create-execute` and `shadow-create-result`. These are host-fixed codes.
No SQL, object name, result value, exception message/stack or credential is emitted.

The result phases include strict result parsing and resource disposal. The
absence-check phase includes its independent connection disposal. They do not
individually distinguish a cleanup error from the immediately preceding operation.
No phase is advanced by cleanup callbacks, so the existing first-error scope and
outer capture retain the preceding observation if cleanup also fails.
The later ownership inspection during cleanup leaves `owned-shadow-cleanup`
intact. The exact SQL51070 existing-database refusal now binds to the CREATE
execute phase, preserving the refusal to remove a preexisting database.

All commands are still built before opening their fresh, nonpooled connections.
The typed owner reader, canonical operator connection, strict database grammar,
exact ownership token/DB_ID, lost-reply recovery restrictions,45-second independent
cleanup deadline and first-exception retention are unchanged. CREATE's scalar
still requires an actual int greater than4; separating its await and assertion
adds an observation only. Every72 full snapshot,32 typed unmapped columns,
original PK/ordering, row/byte caps, canonical JSON and empty/null-row engine
controls remains required.

The Python boundary accepts only these nine added phase literals. It still
requires a terminal failure and complete original closed transcript. Unknown,
private-suffixed, newline-injected, forged-success or cleanup-replaced transcripts
refuse without echo. Every13 old frontier test method is AST-identical; two
additional methods exercise these observations. Actual native execution must
identify the hosted branch before a corresponding repair can be claimed.

## Verification and remaining gates

Focused owned-cursor123 .NET controls,144 owned-stack/15 frontier/5 network/6
terminal Python methods and runtime format apply/verify are the local scope.
They qualify cold behavior and preserved protocols only. The successor's
native120/full3871/all8/Governance/quality/six original browser images are
**NOT_RUN_UNQUALIFIED** until captured at its exact Git HEAD.

Complete frozen six-path source, including both whole durable YAML files, needs
independent SOURCE review before the next same-PR push. Root owns that CI through
terminal and must repair a real failure on this branch. Context19 promoters stay
dormant because parent120 is red. Context/journal/runtime/response experiments
remain private, with their immutable archives and prior preliminary reviews
preserved. Actual121/122, tokenizer/provider profile, Worker DI, automatic
coordinator, full278/279/233, ERP/deployment and merge are incomplete.
Real model quality remains **NOT_RUN_UNEVALUATED**.

## dc5 terminal closure and scalar versus cleanup observation

The preceding sections are historical preparation for dc5. Actual HEAD
`dc5e62f058e14003a6015ad0073d47e8444a68b6` is now CLOSED RED:
Build38153824595/native114512562985/quality114520928815 failed;
Governance38153824577 and all six prerequisites succeeded. Full .NET3871
passed without failures/skips. Native120 has99 once/21 missing/0 duplicates,
five failure markers. No browser artifact exists. Entire quality was read;
entire native/.NET verbatim reads are not claimed. Complete old HANDOFF
paragraphs and terminal evidence are preserved in PR288 comment6111365786.

The actual error is `shadow-create-result; invalid-operation; snapshot=-1`.
`ExecuteScalarAsync` completed. Its strict scalar validation and subsequent
command/connection cleanup shared that phase. The unique cause remains
**UNPROVEN**; no SQL behavior repair or provider execution is claimed.

The successor keeps the exact CREATE SQL and actual `int > 4` requirement.
A closed result validator uses only five fixed observation literals:
`shadow-create-null`, `shadow-create-dbnull`, `shadow-create-non-int`,
`shadow-create-system-id`, and `shadow-create-validated`. NULL, DBNull,
other CLR types and integers at most4 still throw. Accepted integers advance
the phase before cleanup. Cleanup never overwrites that phase, so a scalar
refusal followed by cleanup failure retains the primary scalar observation;
successful scalar validation followed by cleanup failure is distinguishable.
It does not yet identify which cleanup resource failed. No result value,
type name, conversion, ToString, SQL identifier or private exception is logged.

Fourteen new cold .NET cases cover refused shapes including hostile formatting,
valid int5/intMax, and both first-error cleanup sequences. The two existing
Python diagnostic methods extend only their closed phase inputs and private
lookalike refusals; the other13 methods remain AST-identical. These controls
cannot qualify actual SQL, a migration, or a forged successful transcript.
Successor native120/all8/full3885=[6,520,25,3334]/quality/six original browser
images remain NOT_RUN_UNQUALIFIED before its owned CI execution. Full changed
Git SOURCE review is required before pushing. Private53 is still unpromoted;
the dc5 context19 promoter is permanently dormant for the failed parent.
Actual model remains **NOT_RUN_UNEVALUATED** and full278/merge incomplete.
