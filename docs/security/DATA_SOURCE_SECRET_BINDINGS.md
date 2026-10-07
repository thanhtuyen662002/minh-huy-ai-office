# Scoped data-source secret grants

A source row is configuration, not permission to use a secret. The authoritative
`aioffice.DataSourceSecretBindings` table requires one enabled grant for the exact
tenant, company and canonical reference. Grant identity and positive version are
rechecked before each connection use. Its unique binary-collated index preserves
resource case (`FOO` and `foo` differ); `SecretReference.Parse` normalizes provider
case only. Labels are bounded descriptive text, never secret material.

## Provisioning and runtime boundary

Only an elevated bootstrap/operator identity writes the grant table. There is no
runtime grant-management endpoint. Company admin does not imply grant-management
authority. Existing source rows, inherited environment variables and the existence
of a secret provider do not create grants. The platform database reference is
forbidden as a customer source even if an erroneous operator grant exists.

Infrastructure exclusion compares canonical references, including encoded/path
aliases. For `env`, Windows keys compare without case and Linux keys compare with
case, matching the environment provider. This applies to the reserved platform key
and a configured custom platform reference. Other providers retain ordinal canonical
comparison. Customer source-to-grant matching always remains ordinal/BIN2 on every OS;
a different-case customer grant never authorizes the source, including on Windows.

