# Terminal fixture transport repair — issue 278 / Draft 288

## Exact failed run and evidence

`b3c7dfb1637f83b8a85032640bbf43503f220fb0` is closed RED: Build 38112203893, native 114389913437 and quality 114395898042 failed. Six other prerequisites and Governance 38112203900 succeeded. The full .NET log records 3,450 passing tests (6/520/25/2,899), zero failures/skips.

The complete native log is 80,405 bytes, SHA256 `fb1a939fb66bfd580419e32fae768dcc5bacc3c5f49484a02a25f59f2c59cca1`. Frozen 117-marker parsing finds 98 required aggregates exactly once, 150 PASS lines, 19 missing aggregates and three failure markers. The new fixture reader fails with an unterminated JSON string at column 254 before building or running a terminal child. No new terminal write is qualified. Browser execution was not reached; there is no screenshot artifact.

The full quality log is 3,529 bytes, SHA256 `2151ec54d60db39fc834da06aba63b3e2f7153e000704b036768e76721ccb123`, independently read in full. It reports `LOCAL_RUNTIME_RESULT=failure` and six other successful prerequisites. All eight jobs and Governance were closed against the exact head. This is an implementation failure and receives a code repair on the same PR.

The failing query returns one large `FOR JSON PATH` cell through the shared `sqlcmd -W` reader. The observed failure matches the documented default 256-character large-variable display limit. Output width also defaults to 80 columns. [Microsoft sqlcmd documentation](https://learn.microsoft.com/en-us/sql/tools/sqlcmd/sqlcmd-utility?tabs=odbc%2Cwindows&view=sql-server-ver17)

## Bounded transport

The fixture query now selects at most five candidates, then emits six ordered GUID frames per candidate. Each frame is a fixed `varchar(40)` value: fixture ordinal, field ordinal and one canonical lowercase GUID. The actual result stays below both default widths. There is no large JSON result, CLI flag change or permissive line reassembly.

The parser requires exactly 24 frames (four fixtures), exact 40-character lines and every expected ordinal in order. Existing closed-field, canonical nonempty GUID, exact tenant/company and distinct source/event/batch checks remain required. The fifth candidate is still a sentinel that refuses excess fixtures. Malformed/truncated, missing/extra, reordered, duplicate, wrong-index or foreign data refuses before build, child execution or receipt writes.

The original 116 markers plus the terminal aggregate, all original snapshot predicates and child success requirements, protected environment configuration, timeouts, guards and cleanup remain unchanged. This repair touches only the new phase's fixture transport and its cold controls; the shared SQL reader and production code are unchanged.

## Local verification and remaining acceptance

Six terminal cold tests and 144 retained owned-stack guard tests pass (150 total). Two new controls exercise fixed frames/CRLF, documented-default JSON truncation, all malformed frame cases and pre-resource refusal. The complete four-child stub flow retains original snapshots, counts and cleanup checks. The actual query captured through that flow independently passes SQL160 grammar parsing without a database connection.

These stubs and grammar checks do not qualify actual terminal SQL. After full source review, the owning specialist must push a new exact head and close the complete 117-marker native log, full .NET suite, all eight jobs, Governance, quality and six original browser images. The newly prepared historical metadata types and signed-integrity primitive remain separately reviewed prerequisites; historical SQL, signed chain/cursor advancement, nonempty brain/multiple contributors/own inputs on SQL, full worker flow and issue 278/279/233 acceptance remain incomplete. Real model evaluation remains **NOT_RUN_UNEVALUATED**, using the existing AI Gateway only.
