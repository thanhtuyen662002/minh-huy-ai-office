# Contiguous terminal frontier prerequisite — issue 278 / Draft 288

`GroupContiguousTerminalFrontier` is an internal metadata planning kernel. It advances only through the completed prefix of the exact allocated intervals after the current terminal cursor. A completed later interval cannot jump over a pending earlier interval. Neither the scheduled high water nor the maximum terminal sequence establishes completion.

Both actual input enumerations are frozen with a 100-row bound and refusal at the 101st row, independent of collection Count/indexer/CopyTo. Allocations must be scoped, uniquely identified, dense, non-overlapping and contiguous, with the existing inclusive 1–500 raw-revision bound. A short window must account for every scheduled interval. A full 100-row window may stop below the scheduled marker; its plan explicitly retains backlog and requests continuation only after the loaded prefix is complete. Input order does not affect the result. Cancellation and fixed, inner-free failures are preserved.

Terminal metadata must match the exact scoped batch interval, a unique nonempty operation, version 1 and a non-placeholder uppercase SHA256 carrier. **This validates shape only.** The future caller must authenticate each immutable versioned terminal manifest and its complete original-effect graph, then reread the high-water markers, allocations and terminal inputs inside the owned SQL write unit. This kernel grants no completion or write authority.

Scheduled backlog, unscheduled committed work and coverage-gap state remain separate in the plan. A gap does not prevent already known contiguous work from progressing. The gap flag is retained metadata; an automatic zero-message attention carrier and backlog/retry workflow are still unimplemented.

## Verification and remaining work

39 focused controls PASS with zero failures/skips. They cover contiguous prefixes, out-of-order completion, permuted inputs, pending holes, inclusive 100-row continuation, refusal at 101 rows, invalid scope/identity/version/hash/intervals, high-water ordering, long.MaxValue, deceptive collections, disposal, fixed enumeration failures and pre-enumeration cancellation. The two changed C# files pass formatting. Synthetic terminal carriers in these tests are shape fixtures, not authenticated completion receipts.

No DB schema, stored terminal manifest, terminal writer, SQL frontier update, DI, completion endpoint or no-click application path is added. Exact remote67605e1 native116 is separately owned through terminal and excludes this local kernel. Actual model evaluation remains **NOT_RUN_UNEVALUATED**. Full278/279/233 acceptance, production activation and merge remain incomplete.