The migration creates `aioffice_binding_runtime`, grants SELECT and database VIEW
DEFINITION, and denies table/column UPDATE, INSERT, DELETE, ALTER and ownership.
Effective CONTROL must be absent; denying CONTROL would also deny SELECT under
[SQL Server's permission hierarchy](https://learn.microsoft.com/en-us/sql/relational-databases/security/permissions-database-engine).
Explicit column DENY handles SQL Server's column-GRANT exception.
The table has a dedicated no-login owner distinct from ordinary dbo-owned tables
and modules, breaking ordinary cross-object ownership chains into the grant store.
ALTER denial blocks TRUNCATE. VIEW DEFINITION makes the module/trigger inventory
visible; it grants no execution or grant-write authority. Operators must review
database definitions for embedded credentials before enabling that runtime role.

API and worker startup verify their actual SQL connection identity, and every
source use repeats the proof. Elevated identities, unknown permission results,
missing metadata visibility, impersonation/role escalation, column privileges and
unsafe module paths fail closed. This runtime executes ad-hoc EF commands. It
checks effective `IMPERSONATE` on each database user. SQL Server has no DATABASE
permission named `IMPERSONATE ANY USER`; checking that invalid name returns NULL
and incorrectly rejects every safe runtime identity. The disposable SQL gate
verifies the engine catalog and grants/revokes impersonation of the dedicated
binding owner to prove that actual escalation remains denied at runtime and API.
It conservatively rejects executable/selectable user procedures/functions, writable
views/synonyms and every enabled user trigger, rather than trying
to prove arbitrary module bodies safe. Ownership chaining, `EXECUTE AS`, signing
and encrypted/dynamic SQL can bypass direct table DENY; direct DENY alone is not a
security claim. Review such modules separately before any future support is added.

Application grant checks reload current user, company and membership from the
directory. Task workers reload `Tasks.CreatedByUserId`, compare permission-request
identity with that durable owner, and repeat current authority at execution.
Create/full PUT with a supplied reference and any enabling edit require a grant.
Disabling or metadata editing while disabled can retain a legacy missing/revoked
reference, so administrators can recover configuration safely. Full PUT with a
null reference still checks the stored reference when enabling.

Connection tests, task admission/replays, both worker permission providers, the
nested AI probe and evidence reader all use the guard. Resolution is wrapped:
check authority/source/grant, await the secret provider, reload and compare grant
identity/version/reference and source policy, then invoke the probe/evidence
callback. A cached permission, tracked EF entity or previously accepted queue item
does not authorize later use. No rejected path sends a connection value to a probe,
evidence reader or model. Cancellation remains cancellation; authorization failures
are generic and contain no provider/SQL diagnostic.

Revocation fences the next checked use; it cannot undo a SQL request already in
flight. Operators requiring immediate containment must stop/drain workers and revoke
the underlying customer connection too. Atomic distributed cancellation of an
already started provider/SQL request is outside this change.

Worker authorization denial is persisted as Failed/DeadLettered before broker
settlement. Repeated delivery, including an older transient attempt after a denied
retry, never invokes the executor again. Audit or terminal persistence failure and
cancellation propagate without a false settlement or relabelled authorization error.
Failed saves clear uncommitted tracked projections; infrastructure recovery keeps
the existing bounded lease before a fresh denial attempt can complete.

## Fresh installation and secured upgrade

Fresh local bootstrap creates only the explicit sample ERP grant together with the
owner/company/source and installation marker in a single EF SaveChanges transaction.
Residual users, companies, sources or grants without the marker cause refusal.
Repeat bootstrap keeps missing/revoked grants, disabled accounts, edited data and
original credentials. It never repairs security revocations implicitly.

For an existing installation:

1. Stop admission and drain/stop every old API/worker, including queued/retry workers.
   An old binary has no grant boundary and must not remain alongside the upgrade.
2. Back up using the established installation procedure. Apply migration
   `20261005084000_AddScopedDataSourceSecretBindings` as the installation operator.
   It adds an empty grant table; it never backfills authority from legacy source rows.
3. Add the dedicated runtime database user to `aioffice_binding_runtime`. Keep it
   outside elevated SQL roles. Review effective column, role, impersonation and
   executable module/trigger privileges with the runtime identity. The startup proof
   must succeed before serving requests.
4. Review each tenant/company/customer reference and intended scope independently.
   Canonicalize with `SecretReference.Parse`, verify the selected company and use
   `infra/provision-data-source-bindings.sql` with explicit parameters. Create disabled,
   then explicitly enable using the reviewed grant identity and expected version.
   Review custom platform/provider references too; never grant infrastructure secrets.
5. Deploy secured binaries, run exact-head disposable SQL gates and selected-machine
   installation lifecycle acceptance, then resume admission. Previously queued tasks
   with missing/revoked authority must fail before provider use.

To revoke, use the same operator script with `revoke` and the expected version.
Restoring requires an explicit reviewed `enable`, incrementing the version. Deletion
and recreation uses a new grant identity. The template neither loops over source rows
nor silently upserts/re-enables a grant. Treat a previously deleted grant as a fresh
operator decision; bootstrap replay is never that decision.

Do not roll back to pre-guard binaries as a security recovery. Keep secured binaries
and revoke/disable sources; use a reviewed forward migration if repair is required.
The grant migration deliberately refuses Down. Existing installer #239 must rebuild
the secured bundle and complete clean install, reboot, repair and data retention
acceptance separately; CI success is not production acceptance.

## Evidence limits

Synthetic fixtures cover exact scope/case, inactive authority, missing/revoked or
ambiguous grants, retry denial, provider-await revocation/version/recreation races,
probe-to-evidence policy changes, runtime proof failure and bootstrap replay. They
do not prove SQL permissions or constraints. The disposable hosted stack gate checks
actual runtime DML/DDL denial and adversarial ownership-chain paths, explicit fixture
grants and queued-task revocation while preserving PR247's owned blocked-UPDATE
positive and full-PUT negative controls. These gates must execute on the frozen
candidate and subsequently merged main. No local synthetic result is live ERP or
production installation acceptance.

Configured API startup regressions exercise SQL-provider permission verification
using an entirely synthetic `DbConnection`, accepting proof 1 and refusing proof 0
before serving requests. API and both worker DI execution branches cover protected
env aliases and positive/negative customer grants. The Windows paths were exercised
locally; OS-aware Linux case controls still require execution on Linux CI.

PR #249 subsequently passed actual SQL/stack checks and merged at
`db2ef9a141b69b10618b7c74548828753dfd2dd0`; merged-main Build 37506783690 and
Governance 37506783677 passed. New changes still require their own exact-head
and merged-main evidence. Syntax checks do not establish SQL acceptance.

## Approved choices for source onboarding

`GET /api/data-sources/registration-options?offset=0&limit=50` returns a page
with `items`, `offset`, `limit` and `hasMore`. Each item contains only `bindingId`,
`label`, positive integer `version` and additive decimal string `versionToken`. Canonical references, credentials and
infrastructure bindings are excluded. Labels must be bounded human display text
without a secret-reference marker. Listing does not resolve credentials, create
grants or create sources.

Both before the query and before releasing metadata, the service resolves active
company administration from the current server directory. The existing SQL
binding-store permission proof runs before binding rows are read. Selection is
restricted to the exact tenant/company and enabled positive-version grants;
noncanonical, infrastructure or invalid-label records are omitted.

The snapshot reads at most 1001 enabled candidate rows in stable ID order. More
than 1000 candidates makes the feature unavailable instead of silently truncating
the approved set. Offset is 0 through 1000; limit is 1 through 100. Safe choices
are filtered before paging, so `hasMore` refers to actual safe choices. Clients
must use `versionToken` for registration, preserving the full positive Int64
without JavaScript rounding. Numeric `version` remains for compatibility.
Choices are a snapshot; registration re-authorizes binding ID/version.

Invalid page bounds return 400. Unavailable or unauthorized authority/store
returns 403; absent authentication configuration returns 503. Responses carry
`no-store`. No grant authority is inferred from possessing a returned ID.

Issue #250 / PR #251 verified the choices prerequisite. Issue #252 / PR #253
owns audited read-only registration and its admin frontend. Real ERP privileges,
schema/capability mapping and full product completion remain under #233.

## Audited read-only registration

`POST /api/data-sources/read-only-registration` requires current server-resolved
company administration. Its strict JSON body contains only `bindingId`,
`bindingVersion` (canonical positive decimal Int64 string), `operationId`,
`logicalName`, `environment`, `purpose` and `maxConcurrency`. Tenant, company,
actor, SQL kind, enable/read/write policy and secret reference are server-owned.
Unknown fields and numeric/rounded versions are rejected. The local BFF requires
same-origin requests, a canonical company selector and an at-most-8192-byte body.

The SQL transaction uses Serializable isolation: fresh directory, grant, operation
and logical-name reads keep their locks through source/audit commit. A disabled,
foreign, noncanonical, infrastructure or changed-version binding is unavailable.
Source and audit insert in one SaveChanges/transaction. The service revalidates
administration and binding before saving. Failed writes leave no pending tracked
source/audit entities; SQL transaction disposal rolls back both inserts.

Registration pins its connection until transaction disposal and restores SQL
session isolation before returning it to the pool. Newly rented sessions return
to ReadCommitted; an explicitly caller-owned open session keeps its original
isolation. Cancellation cannot skip bounded restoration. A failed restoration
invalidates the affected SqlClient pool and closes the unsafe session, preventing
another request from inheriting Serializable. The real SQL smoke checks sleeping
runtime session isolation after denied registration and preserves the prior
blocked-UPDATE metadata race and full-PUT negative control.

`DataSourceRegistrationAudits` stores scoped actor/source/binding identities,
binding version, operation ID, UTC occurrence and a SHA256 fingerprint of normalized
nonsecret registration metadata. No credential or canonical reference is retained.
The unique tenant/company/operation index supports lost-response retries. Same
actor and fingerprint return the current source descriptor; mismatches or an
existing logical name return a generic 409. Retries still require current
administration and the original enabled binding/version.

The additive migration gives the audit table the separate operator owner, grants
runtime SELECT/INSERT, and denies UPDATE at both table and every column plus
DELETE/ALTER/TAKE OWNERSHIP. The global binding-store proof excludes privileged
roles, impersonation, schema authority, executable ownership chains and triggers.
Registration additionally proves effective audit SELECT/INSERT and absence of
UPDATE/DELETE/ALTER/CONTROL/ownership and column UPDATE. No destructive Down
migration is permitted; corrections use a reviewed forward migration.

The admin UI loads approved choices without references, submits only the strict
body and confirms a fresh source list before showing success. Every awaited
response is fenced by the current session/company generation and fresh directory
validation. An uncertain request freezes its body and operation ID for explicit
retry. Company/user/role changes clear private choices and pending UI state.

Local synthetic tests prove service/API/UX behavior, not live SQL privileges.
The disposable SQL/OIDC/Compose smoke must prove effective audit denials, atomic
rollback after a failing audit INSERT, BFF idempotency and retained history across
restart on the exact candidate. No result qualifies actual customer ERP business
data or clean Windows installation.
