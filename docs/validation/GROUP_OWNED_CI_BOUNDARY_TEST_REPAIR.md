# Owned CI entity-boundary test repair

Issue278/Draft288 remains incomplete. Exact d286e266 Build38091747428 has a
terminal .NET job114329405159 FAILURE; root fetched and inspected its FULL log.
It executed2761 tests across6/520/25/2210, with2760 passing, one failure and zero
skips. The known-length entity boundary case failed at line208: the plus-one
error had IsTransient=true after approximately two seconds. The log did not
retain that exception's fixed message; its precise cause is unproven. Native
job114329405046 is still running and must be preserved through terminal.

## Narrow test-only change

Only the exact262144-byte/plus-one entity boundary test now has a finite30-second
test option, allowing the concurrently scheduled blocking suite to finish while
keeping every byte limit and both known/unknown length cases. This is a test
scheduling hypothesis, not a confirmed CI causal diagnosis. The unchanged
shipping timeout remains60 seconds; the shared test default remains2 seconds
and separate250ms deadline/capacity/cancellation probes retain their budgets.

Strengthen the actual copy oracle: exact length must copy once; known oversized
length must refuse before a second body copy; unknown oversized length must
attempt the second copy and fail permanently. Both invoke the handler exactly
twice, use fixed expected refusal diagnostics, and require no inner exception.
No bound, production/provider/config/schema or policy has been relaxed.

Local90 structured adapter controls PASS, including the retained250ms probes.
Full2210 Persistence PASS, zero failures/skips; changed whitespace verification
and diff checks PASS. These use in-process HTTP content, not a real provider.
Native111/all8 remains NOT_YET_QUALIFIED until a reviewed repair head has exact
green CI with FULL logs and six ORIGINAL images. Do not rerun the failed head
blindly, supersede meaningful active native work, merge incomplete278 or claim
real-model quality. The repaired501 fixture dc4b2e7 has scoped approval6102945756,
independently FULL read by root; actual501 ingress remains NOT_RUN_UNQUALIFIED.
