# ERP read credential qualification

Issue #264 / PR #265 qualifies the actual SQL connection used by a read. Source `AllowRead`, connectivity and an earlier successful probe are insufficient evidence of current credential permissions. This acceptance covers the conservative standalone SQL Server 2022 engine16/current-user-database profile in [the ADR](../architecture/ADR_ERP_READ_CREDENTIAL_PROFILE.md).

## Shipping contract

- Each already-open connection must return the explicit positive effective-rights proof. Qualification is not cached by source, secret, connection string or pool. Missing metadata, NULL/unsupported results and proof errors deny with bounded nonsecret guidance; cancellation is preserved.
- Catalog evidence, typed capabilities and schema discovery prove rights before reading and again after disposing the reader. A changed permission context discards materialized evidence. Read-only worker probes independently request qualification; denial cannot produce a success checkpoint or provider call.
- Fresh authoritative source flags choose qualification for read-only connection tests. Policy changes during secret resolution or probing deny. Supported write-enabled source tests retain connectivity semantics; legacy probe implementations cannot satisfy read-only qualification silently.
- Trusted capability definitions pass a pinned SQL160 ScriptDom parser before opening a connection: one current-database SELECT, without external object/function references, sequence advancement or external rowsets. Local aliases, CTEs and dotted literal values remain supported.
- Persistent DML/DDL, permission grants, impersonation, subordinate ownership/control, fixed roles other than public and unsupported or concealed module graphs deny. The ADR documents metadata visibility and the supplemental filtered credential-catalog check.
- Owned bootstrap adds sample reader metadata/direct SELECT only on initial login/user creation. Repeated bootstrap preserves revoked rights. Neither customer nor retained permissions are repaired automatically.

## Actual runtime controls

The mandatory complete-stack job invokes `scripts/smoke-erp-readonly.py` and the compiled `tests/ErpReadOnly.RuntimeProof` executable. Both refuse execution outside the exact owned disposable CI fixture. The compiled proof is absent from shipping production images/endpoints. Successful adversarial effects are rolled back, restored positive reads are verified, and private SQL/credentials/definitions/rows are not emitted.

Actual SQL Server controls passed:

| Control | Observed outcome |
| --- | --- |
| Ordinary reader, parameterized capability, catalog and schema | All four shipping paths pass; owned table/internal view/FK/index and structural hashes are materialized. Persistent UPDATE/INSERT/DELETE/TRUNCATE/ALTER/ownership change deny. |
| Direct UPDATE, column GRANT over table DENY, nested/elevated roles, owner module and user impersonation | Real unsafe effects succeed only inside rolled-back controls; qualification and API/BFF reads deny. Explicit restoration returns the safe profile. |
| Prior successful API probe, then changed rights before queued worker | Later independent worker session denies with authorization failure, without a success checkpoint. |
| Replacement elevated connection | Each shipping read independently denies. An earlier positive session cannot qualify the replacement. |
| Permission elevation during materialization | An actual UPDATE succeeds and rolls back on that same reader connection; final proof discards evidence. Restoring rights permits a subsequent read on the connection. |
| Revoked/missing metadata | All read paths deny. Repeat bootstrap preserves revocation; only explicit owned operator restoration returns the positive profile. |
| Hidden function through an accessible same-owner view | Low-level SELECT succeeds, while every shipping read denies the unqualified graph. |
| TYPE CONTROL and type ownership; synthetic database-scoped credential CONTROL | Real DROP succeeds and rolls back, while every shipping path denies. Explicit revoke/drop of owned fixtures restores safe reads. |
| SELECT WITH GRANT OPTION | Real permission grant succeeds and rolls back; no recipient grant remains, and every shipping read denies. |
| Concealed login impersonation and fixed metadata-role membership administration | Real impersonated UPDATE and role-member administration succeed only inside rolled-back controls; every shipping read denies. |
| External definition/sequence, accessible cross-database and encrypted views | Definition validation denies before query/sequence use; the owned foreign sequence remains unused. Low-level view SELECT succeeds but every shipping read denies the unqualified graph. |

The same run passed retained SQL/broker/worker/source-registration/member/authentication controls and full Chromium Code/S256 sign-in, company/role isolation, member suspend/reactivate and retry, stale-version private-data clearing, logout/session expiry and recovery. These existing workflows are regression evidence; they do not constitute a customer inventory adapter.

## Frozen evidence

Frozen implementation `e3d5254d8b7c7d72261ddf53841232587b1ee1c6` passed [Build 37799083533](https://github.com/thanhtuyen662002/minh-huy-ai-office/actions/runs/37799083533) and [Governance 37799083660](https://github.com/thanhtuyen662002/minh-huy-ai-office/actions/runs/37799083660): all eight Build jobs, actual SQL/Chromium job 113386172671 and required quality job 113389149345 succeeded. Sanitized browser artifact 11559389155 is 68,657 bytes.

Local full Persistence verification passed 650 tests. Focused connection/source/bootstrap/schema/definition verification passed 85 tests, including 13 negative and six positive parser cases. Earlier UI verification passed 30 lifecycle controls and TypeScript. Independent frozen review passed 313 focused ERP/source/schema controls, additional external/local parser variants and the nonshipping proof build; the reviewer inspected the exact actual SQL/Chromium logs and approved the implementation, security and runtime scope. Shipping source was unchanged by the final proof-only fixture restoration checkpoint.

The evidence-only final PR head still requires exact-head CI and independent delta review; acceptance delivery also requires exact main CI. Subsequent delivery hashes/run IDs are recorded in PR #265's current HANDOFF. Historical red checkpoints in the workstream document repaired schema collation and fixture-permission/cleanup errors, not a waived gate or current passing claim.

## Remaining product work

Medcom/Novo dedicated read-only credentials, database/version and legacy module dependency profiles, inventory/stock-movement adapters and reconciliation remain unverified. The current profile intentionally refuses unqualified legacy functions and external graphs. Santino remains deferred. Customer configuration/mappings remain private outside Git, and no customer business data or privileges were changed by these fixtures.

Production deployment, clean Windows installation/signing and all remaining requirements of #233 are separate acceptance work. PR #239 remains Draft until its external Windows/signing evidence is available. This report does not claim production completion.
