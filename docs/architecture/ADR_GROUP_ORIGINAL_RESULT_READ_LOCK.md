# Accepted original-result read transactions

Status: proposed implementation, independent source review and actual SQL125 qualification pending. Parent c7e01d2 is CLOSED_RED at receipt6145607079; this document does not qualify the successor.

## Evidence

Exact c7 hosted Build38392179380/native115278401959 reported `attempt-result-resume`, `concurrent-read`, `source-lock-wait-timeout`. The actual negative APPLOCK return -1 is now identified; holder duration and unique underlying scheduling cause were not measured. Hosted4383.NET and the other five prerequisites/Governance passed. Original125 was102 once/23 missing/0 duplicate/5 failure markers. Permission-chain batching alone did not close concurrent-read acceptance.

Four accepted-result readers previously performed two journal units each, with every nested claim inspection acquiring the same Exclusive source lock. Smaller command counts still leave these eight units serialized behind a finite five-second wait. The native proof must keep four genuine simultaneous shipping reads and all current fences.

## Decision

Only `GroupModelAttemptResultStore.ReadOriginalAsync` uses a separate fixed read-only unit. Both units remain serializable, transaction-owned, scoped to the same original handle and plan, with key resolution outside SQL between them. Each executes fresh model/result effective permissions, every original source/coverage/brain/claim/current-directory/plan check before and after the fixed result operation. Structural entry validation, journal authentication, ciphertext bounds, original request/input/output/deadlines and key read counts remain.

A `GroupSourceReadLock` object binds exact DbContext, current DbTransaction, connection and GroupScope. It is explicitly propagated only through the claim/source/brain manifest checks used by this unit. Every invocation executes an actual Shared APPLOCK command; the object is not a permission verdict. Its transaction registry only prevents a nested default Exclusive writer from converting a read transaction. The registry does not cache rights, content, current claim or provider authority. Other call paths retain default Exclusive locking. Shared acquisition refuses an existing stronger lock; it cannot certify an Exclusive transaction as a Shared reader. The SQL command limit10seconds and APPLOCK wait5000ms remain.

The fixed read unit stages no effects and needs no write savepoint. Failure returns no result and disposes its transaction. Provider execution, journal append, result persistence, notes, outbox and worker registrations are outside this decision. No database schema, persisted format or API contract changes are required.

## Expiry across transaction boundaries

An Expired verdict carries the exact typed original observation. The Shared read unit first fully rolls back/disposes and restores its connection session. Only afterward does `RetireObservedExpiryAsync` open a new owned Exclusive metadata-only transaction and reuse `RetireExpiredLockedAsync` to authenticate fresh effective rights, directory, original receipt, matching epoch/state and allocation and commit the witness. Cancellation/revocation/replacement/invalid receipt fail without returning retained content or claiming successful retirement.

Retirement uses the original observed timestamp even if the clock has moved backward. Re-running only public RequireCurrentAsync after release could otherwise treat the old claim as current and lose known expiry. Full source/head/history equality is not a prerequisite for metadata-only witness retirement: later dependency changes cannot erase an authenticated observation. A witness is not note completion, result release or current-content authority. Success reads still require every full dependency check. Retirement never upgrades Shared in place, stages another effect, grants a replacement lease or returns an expired result.

## Verification

Model-free controls cover actual command construction/transaction binding, no cross-context/scope/transaction reuse, fresh permissions after acquisition, cancellation, negative acquisition and rejection of default writers before an upgrade command. Dedicated retirement controls cover clock rollback, replacement and revocation. These synthetic ADO/InMemory tests do not prove SQL compatibility, permissions or durable physical commits.

The owned original-result runtime proof additionally holds all four genuine readers at their first actual Shared lock until all four distinct serializable SQL transactions arrive, validates actual APPLOCK_MODE is Shared on the same connection/resource/transaction, then requires the original bytes/output/key2/0 and cleanup checks. An Exclusive reader, serialized fake, missing reader or write fails this proof. Its15second rendezvous only bounds test coordination; it changes no production lock or operation timeout. Original125 markers and all earlier SQL/expiry/replay assertions remain required. Actual SQL and current SOURCE review must pass before promotion/production qualification.

References: Microsoft [sp_getapplock](https://learn.microsoft.com/en-us/sql/relational-databases/system-stored-procedures/sp-getapplock-transact-sql?view=sql-server-ver17) describes transaction lifetime, bounded return codes and stronger-mode retention on mixed requests; [APPLOCK_MODE](https://learn.microsoft.com/en-us/sql/t-sql/functions/applock-mode-transact-sql?view=sql-server-ver17) identifies the current owner's actual mode.
