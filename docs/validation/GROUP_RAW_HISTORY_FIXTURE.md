# Owned 501-revision ingress fixture preparation

Issue278/Draft288 remains incomplete. This checkpoint adds only a test fixture
and its guard controls; it does not change shipping source, schema, four existing
native profiles, their markers, the original104 reference module or cleanup.
Parent d286e266 has scoped proof approval6102829342, independently FULL fetched
and read by root. It is now the owned remote111 CI head, Build38091747428/native
114329405046 ACTIVE; Gov38091747436 SUCCESS. Prior30a native108 is closed green:
Build38089462537/Gov38089462538/all8/native114322730774/quality114328713161,
FULL108 exactly once/164PASS, FULL.NET2711 zeroFailSkip and six ORIGINAL images
11684211653 independently inspected by root. This cannot qualify later raw111.

## Actual Core fixture contract

Use a separate enrolled source/account and the existing Core event endpoint,
credentials and source content key. The existing fixture creates two distinct
NewText messages. Their plaintext is empty, with the actual empty SHA256 digest.
Submit499 Edit events for the first wire message, each with a fresh revision event
identity. Require501 exact non-replay scoped Core ACKs in total: the first message
has revisions1..500, the second has revision1; committed sequences are1..501.
The final six SQL observations require501 revisions,501 receipts,501 outbox rows,
two messages, CommittedSequence501 and ScheduledThroughSequence0.

Generation of the499 extra events has a120-second monotonic deadline, checked
before and after every ACK. Every25 extra events, the test operator renews ONLY
the previously seeded, still-live lease with this exact tenant/company/account/
owner/epoch1 and a non-future heartbeat. Expired or foreign leases fail. This is
explicit fixture SQL, not shipping listener recovery: it creates no new epoch,
listener command receipt, coverage gap or continuity claim. Do not use it as
production deployment or listener qualification.

## Verified scope and next action

132 repository Python methods PASS after restoring every original128 method
unchanged. Four added methods cover501 unique events, exact message/revision/
sequence and empty-content shape,20 bounded own live-lease renewals, malformed
ACKs/lease/counts, both deadline boundaries and unowned/API/directory refusal
before callbacks or time. These are callback oracle controls without Core,
SQL Server, Docker resources or a model. Actual501 Core ingress is
NOT_RUN_UNQUALIFIED. No native112 or real-model acceptance is claimed.

Next add a separate owned raw-history executable/coordinator profile. Allocate
only0..500 and retain revision501 as an honest pending suffix. Independently
observe500 raw rows, the exact selected heads and ChangedAfterCutoff metadata,
atomic effect rollback/replay/detachment and runtime permissions. Preserve all
four existing profiles and complete retained snapshots. Whole-batch terminal
coverage/frontier, partial chunks, all-contributor revalidation, physical scoped
FK attacks, independent-process races, honest gap/backlog carrier, retry/context/
tokenizer/automatic DI/API/UI, issue279 and full233 remain outstanding.
Real-model evaluation remains NOT_RUN_UNEVALUATED; reuse the existing AI Gateway.
Do not supersede meaningful remote d286 CI or merge incomplete issue278.
