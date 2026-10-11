# Shadow cursor test portability repair

## Proven hosted failure

PR #288 HEAD `fcb758f2f2ef8b0d4a2a405b14593b711a40774a` is not green. Build
38131017513, .NET job 114445185337 completed with two test failures. Governance
38131017482 passed. The full original .NET log is 1,025,320 bytes, SHA256
`991ae5b33dd85634c1ed4ff4f490fc70ba1742064d33e4edc5116825ecb7066c`.

The four projects reported 6, 520, 25 and 3,292 tests: 3,841 passed, two failed,
zero skipped. Build reported zero warnings and errors. Both failures were
`DirectoryNotFoundException` from Windows scratch exports in
`ReviewerShadowCursorSqlPrototypeTests`:

- `RealRecursiveFkClosureIsExactlyElevenTablesIgnoringAdvanceSelfEdge` exported
  a diagnostic model projection after its FK closure and closed connection assertions.
- `EveryGuardCopyAndFullSourceShadowSnapshotParsesSql160WithoutConnection`
  exported rendered SQL after parsing it and before its final closed connection assertion.

Linux interpreted the drive-prefixed scratch paths relative to the test binary
directory. This is a proven source defect missed by the prior source review;
it is not runner infrastructure noise. The earlier Windows-only local pass is
retained as historical evidence and does not qualify portability.

## Minimal repair and verification

Remove the two diagnostic exports, their unused projection, and the unused
JSON import. All 25 prototype cases, SQL160 parsing, real recursive FK closure,
scope checks and closed connection assertions remain unchanged. These tests
need no filesystem output. No shipping runtime, schema, provider loader,
workflow, permission, guard or assertion changes accompany the repair.

Shared verification: 95 shadow controls passed, zero failures, skips or compiler
warnings. Their 2,389-byte log has SHA256
`ded1c2e9ce52487edbe3a5d8652b07e3819d575b9d9a042233f231600ab23a60`.
Changed-file whitespace formatter verification passed with an empty log.
The focused controls include every retained prototype case. Exact same-branch
Linux CI must still confirm the repair; no retry of the defective HEAD is needed.

Independent bounded audit read all 15 frozen checkpoint paths and adjacent
Persistence.Tests/GroupReference.RuntimeProof write sites. It found no additional
Windows scratch export. Its complete 3,552-byte report has SHA256
`930dd54974d4b5812b6b6e8aa99c4d02fa24698b5c6700829246dd74644dd6a9`;
the 5,805-byte evidence has SHA256
`e56292b6a06416838f3834555f01011319580db4acad6171f11b8f7e2ccabfd0`.
Frozen repair review remains separate from this diagnosis.

## Owned CI and remaining acceptance

At this repair preparation checkpoint, native job 114445185134 remains active
for the original 120 required proofs. Preserve that run through its terminal
outcome, inspect any additional real failure immediately, capture the complete
native/.NET/quality logs and inspect the original six images if emitted. Then
push the independently reviewed repair on the same branch and own its fresh
120-proof, 3,843-test, all-eight-job and governance closure. No early push may
cancel meaningful owned proof execution.

Actual shadow migration/runtime120 is not yet qualified by this checkpoint.
The prior signed cursor119 parent remains separately qualified. Full issue278,
automatic worker integration, successor/race/multiple-contributor/history/own-input
proofs, internal notification279, customer ERP qualification and production233
remain incomplete. Real model evaluation is `NOT_RUN_UNEVALUATED`; the existing
AI Gateway remains the provider boundary. This repair gives no merge or customer
activation acceptance. The current PR HANDOFF records subsequent CI outcomes.
