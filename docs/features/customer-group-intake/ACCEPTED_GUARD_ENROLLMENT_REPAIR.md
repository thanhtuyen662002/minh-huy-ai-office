# Accepted-result guard mutation: isolated source enrollment

## Observed failure

PR #288 head `97ab2137a1aa4dc2f0bbd5cb9f4e83b3ed047468`, Build38787704388 / Governance38787704385 attempt1, ended failed. Native job116603884173 reached `smoke-group-accepted-guard-counterfactual.py:49`. It called the original admission factory a second time, whose slot18 was already occupied. The actual traceback names `enroll_owned_source` and its occupied-prefix assertion. This is an observed fixture collision. It is not a model failure or a timeout. Closure6200811199 preserves the complete native, .NET and quality logs. All eight jobs are terminal; six prerequisites and Governance passed, native and quality failed, no browser artifacts were produced.

## Repair

The original admission driver now requires a separate `prepare_counterfactual_source` callback and rejects a noncallable callback or the same callable before any SQL or child execution. Ingress supplies `prepare_admission_source` for the original source at slot18 and `prepare_accepted_guard_source` for the mutant at slot20. The latter uses the existing owned fixture builder, Core configuration override, source-key loader and cleanup. Its independently generated key is referenced by `OWNED_ACCEPTED_GUARD_GROUP_CONTENT_KEY`; no provider configuration/client is added. The first-empty fixture retains its separately reviewed slot19. The original role-enrollment slots4/5/6/8 are unchanged.

The occupied-prefix assertion and fresh-binding refusal remain. A second enrollment in slot20, an existing binding, reserved slot15, unknown21 and Boolean slots all refuse before creating another secret, override write or restart. Runtime SQL ownership/tenant fences, accepted-result guard, companions, FKs, permissions, exact mutant negative oracle, unchanged graphs and both cleanup gates are unchanged. The production guard deletion still occurs only within the isolated mutant build.

## Verification and limits

Thirty fresh offline Python test methods pass: six new actual nested factory/enroller/dispatch controls, ten original admission controls, six first-empty enrollment controls, three mutant controls and five first-empty driver controls. The new suite reproduces the old slot18 collision without overriding its refusal, executes the actual parent dispatch statement with the separately enrolled callback, checks18/20/19/5 coexistence with preserved keys/roles, and rejects repeated slots and rebound identities. SQL, HTTP and Docker ports in these tests are inert. Existing original-driver controls now verify callback identity instead of ignoring the mutant callback.

All C#, project and production source bodies are unchanged from source55/53; local5365=[6,584,25,4750], formatting and eight compiled guard refusals are inherited, not rerun for62. Hosted97ab5357 belongs to the failed older head and is not a5365 successor result. Actual new SQL146 and model evaluation have not run. Original53 WITHHELD FEB53-1 and the failed e8/97ab closures remain immutable. Frozen62 source and revised source-bound capture/parser need independent review before the ordinary same-PR successor push.

The native integration job budget increases from60 to90 minutes because the observed job already consumed57m47s before the mutant fixture refusal, with required mutant completion, retained-expiry/restored/first-empty phases and Chromium still ahead. All assertions, subprocess bounds, required gates, cleanup and original-image requirements remain. A larger bounded job budget cannot qualify missing markers or a failed child.

No partial main merge, customer-group outbound, ERP business write or model qualification is authorized by this repair. The real model remains `NOT_RUN_UNEVALUATED`; only the existing AI Office Gateway/options/client may be used when protected configuration is available.
