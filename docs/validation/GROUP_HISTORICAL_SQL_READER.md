# Original terminal SQL evidence reader — issue 278 / Draft 288

Status: source preparation. Actual historical SQL execution, signed advancement,
production DI and full feature acceptance remain **NOT_RUN_UNQUALIFIED**.

## Purpose and authorization

`GroupBatchHistoricalSqlReader` reconstructs an already committed terminal from
its original immutable SQL evidence under separately fresh current Extract
authority. A legitimate source edit/recall or service, grant, account or source
version rotation does not replace the old committed graph. Disabled resources,
revoked rights, a changed worker credential or source deletion generation refuse.
The old lease is evidence of the actual original write window; it supplies no
present authorization. No artificial `Current` verdict, obsolete handle or old
current clock is constructed.

The internal caller must own the same pinned SQL Server Serializable transaction
with savepoints, no ambient transaction or MARS, and a clean tracker. Effective
terminal/work/ingress rights and the current directory are checked before reads.
The reader acquires the existing exclusive source application lock, then checks
the directory again. It has a linked two-minute deadline. Permissions, directory,
transaction affinity, tracker and real UTC are checked again before returning.
The caller retains responsibility for final authorization and atomic delivery.

## Original graph and bounds

- Terminal scope/batch selects at most two metadata rows. Exactly one must have a
  257–8177-byte manifest and 32-byte digest before the payload is selected; the
  actual operation and both byte lengths are reread and matched.
- The existing allocation reader restores the actual immutable dense allocation,
  original revision metadata, ingress receipts and source state. It reads no
  source plaintext and retains the 500-raw / 501-sentinel boundary.
- Every allocation message has a cutoff winner from immutable revision history,
  with Recall/Edit priority and revision ordering before its original cutoff.
  Today's mutable message heads are not used.
- Work metadata uses the 100-contributor / 101-sentinel boundary. Every manifest
  and 32-byte expected effect digest is length-checked before payload selection.
  Selected dispositions are bounded at 101 and raw dispositions at 501.
- The distinct actual original acquisition epochs of all contributors and the
  terminal are read in scope/batch, with an actual 102-row sentinel and exact
  cardinality. The existing original effect reader verifies every canonical
  expectation and complete original note/evidence/revision/outbox/item graph,
  including its existing protected-envelope preflight and 256000-byte limit per
  contributor. There is no key resolution or decryption.
- Own-input request origins retain the 2000-origin / 2001-sentinel boundary and
  the reviewed immutable dependency DAG. The separate sealed historical graph
  and proof verify the original allocation, contributor effects, unchanged v1
  terminal manifest, all 24 receipt fields and actual commit within its original
  acquisition window and real observed UTC.

`GroupBatchHistoricalTerminalEvidence` detaches the manifest and returns copies;
it retains the immutable original graph and typed interval/hash/commit metadata.
It supplies no signed-prefix proof, current lease or future write permission.
The reader owns no transaction commit, witness retirement, SQL mutation, cursor,
advance signature, key provider, AI client or DI registration.

## Local verification and evidence limits

323 focused tests pass: 20 new controls and 303 retained historical receipt,
own-input, manifest and original-effect controls, zero failures/skips/compiler
warnings. The new controls execute actual EF SQL rendering and SQL160 grammar
checks for scoped bounds, cutoff ordering and payload-length projections; malformed
metadata, overflow and duplicates refuse before encrypted payload selection.
Missing ownership, cancellation and foreign current worker bindings refuse with
the connection closed and tracker empty. A mutable-row/exported-byte probe proves
the returned historical evidence is detached.

Four C# files are formatted. SQL rendering, grammar and in-process metadata tests
are cold evidence; they do not execute SQL Server or prove restart/rotation races.
The reviewed parent `013a807` owns Build `38115318877`, Governance `38115318868`,
native `114398953852` and .NET `114398954048`. Its runtime excludes this reader.
Parent new terminal qualification is pending exact-head 117/all8/quality/images.
Full independent source review is required before pushing this new checkpoint.

Next: qualify the existing terminal writer, then add owned actual historical SQL
reconstruction/legitimate rotation/revocation/corruption probes. Implement the
signed sequential predecessor schema, latest authentic leaf/cursor fence,
outside-SQL retained-key resolution and atomic advancement/readback as specified
in `ADR_GROUP_SIGNED_TERMINAL_FRONTIER.md`. Nonempty brain, multiple contributors,
own-input SQL chains, gap/retry and automatic worker integration remain required.
Real model evaluation stays **NOT_RUN_UNEVALUATED** through the existing Gateway;
full #278/#279/#233 and merge acceptance remain incomplete.
