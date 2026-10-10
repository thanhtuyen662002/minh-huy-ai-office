# Group batch claim checkpoint — issue278 / Draft288

Status: implemented and locally verified; independent frozen checkpoint review and new native SQL execution remain required. This is metadata acquisition/fencing, not extraction completion, protected context, automatic notes or full278 acceptance.

## Behavior and durable identity

GroupBatchClaimStore uses the trusted process worker binding, current Extract directory, effective permission proof and the identical ingress/allocation source application lock in its owned serializable SQL transaction. It validates the existing immutable allocation/raw-ledger/original-ingress graph before issuing or reconciling a claim. Scope, batch/owner/operation IDs and a10-second–10-minute requested lifetime are mechanical inputs.

GroupBatchClaimReceipts is append-only with an original acquisition nonce. Replaying that nonce with the same batch/owner/lifetime returns the original epoch/timestamps; it never renews a lease. Conflicting intent refuses. GroupBatchClaimStates holds only the reviewed current owner/epoch/nonce/issued/expiry fields. A new nonce cannot steal an active lease. Actual expiry permits checked monotone epoch advancement under the source lock. State must reconcile with its immutable current receipt and full epoch receipt count.

A sealed handle has no public constructor. Its captured authority includes exact tenant/company/source/account identities, enabled states, service credential reference/epoch, grant/source/account versions and deletion generation. SQL stores only the purpose-versioned canonical authority SHA256, never credential references or source plaintext. Expired/replaced/authority-changed original receipts remain honest metadata with no current handle. Current revocation refuses even metadata reconciliation. Unrelated ingress cursor advancement does not invalidate a still-current claim.

Public RequireCurrentAsync proves current metadata only at that instant. Future protected readers/result stores must call the internal live SQL fence inside their own transaction, and recheck before final effects. No generic callback or arbitrary model/broker DTO can confer authority. Expiry is not completion; scanner ordering, immutable terminal outcomes/frontier and atomic notes/outbox are still required before pipeline activation.

## Versioned storage and permission policy

AddGroupBatchClaims is an additive generated EF migration with full scope keys, restrictive allocation/company/service foreign keys, unique acquisition nonce and batch epoch, UTC/lifetime/positive-authority constraints. Both tables are operator-owned. Runtime SELECT/INSERT on immutable receipts; every receipt UPDATE column is denied. State UPDATE allows only Epoch/OwnerId/OperationId/IssuedAtUtc/ExpiresAtUtc; scope and batch identity stay immutable. DELETE/ALTER/ownership are denied, and the effective rights verifier also rejects unreviewed additional columns. Destructive Down refuses; old runtime remains compatible with the expansion.

## Retained and new local controls

-120 focused .NET controls PASS, zero skipped:83 retained allocation/ingress/model/native-guard controls plus36 claim controls and one exact refusal-type control.
- Claim controls include100 original nonce replays through fresh contexts, active lease refusal, real clock expiry/epoch replacement, expired and replaced handle refusal, conflicting intent, seven exact authority changes, seven current revocations, ten corrupt graph cases, unrelated ingress, final authority mutation from a separate context, final expiry/backward/non-UTC clock, scope/cancellation/lifetime/ID bounds and restrictive native model metadata. InMemory is not a SQL concurrency/rollback proof.
-49 owned-stack Python controls PASS; six actual native claim executable entries refuse before stdin/configuration/resources with ownership flags absent.
- Shipping persistence/native proof build: zero warnings/errors. Two Python AST checks, changed-file formatting, actual EF generated idempotent script/no pending model changes and strict durable YAML/diff checks pass at checkpoint preparation.

## Native modes implemented; UNEXECUTED at this checkpoint

Existing owned CI proof executable has six guarded claim modes; it uses only the ephemeral SQL restricted aioffice_runtime identity. The coordinator runs these after all retained277 broker/application/restart/no-cursor assertions and new allocation assertions. Fixed failure types, original byte digests, complete metadata graphs, portal counts and SQL isolation restoration remain mandatory.

- Controlled CHECK failure must produce the actual sanitized claim commit exception and leave both claim tables and original allocation/source graph unchanged.
- Current Extract revocation and effective immutable receipt/state identity column escalation must refuse; owned setup is restored even on partial failure.
- Four actual competing SQL contexts share one original nonce. The owned proof process abruptly kills itself after commit/reconciliation and flushed checkpoint, before external receipt delivery. A fresh executable performs100 concurrent original nonce replays without renewal or duplicate metadata.
- Actual TimeProvider.System expiry, active new-nonce contention, epoch2/3 replacements and stale handle refusal are exercised; immutable original graph and no private/model/note/portal effects are checked.

These native modes require independent source review and exact-head hosted execution before any runtime claim. Parent a50 allocation Build38050984824/actual114209890966 and Governance38050984773 are active; parent green or earlier transport results cannot prove new claim modes. Real model evaluation proposed120/40 remains UNEVALUATED; provider configuration location is pending.

## Next executable action

Own parent a50 exact CI through terminal. Independently review this immutable claim checkpoint, repair any concrete blockers on this same PR, then push only approved source after useful parent CI is terminal and own renewed exact native CI. Continue scoped source/current-note/glossary brain, chunk/dependency/tokenizer profile, protected notes/evidence/dispositions/terminal receipts/atomic NotesCommitted outbox, worker DI/no-click APIs/UI and actual frozen model evaluation. Keep278,279 and full233 active.
