# Companion-bound corruption fixture repair

## Actual failed run

Exact e8 Build38778313333/Governance38778312633 attempt1 is terminal FAILED,
closure6199096035. Hosted .NET5191 and Governance passed. Native116571614755
failed with SQL547 during the corruption UPDATE; quality116588843667 failed
only because local-runtime failed. No browser images were produced. The full
native log is97,294 bytes, SHA256
`b826a9555072a10b74b72e99a3bc218473c67f084c50d32aa6825729329dac53`.

The actual log names neither case nor constraint. Source shows the old matrix
starts with a row-SHA edit, while the newly committed model-attempt companion
references that exact sidecar SHA through
`FK_GroupModelAttemptAdmissions_ExactOriginalAdmission`. The fixture expected
four physically incompatible hash-changing updates to persist. That source
inference explains the incompatible expectation; the hosted547 is not
retroactively relabelled as an observed exact constraint receipt.

## Separate fixture repair48

The original sidecar, companion, schema, constraints and runtime permissions
remain intact. No companion deletion, FK disabling, cascading update or grant
widening is used. The matrix now distinguishes its enforcement layers:

| Cases | Required result |
| --- | --- |
| Row SHA only; three altered payloads with recomputed row SHA | Four physical update refusals. A fresh explicit transaction must receive547 naming the exact quoted companion FK. Allowed update, missing row, wrong constraint/error, rollback failure or unexpected output fails. |
| Three altered payloads retaining original row SHA; future-created time; legitimate newer same-scope claim metadata | Five updates must persist and affect exactly one row. The actual runtime must still recover the accepted output FIRST and refuse the sidecar. SQL rejection is a failed persisted case. |

Each physical case rolls back before emitting its fixed receipt, verifies no
transaction remains, compares complete unchanged graphs/permissions and
requires healthy runtime resume. Raw ERROR_MESSAGE stays inside SQL; the
Python boundary accepts one fixed case receipt only.

Every persisted case retains exact restoration in `finally`, including after
partial mutation or failed runtime child. Mutation/restoration output must be
empty. Original bytes, SHA, timestamp and claim metadata must be restored,
full graphs/permissions compared, and healthy resume required. The original
outer container/image cleanup gates still precede aggregate publication.

The three byte variants have invalid internal checksums. They demonstrate
persisted altered-byte refusal, not three distinct magic/parser branches.
The original5bf six-persisted-case proof stays historical and separate. The
new aggregate precisely claims four exact FK cases and five persisted cases;
its source/parser/capture must be rebound before a new run can qualify it.

## Verification and remaining gates

Nineteen inert Python methods pass:6 corruption,10 parent-driver and3 guard
counterfactual controls. They reject generic547, wrong FK, allowed update,
private/extra output, SQL-blocked persisted cases and restoration/graph/child
failures. These controls execute no SQL or model.

All C# and production source bytes are unchanged from separately reviewed44.
The existing exact44 full5357 `[6,584,25,4742]`, focused7 and format evidence is
inherited; no fresh .NET rerun is claimed for Python/document changes.
Repair44 closes RBP40-1 only; original40 remains WITHHELD. This fixture repair
requires its own exact source/durable review and fresh actual native145,
full5357/all8/Governance/quality/six-original closure. Old e8/144 and mocks
cannot qualify its nine physical/runtime cases or restored-batch phases.

Actual repaired SQL is NOT_RUN_UNQUALIFIED. Production catalog remains empty;
real model evaluation is NOT_RUN_UNEVALUATED. First originals, multichunk,
invalidated-original continuation, scanner/shipped Worker, queue/restart and
no-click278, internalIT279, full233, readonly ERP, Windows and deployment
remain open. Preserve all current source/brain/claim/permission denials.
