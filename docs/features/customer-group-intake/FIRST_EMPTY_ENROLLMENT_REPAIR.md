# First empty fixture enrollment repair

## Original finding and separate correction

Frozen53/head4d14a02 remains WITHHELD for P2 FEB53-1. Its five driver mocks
replaced the source factory and missed that the whole ingress route called
prepare_no_work_source twice. The real enroller refuses occupied slot5;
the second fixture could insert owned source rows but emits no events or new
successful proof. Independent receipt245f72d42fb38bda8b8089090ab704a4b572f70d6e92f1da39444a317f641ab2,
report518a13624eeeee779f2144d749e9d7152ef952b42cc913171d2919c71a9c72de,
envelopeb8cceff801d7ecc0aec5afc14962f414930f41ecd6f33fe5f520a6df5d8d3041
were fully read/rehash checked (60 artifacts). No SQL/network ran in that review.

Separate55 uses prepare_first_empty_source, fixed slot19 and generated private
OWNED_FIRST_EMPTY_GROUP_CONTENT_KEY through the existing owned override/key
loader. Slot19 is explicitly allowed and uses the existing fresh-binding guard.
Old slot5, its generated key, original role enrollment and strict four-role
frames remain unchanged. Slot18 is the original-admission fixture; it is not
reused. No duplicate-prefix guard is disabled and no key is overwritten.

This is only a disposable CI source/key fixture, not a new provider client,
AI configuration path, production registry grant or secret from this machine.

## Regression and evidence

Six new inert methods extract and execute the actual nested enroller and
both factories from ingress AST. External fixture/import/SQL/HTTP/compose/file
ports are inert. They reproduce double-old5 refusal with original mapping
preserved, prove old5 then new19 succeeds with original role/key unchanged,
check the actual caller uses the distinct factory, and reject repeat19,
reused binding, reserved15/unknown20/boolean slots before new secrets/writes.
Generated synthetic keys stay in private in-memory override and never become
compose arguments. The workflow runs this suite plus all retained controls.

These six methods and the five retained driver methods pass (11 inert total).
All C#/projects/production source bytes are equal frozen53. Its5365 full tests,
format and8 actual compiled owned-guard refusals are inherited unchanged;
no fresh .NET rerun is claimed for55. Python/workflow parsing passes.

Exact55 independent source review and fresh146 capture helper review remain
required. Actual first-emptySQL/native146 and model remain NOT_RUN_UNQUALIFIED/
NOT_RUN_UNEVALUATED. Current97ab native145 CI is separately owned; do not cancel
it by pushing a successor. Preserve all original findings/failed checkpoints
and full acceptance work. No main merge, issue completion or activation.
