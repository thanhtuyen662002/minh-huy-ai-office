# Automatic batch dependency check — issue 278 / Draft 288

## Worker call and authority

`GroupAutomaticBatchDependencyStore.RequireCurrentAsync` accepts an existing sealed claim handle and trusted extraction worker binding. It owns a bounded SQL Server Serializable transaction and calls the reviewed all-contributor dependency reader. This gives the automatic worker a current dependency check for every committed chunk, preserving all original raw/head/receipt relationships and the source/brain/cipher/coverage fences.

Success describes dependencies under the transaction just committed. It is not a completion receipt, effect-ledger verdict, permission for a later write/send, or completed cursor. A future terminal writer must call the internal reader again inside its own effect transaction, with complete effect-ledger/original-claim/own-write-chain checks. No HTTP endpoint, new service grant, model client/configuration, note/outbox write or frontier is introduced.

Foreign tenant/company/service/credential bindings and cancellation refuse before database access. Dependencies must use the exact database instance, worker binding and clock. Non-SQL, MARS, ambient/caller transactions and dirty tracking refuse before opening a connection. The check has a two-minute linked deadline. A witnessed claim expiry is retired through the retained metadata-only claim procedure in the owned transaction, then denied; no successful coverage escapes. SQL/update/argument failures expose a fixed message without inner exception or private data. Existing transaction disposal restores isolation and closes its owned connection.

## Evidence limits

17 focused C# controls PASS with no failures/skips: non-SQL/MARS refusal without keys/connection/witness/effects; cancellation and four foreign-worker dimensions before a disposed database; source or brain database/worker/clock/missing-dependency refusal; dirty tracker and ambient transaction refusal with a closed SQL connection. These are cold entry controls. The successful SQL reconstruction path and expiry-witness commit have not run locally. Existing keys are never requested by this path; native proof must verify that property.

Parent reader8bc source review6103738028 is scoped approved after31 controls, ROOT FULL3526 characters read/persisted. Structural kernelac28 review6103648579 remains scoped approved; original ab58 WITHHELD6103599748 remains retained. Neither source review qualifies native runtime or completion. This later public store requires its own whole-checkpoint review before push.

Remote5fc113 remains owned: Build38096849478/native114344400101; Gov38096849367 and six other prerequisites passed, ROOT FULL.NET114344400163 shows2844 tests with zero failures/skips. This run predates the structural kernel, reader and this store. Close its frozen native113/full all8/quality/Gov/original images before superseding meaningful CI. Then add successful all-contributor reconstruction checks to the owned automatic fixtures while preserving their original graph digests, replay, expiry/rights and pending suffix assertions. Native nonempty brain, mutations/races, partial chunks/max bounds and complete ledger/frontier remain required.

Actual model evaluation is **NOT_RUN_UNEVALUATED**. Full278/279/233 and production/merge acceptance remain incomplete. No live provider, customer account, production SQL or ERP is activated by this preparation.
