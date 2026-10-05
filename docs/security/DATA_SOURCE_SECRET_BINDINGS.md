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
conservatively rejects executable/selectable user procedures/functions, writable
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

Hosted SQL/stack evidence for this local candidate: **NOT_RUN**. Parent owns
publication and exact-head hosted execution; syntax checking the smoke script and
generating an offline migration script do not constitute SQL acceptance.
