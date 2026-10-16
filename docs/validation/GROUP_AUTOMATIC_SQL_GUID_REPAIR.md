# Automatic proof SQL GUID projection repair

Exact remote `570dedb8b5ba30713124dc42a8418d588e6a0dc5` failed in
Build38084668247, native job114308583350, on 2026-10-10 at21:02UTC.
The complete private log identifies the first automatic proof's source-binding
GUID projection and strict `canonical()` assertion. This is an executed proof
failure, not zero-step runner noise. Governance and six prerequisites passed;
the native job and required quality gate failed. No browser artifact or native105
acceptance is claimed.

The three SQL projections now explicitly select lowercase, fixed36-character
representations of internal `uniqueidentifier` columns: source binding ID,
connector account ID and the new source's connector account reference. The
nonzero, exact canonical input check is unchanged. Opaque external identifiers,
fixtures, protected source, shipping C#, schema, permissions and workflows are
unchanged. Full33 retained snapshots and all effect/witness/cleanup assertions
remain required.

119 Python controls pass. The two added regression methods exercise all three
automatic profiles with the exact SQL projections and reject an unexpectedly
noncanonical SQL result before fixture enrollment or Docker. They also preserve
refusal of uppercase, compact, braced and zero GUID inputs. These are intercepted
resource controls; the repair still needs an exact-head native execution.

The already reviewed local NoWork06d and host-onlybef ancestors can accompany this
same-PR repair after full frozen repair review. Both source receipts were fetched
and read in full by root (6102067087 and6102145588). The new native run must require
all107 frozen aggregates, all8 quality gates, Governance, full .NET results and
six original browser images. The failed570 run is preserved as failed evidence;
it is not described as closed green or as model/product acceptance. Real-model
evaluation remains NOT_RUN_UNEVALUATED.
