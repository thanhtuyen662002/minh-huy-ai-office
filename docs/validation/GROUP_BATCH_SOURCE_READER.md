# Scoped batch source reader checkpoint

Issue278 / Draft288. This checkpoint supplies private source context to a trusted Extract worker. It does not deliver automatic notes or qualify a model.

## Shipping boundary

`GroupBatchSourceReader` takes a sealed live claim and a host-selected slice of 1–100 distinct allocated message IDs. It reuses `IGroupSourceKeyProvider` and the existing source AEAD protector. It never impersonates a portal user or accesses owner history, DMs, another group or a model.

The reader checks current service, source, account, credential/grant versions, deletion generation, immutable allocation/claim receipts and effective SQL permissions under the existing source transaction lock. It selects revision heads as of the immutable allocated cutoff with Recall > Edit > New/Media precedence. A winning revision may predate the batch interval. A later head change, recall or obsolete generation becomes a metadata-only disposition; it does not resolve a key or decrypt content.

Original UTF16 identities, sender/reply metadata, full ingress receipt, purpose-bound ciphertext and original envelope hash must agree. SQL checks material lengths before returning variable-sized columns. Source plaintext retains its original UTF16 identity and is bounded to8000 units. Selected IDs and ciphertext are copied before external awaits. Private context construction is internal; default string formatting contains no private content.

Keys are resolved outside SQL transactions through the existing scoped provider. Every key await is followed by a new source-locked live claim/authority/dependency check before decryption. All contributing heads, receipts, ciphertext and coverage-gap state are checked again before context release. The whole read/fence has a finite two-minute cancellation deadline; a provider ignoring cancellation cannot release late context, and late key material is disposed. Provider failures produce a fixed error without their private exception text.

The reader owns fixed metadata/ciphertext reads only. Its initial/final expired verdict is retired in a metadata-only SQL unit before denial. There is no generic effect callback or staged business write to accidentally commit. A later note/effect consumer still needs its own source-lock-before-savepoint rollback/retirement wrapper and native staged-effect/MARS denial proof. `RequireCurrentAsync(context)` is an instant dependency fence, not future effect authorization.

## Verification and limits

32 source-reader controls cover exact scope/UTF16, original AEAD envelope, cutoff precedence, unrelated traffic, recall/edit during key await, current authority changes before decrypt, durable expiry across clock rollback, malformed stored material, finite cancellation with late key disposal, bounded slices and private formatting. InMemory controls do not establish native SQL serialization or production access.

Five owned executable modes are implemented: source-read, source-foreign, source-deny, source-key-revoke and source-expiry. They use actual Core/spool committed originals and the production configured source key provider. The held-key mode requires an observed owned container/label checkpoint, external operator SQL Extract revocation while no reader transaction holds the source, exact denial, grant restoration and a positive reread. Controlled key-await expiry may change only the current claim's expiry witness; it is not a real five-minute wall-clock expiry. Full original raw/inbox/allocation/claim graphs, portal counts and pooled isolation are checked. Remote2199 executed source-read, source-foreign and source-deny successfully, then failed the held-key owned-container observation before revocation/expiry/browser completion. The full modes remain **UNACCEPTED** until renewed exact-head native CI succeeds.

The parent3a78 native job114219254243 failed before these new modes existed, at the claim crash assertion after~154 seconds. Its six prerequisite jobs and Governance succeeded, but native/required-quality gates failed and no browser artifact was produced. The frozen .NET job passed2212 tests. The fixture ran dotnet as namespace PID1 and attempted self SIGKILL before a150-second infinite wait. Docker `--init` now makes it a child, and an explicit process-ID guard rejects PID1 before stdin/configuration/SQL. The strict checkpoint/exit137 and original graph assertions remain. Native claim acceptance is still unproven; do not call the repair successful from local controls alone.

No source reader DI activation, model execution, notes, glossary, secret quarantine, token budget qualification, dispositions/completion ledger or NotesCommitted outbox is delivered by this checkpoint.278/279/full233 remain active. Primary120 and independent40 proposed evaluation inputs remain UNEVALUATED.

## Latest2199 native evidence and scoped fixture repair

Build38059643216/native114235105757 and quality114239125705 are terminal RED; six prerequisites and Governance38059643200 succeeded. Root verified exact .NET2319 and native73/93 required markers,124 PASS/four failure markers. The strict failure was smoke-group-reference.py634 during source-key-revoke container observation. The identical image/container name plus untyped inspect permits image fallback before creation; both observation and ownership-checked kill now require container type. Full64hex identity, exact ownership label, current reinspection before release, original exact child reply and all graph/permission/cleanup assertions remain. The failed inspect bytes are unavailable, so unique runtime causality is unproven. Local58 inert Python controls pass; new native execution and six original browser images remain required. No source/note/DI/model/full278 completion follows from this repair.
