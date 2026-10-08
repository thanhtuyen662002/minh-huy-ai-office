# Existing company membership access

Issue #262 / PR #263 extends the admin member directory with suspension and reactivation of an existing membership. Invitations and role administration remain separate product requirements.

## Contract

- `GET /api/company/members?includeAccessVersion=true` opts into a canonical positive SQL bigint version string alongside the status from the same directory query. The default response retains its original five member fields.
- `POST /api/company/members/{userId}/access` accepts only `operationId`, `expectedVersion` and `isActive`, with a 2 KiB body limit. Authority comes from the authenticated directory and selected company. Client scope or roles are rejected.
- Each operation serializes on its company, rereads admin authority, verifies global effective SQL rights and append-only audit rights, then commits membership state and audit together. A changed status increments the optimistic version. Roles, global user state and durable tasks are preserved.
- Self suspension, inactive-user reactivation, unavailable targets, stale versions, conflicting operation reuse and removal of the last effective administrator fail closed. SQL role aliases do not count as exact `admin` authority.
- Exact retry returns the recorded operation result, including after later state changes. It never reapplies the old operation or duplicates history. The UI retains the operation ID and version after an ambiguous failure and reloads the directory after success/replay to show current state.
- The BFF requires the configured same origin and an issued session, relays the company selector, bounds the streamed body and strips upstream cookies. The UI discards stale generations and clears private rows after denial or failed authority confirmation.

The migration preserves audit history and supports reviewed forward repair. Its destructive `Down` operation is intentionally unavailable.

## Required runtime proof

The complete local stack job runs `scripts/smoke-membership-access.py` through `scripts/smoke-local-stack.py`. The new fixture refuses execution outside the exact owned disposable GitHub CI directory before introducing its users or permission controls. It verifies:

- Actual OIDC identity and SQL membership suspension/reactivation, same issued identity denial/restoration, version conflicts and historical replay without reapplication.
- Concurrent same-operation replay and different-operation stale rejection.
- Actual runtime direct audit mutation denial; column grant, elevated executable, trigger and impersonation negative controls. Successful adversarial writes are rolled back, and the API must deny the unsafe configuration even when direct table rights pass.
- A real audit insertion failure rolls back the membership update.
- Two real admin requests wait behind an operator-owned SQL application lock barrier before release. Opposite-target suspension leaves one active administrator, one audit and a denied freshly revoked actor.
- Global identities, roles and existing durable tasks retain their original fingerprints.

The required Chromium job also verifies native member buttons, self-action disablement, suspension/reactivation, an actually committed operation whose response is aborted, exact stable retry without a second audit, stale-version reload and same-session role/logout/company denial. The browser fixture retains its owned member and immutable audit until the disposable volume is removed.

## Evidence status

The security checkpoint `e7fe3217c6cdfd910a8affa688d41780bd2a9ba7` passed Build 37742062502 / Governance 37742062504 and independent frozen backend review (53 focused controls). The UI checkpoint `1250c24b2cbb08c747bad61f213bf712668cf574` passed Build 37742649576 / Governance 37742649549; local 102 focused member controls and 882 web tests passed, with 20 actual Redis controls in hosted CI.

Those runs predate the new member SQL and Chromium proof described above. The current integration checkpoint must pass those required jobs and receive full frozen review before #262 is complete or PR #263 is eligible to merge. No customer database or production deployment is touched by these fixtures. Full product #233 remains active.
