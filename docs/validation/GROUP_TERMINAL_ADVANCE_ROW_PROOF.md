# Authenticated terminal advance carrier — issue 278 / Draft 288

This checkpoint adds a pure, bounded comparison between the signed envelope and
all 26 fields of its SQL carrier. It supplies no SQL store, current authorization,
latest-leaf proof, key lookup, cursor update or worker integration.

## Proof and staging

`GroupTerminalAdvanceRowProof.Require` captures the five mutable byte-array
references once, checks finite lengths before copying, authenticates the complete
envelope with the exact scoped retained key, and compares every projected field.
The fingerprint covers the complete envelope including its MAC. All SQL UTC
offsets must be zero; nullable predecessor fields must have the exact root or
successor shape. Hash comparisons use fixed-time equality. Returned authenticated
metadata and bytes remain detached from the mutable EF carrier. Diagnostics are
fixed and inner-free.

A root refuses a supplied predecessor. A successor requires an authenticated
predecessor with identical scope, the exact previous version and Through, the
exact complete-envelope fingerprint and an advance UTC no later than its own.
`Stage` constructs the detached carrier from signed metadata and that predecessor;
it does not authorize signing or advance historical work. A key holder can sign
arbitrary shaped metadata. The future owned store must establish original graph
evidence, fresh Extract authorization, retained-key enrollment, authentic latest
leaf and atomic SQL readback before using this helper.

## Local verification and remaining qualification

109 focused controls pass with zero failures/skips/compiler warnings: 20 new
carrier controls, 38 retained integrity controls, five schema controls, seven
source-key controls and 39 contiguous-frontier controls. Both new C# files pass
formatter verification. Tests mutate each of the actual 26 record properties,
exercise root/successor and retained-key rotation, replace or omit predecessors,
attempt gaps, wrong scope/key, non-UTC and malformed/oversized/null arrays, and
check detached output. These use real HKDF/HMAC with synthetic metadata, without
opening SQL or calling a model.

An initial test used record equality on distinct byte-array instances. Its
positive assertion now compares scalar values and array contents; no gate was
removed. The final 109 run includes the captured-reference bounds fix.

Parent expansion `4ed6a18` is source-approved in receipt `6106050594`, independently
refetched and fully read by root (7873 characters). Its independent 3021 controls
are cold model/grammar/permission tests. Physical SQL schema and history remain
**NOT_RUN_UNQUALIFIED**. Current remote `013a807` owns Build `38115318877` and native
`114398953852`; Governance and six prerequisite jobs pass, including full .NET
3538. Native 117/all8/quality/six-original-image closure is pending at preparation
and excludes this checkpoint, the expansion and historical reader.

Physical carrier mutations, current-authority and retained-key races, the latest
leaf, cursor contract, atomic advancement, crash/replay and no-click worker flow
remain required. Full #278/#279/#233 and merge acceptance remain incomplete.
Model evaluation stays **NOT_RUN_UNEVALUATED** through the existing AI Gateway.
