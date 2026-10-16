# Owned terminal frontier store — issue 278 / Draft 288

Source checkpoint only. This store is not yet wired to production worker DI or qualified on an actual SQL engine. Its acceptance requires the physical and concurrency probes below.

## Owned call and idempotency

`AdvanceAsync(scope, batchId)` owns two pinned Serializable transactions and the existing transaction-owned source APPLOCK. Exact trusted tenant/company/worker, SQL Server, no MARS/ambient/current transaction, clean tracker and a closed connection are required at entry. Batch identity is the idempotency key: replay authenticates that original advance and original history and returns the same version/time/fingerprint, including when the batch is older than the latest leaf. It never advances the next batch as a side effect of retry.

The first unit establishes current Extract/directory/effective complete key/advance/terminal/work/ingress/cursor rights, reads bounded actual latest TOP2, cursor/target/immediate target predecessor and the complete enrolled-key inventory (4096 plus a rejecting sentinel). Envelope/hash lengths are selected before payloads. No MAX terminal sequence supplies progress. Commit/dispose restores and closes SQL before any provider await.

Every enrolled key is resolved through the existing exact-scope provider and must match its immutable material fingerprint, including older keys outside the leaf pair. Active write material is resolved through the same provider and checked if already enrolled. A provider that ignores cancellation cannot extend the owned wait indefinitely: eventual faults are observed and late material is disposed. There is no independent provider/configuration/client/secret store and no key bytes or secret references in SQL.

The second owned unit checks unchanged fresh authority and the exact entire prior snapshot after key await. Every actually used envelope key must be enrolled. It authenticates the actual latest leaf/all26 carrier fields and exact cursor; previous complete MAC and only relied prior projections provide the immediate inductive anchor. Positive unchained/rolled-back/stale/forged newer cursor/leaf refuses. Deletion-generation changes retire prior completion; legitimate credential/grant/account/source-version rotations between calls can preserve original immutable history.

## Original evidence and atomic write

The actual locked historical SQL reader reconstructs the target's original allocation/raw/cutoff/acquisition/contributor/effect/own-input graph and terminal bytes under separate current Extract authority. No expired handle, Current verdict, old fabricated current clock or today's source/brain fingerprints are manufactured. A new advance must start at the latest authenticated Through (zero at trusted empty genesis) and increment its version exactly once. Noncontiguous later batches refuse.

Sign occurs only after original history, current authority, complete material inventory and latest leaf agree. New key enrollment, signed advance and cursor are saved atomically after the source lock and effect savepoint. Actual untracked SQL readback must equal every expected row/key/cursor field; all keys/leaf/history/current directory/permissions and exact final snapshot are rechecked. The whole owned unit rolls back on any failure and detaches every staged effect. This bookkeeping path writes no lease-expiry witness or model/customer/IT output.

One monotone clock wrapper covers every observation, including observations inside the historical reader. A rollback there aborts immediately even if a later observation would recover. Signed UTC binds the cursor; equality of instants with a nonzero offset cannot bypass leaf checks. Returned result is stable metadata only.

## Local verification and remaining gates

Final private full Persistence: **3112 PASS = 44 new store + 15 leaf + 3053 retained**, zero failures/skips/compiler warnings. Both new files pass actual-project format. New controls exercise actual EF SQL scope/TOP/order/length projection grammar on closed connections; malformed/oversized/null/duplicate advance and finite ordinal key metadata; all26 carrier, all14 registry and every cursor snapshot projection; rollback/offset clocks; ignored cancellation and late key disposal; private provider faults/nulls; cold foreign/dirty/ambient/MARS/open-session/non-SQL/missing-dependency/cancelled entries before SQL or key resolution. SQL query strings/inert connections are cold evidence, not SQL execution.

Initial private test import ambiguity was corrected to explicit System.Data isolation. A local overlapping build hit testhost file locks; it was rerun sequentially after the owned testhost finished. Final full run above includes the bounded resolver/clock fixes; no shipping gate was removed. Shared focused verification: 157 PASS = 44 new + 113 retained, zero fail/skip/compiler warnings; both new files also pass shared format verification. Prior 3105/3107 passing runs predate those final fixes and are not substitutes for3112.

Required next qualification: actual immutable key/advance schema and effective rights; authentic original history after lease expiry and edit/recall/authority rotation; atomic root/successor/readback/rollback/lost-reply; all-enrolled missing/rebound keys, key-await changed snapshot and current revocation; physical partial-NULL/FK/owner/column tampering; signed latest cursor migration/contract; multiple intervals and explicit continuation, gaps/retries, nonempty brain/multiple contributors/own-input, worker DI and no-click flow. None has been claimed by these cold tests.

Parent0ceaa21 is full source-approved6106406446, ROOT independently FULL6968chars SHA25640e40de5819b1821de8b887714284744bc894010bead8bba13a5d58b497c2017. Actual remote77f Build38118871590/native114409281277 remains owned/nonterminal at preparation, six prerequisites and Governance38118871592 successful. ROOT full .NET114409281240966071bytes SHA2565484867f40c494a3edac7fd8e4ecf0ac7901607e6c9f73f8b505be69f971a644:3604PASS6_520_25_3053, four successful runs/zero fail/skip. That head excludes leaf/store and its retained native117 phase cannot qualify original-history/keys/frontier SQL.

Actual history/keys/chain/frontier SQL: **NOT_RUN_UNQUALIFIED**. Real model through the existing Gateway: **NOT_RUN_UNEVALUATED**. Full278/279/233/customer/production/merge remain incomplete.
