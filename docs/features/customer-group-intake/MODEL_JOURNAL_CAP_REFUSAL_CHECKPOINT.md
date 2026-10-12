# Journal cap proof: bounded source-lock refusal

## Actual predecessor

Exact HEAD `5b69bcec1aca3b15552d08b1c9c65ea052496142`, PR #288:
Build38203250982 / Governance38203251025, attempt1. Native114661044158
failed at model-journal-cap with `phase=cap-start; kind=source-lock`.
The full native log84126 bytes hashes to
`ab06ee04bcb1662e6f9eb7c0baa7115c6bf0278312212a7a31ec16f9446a1015`.
All eight jobs reached terminal; six prerequisites/.NET4194/Governance pass,
quality114669269755 fails only LOCAL_RUNTIME_RESULT. Frozen124 accounting is
99 once /25 missing /0 duplicate /5 failure patterns. Input-plan and subsequent
SQL/browser proofs were not reached. Complete prior handoff and exact logs
are preserved in closure6118047452. The engine return number and unique
underlying cause were not observed; earlier dba causality remains unproven.

## Source repair

The old oracle requires all four concurrent journal starts to return successfully,
although the unchanged shipping source lock can refuse a contender after its
bounded wait. This is a source-derived oracle mismatch, not an engine diagnosis.
The proof still runs four independently prepared current contexts at the same
barrier. Each calls TryStart exactly once; there is no retry or refill.

Only the exact shipping InvalidOperationException type and source-lock message
at the start operation may become a losing outcome. A live caller, unchanged
tracker, no current transaction, closed connection, one source key read and zero
key writes are mandatory. Cancellation, authority/journal failures, private or
same-message subclass errors, dirty/open state and null success remain fatal.
No specific negative lock result is inferred from the closed exception text.

All four starts finish before the winning context appends its interrupted failure.
Its original permit/context stays bound. Exactly one real third-ordinal permit
with the original epoch must exist, every normal result must report budget3 and
no replay, and each losing nonce must have zero start/failure rows. The complete
journal still must contain exactly three starts/two failures. A fresh fourth
attempt must be exhausted without changes. All nine exact named physical SQL
constraint/permission attacks, final original state/effect/retained-scope checks,
ownership guards, configured5s lock and historical success markers are retained.

## Verification and limits

Fresh full solution4217=[6,520,25,3666] passes with zero failed/skipped/warnings.
The38 focused controls include23 new cases and15 retained diagnostics/ownership
cases; they overlap the solution total. Both changed CSharp files pass format.
The initial511 ENDOFLINE diagnostics remain retained; only the selected proof's
CRLF was normalized to LF afterward. All canonical source tokens remain equal
to the tested source. No fresh full solution run after that line-end-only edit
is claimed; the final format verification is empty and successful.
All Python remains RAW equal to the parent; prior189 is inherited, not a fresh
Python execution. Production/schema/configuration/Gateway/WorkerDI are unchanged.

These are cold callback/source controls, not real SQL concurrency or physical
deadline evidence. Current immutable5 and actual Git SOURCE review are required
before a single SAME288 push; successor native124/full4217/all8/Gov/entire quality
and six ORIGINAL images must close through terminal. Current39v2 result source
is preliminarily clear but cannot promote over the red parent. Actual model is
NOT_RUN_UNEVALUATED. Full278/279/233/history/profile/tokenizer/no-click/ERP/
deployment/release/merge remain incomplete.
