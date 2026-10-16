# Native observation of automatic receipt manifests

Issue278/Draft288 is incomplete. Remote ec3e8f79 owns active native112;
FULL.NET2774 is qualified and Governance is green. That head predates the new
manifest schema/source and cannot qualify it. Keep meaningful active CI alive.

This separate preparation adds an independent observer to the existing mixed,
host-only, empty NoWork and raw-history fixtures. It reads the actual automatic
receipt, original allocation and source dispositions. Format1 must have exactly
274 bytes for those two-source/zero-brain fixtures, bind all five scoped IDs and
the allocation cutoff, and contain both exact canonical message IDs/revisions.
Authority, coverage and source fingerprints must be present. A separate complete
blob digest is retained through replay and every original-graph check.

Each existing post-flush expiry probe checks that actual manifest before advancing
the clock. Existing rollback probes then require zero receipts/effects/raw rows,
clean detach, the independent expiry witness and clock rollback denial. Existing
seven-table graph digests already include both new receipt fields. The committed
runtime executes two closed no-op UPDATE statements against the actual columns;
both must fail with SQL229 and preserve the exact manifest digest.

After all four independently observed commits and their resource cleanup, an
additive parent assertion checks four distinct version1 receipts/batches/operations,
exact size/magic/counts. One new aggregate marker extends frozen112 to future113.
Every original reference, four-profile coordinator, history coordinator, resource
cleanup and prior112 marker remains. No new executable mode, effect row, provider,
secret configuration, source cursor or permission grant is added.

Local verification:23 independent observer controls, complete2286 Persistence
tests and136 Python controls PASS; no failures/skips. Release build has zero
warnings/errors, six changed C# files pass format verification. All25 actual
unowned Release entry points refuse before invalid config or resources. Original
reference/four-profile/history coordinator bytes are retained. These controls
prepare a real SQL test; they are not real SQL or model acceptance.

The observer checks storage, scoped contributor identities, atomic rollback,
original replay and column immutability for these fixtures. It does not independently
reconstruct the shipping metadata fingerprints or exercise nonempty brain
dependencies/current dependency reconstruction under locks. Those native checks,
maximum contributors, independent-process races and malformed receipt qualification
remain required. Future113, all8 and original browser screenshots are unqualified
until exact-head terminal CI is inspected.

The manifest records pre-effect inputs. A future operation that updates a
contributing note must validate its explicit input-to-post-effect head chain;
requiring the input head to remain current after that same update is insufficient.
The current automatic writer creates new notes; no terminal consumer/frontier is
enabled. All raw coverage, all contributing chunks, contiguous frontier, context
and token budgets, provider/tokenizer pinning, honest gap/backlog handling, no-click
DI/API/UI and real-model100/30 remain incomplete. Model=NOT_RUN_UNEVALUATED.
Full278/279/233, customer production and merge acceptance remain withheld.
