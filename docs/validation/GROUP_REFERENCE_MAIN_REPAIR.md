# Group reference main integration follow-on — issue284

PR283 merged at main `6c746b929c2b04b8a21453680cbe940161e465c9` after full
runtime review6095552242, final documentation reviews6095561405/6095593001,
exact final408 Build38037330512/Gov38037330658/all8 SUCCESS. Root inspected the
selected80 required native/browser markers,136 overall PASS lines,2027.NET and
1334web tests plus all six original current images from artifact11664696555.

## Exact merged-main result

Build38038487497/actual114173836023/quality114176087671 FAILURE;
Governance38038487489 and all six prerequisites SUCCESS. The first five native
reference stages and retained group/listener/read/spool/managed SQL controls
passed. At08:50:07UTC the application-restart baseline failed its separate
`deliver == 3 && consumers == 1` assertion after ACK==2 had been observed. No
actual numeric snapshot was emitted; the underlying cause remains unproven.
The pending original-reference RabbitMQ process restart and Chromium were not
executed on this main run.277 remains open and main acceptance is incomplete.

## Narrow repair candidate

Issue284 uses a follow-on branch because PR283 is already merged. Both baseline
and resumed application delivery now require original exact ACK/delivery/consumer
counts together in one management snapshot within the existing30-second wait.
Failed observations emit only fixed phase plus numeric expected/observed counts;
the first observation error remains chained. Original persistent publication,
empty queue, count2, full8 graph, portal isolation and restoration remain required.
Literal pending RabbitMQ restart proof is unchanged. No shipping src/apps/schema,
CI workflow, runtime timeout or receipt oracle change.

Local44 Python tests PASS, including12 actual-block delayed-positive and
disjoint/extra-ACK/extra-delivery/missing/extra-consumer probes across baseline and
resumed phases, plus all retained ownership/reference/cleanup controls.
AST/YAML/diff checks and independent frozen review are required before push;
actual renewed SQL/broker/Chromium and exact PR/main gates remain required.

Attachment was fully reread after283 merge. Its corrected requirements are
already represented by the accepted customer-group-intake contracts. Private
278 design and30 independently prepared synthetic labelled windows exist; they
are not implemented/evaluated outputs, semantic accuracy, account access or full
278 acceptance. Continue278 automatic SQL brain/notes and279 IT notification
only after accepted277/main. Full233 stays active.
