# Owned terminal receipt runtime gate — issue 278 / Draft PR 288

## Reviewed prerequisite

The seven-path owned consumer checkpoint87e163b is source-approved by
[receipt6105354729](https://github.com/thanhtuyen662002/minh-huy-ai-office/pull/288#issuecomment-6105354729).
Root independently refetched and read the complete5595-character receipt,
SHA256809b7d772bfb929ec0518504488b14cd9ece172a74566e940146ffcf1e08e02d.
Its368 passing controls qualify source behavior only. Actual SQL remains a
separate required gate.

## Additive native phase

`smoke-local-stack.py` runs the new terminal phase after the complete original
ingress/reference/automatic/raw-history gates have returned successfully.
All original116 markers, original three dependency checks per fixture and
their complete source/effect/claim/brain/key/pending501 oracles are retained.
The new phase adds one aggregate terminal marker; it does not replace an
original marker with terminal success.

Only the existing disposable GitHub CI fixture may run this phase. Its guard
precedes configuration, SQL and Docker. The metadata query has a five-row
refusal sentinel and requires exactly four distinct scoped source/allocation
fixtures with the original two-source, zero-selected-brain manifests. A closed
five-ID configuration is passed through stdin. The existing ephemeral runtime
DB password is passed by environment name, never argv or stdout. The container
is read-only, drops capabilities and uses the existing owned identity/removal
checks. No source key is resolved or passed to this phase.

Each actual runtime invocation:

- Preserves the complete original note/outbox/disposition/raw graph and all
  original acquisition receipts.
- Acquires an additional lease and calls the shipping terminal store. A probe
  observes the staged receipt and actual SQL row after SaveChanges, then moves
  the fixture clock to expiry.
- Observes Serializable isolation, the real Exclusive source APPLOCK at
  savepoints and the actual SQL row disappearing after rollback. The staged
  receipt must be detached before a single expiry-witness property is saved.
  The actual witness is read back after return; clock rollback cannot reuse it.
- Acquires a new lease and commits the actual receipt, then independently
  verifies all receipt fields, original terminal acquisition, SHA256 and the
  complete257-byte one-contributor manifest layout. The original pending501
  observed watermark remains distinct from the allocated500 prefix.
- Replays under that lease and a later lease, requires the identical original
  result, and refuses a new terminal nonce for the already completed batch.
- Executes24 fixed-column no-op UPDATE probes as the runtime principal; every
  attempt must receive SQL229 and leave the original receipt unchanged.
- Requires no new frontier row, no key reads/writes and no duplicate receipt.

The Python orchestrator compares ordered, tagged full-record SQL digests of
the original scoped sources/effects/raw/frontier, source/account/service
metadata and portal graphs before and after every invocation. Missing results,
duplicate/wrong markers, stderr, nonzero exit, mutation, wrong receipt counts
or cleanup failure refuse aggregate success.

## Local verification and remaining gates

833 selected .NET controls pass against the promoted sources, including49 new
independent manifest/receipt observation controls and784 retained controls.
All have zero failures/skips/warnings. Three changed C# formatting checks pass.
The four new Python tests include malformed/duplicate/foreign fixture metadata,
guard-before-resource checks and ten orchestration failure cases; all pass.
The144 retained fixture-boundary Python tests also pass. These are cold,
InMemory or mocked process controls; their success is not SQL execution.

The native terminal phase is **NOT_RUN_UNQUALIFIED** until a fresh reviewed
exact-head run executes it on SQL Server and its complete logs are checked.
Parent691 CI contains only the earlier original116 profiles. Multiple
contributors, nonempty selected brain, own-input SQL chains, payload corruption,
permission/concurrency races, cancellation/session restoration, authenticated
contiguous cursor, gap/retry policy and automatic DI/API/UI remain incomplete.
No partial merge or production acceptance is implied. Actual model evaluation
is **NOT_RUN_UNEVALUATED** and must reuse the existing AI Gateway/configuration.
