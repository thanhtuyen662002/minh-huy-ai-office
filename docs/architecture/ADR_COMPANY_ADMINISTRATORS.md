# Existing member administrator changes

Issue #268 / Draft PR #269, under full product #233. This checkpoint implements the service/API/migration foundation. The supported browser flow, actual role-mutation SQL/Chromium proof, independent security review and exact PR/main delivery are still required.

## Authority and concurrency

An active company administrator changes only the exact existing server role `admin` on an existing active member with an active global user. The request carries an operation ID, the existing membership version and an administrator boolean. It cannot name an arbitrary role, identity, tenant or grant. Self role changes are refused; removing an administrator must leave another active exact administrator. Case/padding aliases do not count as administrators and ambiguous target roles are refused.

Role and access mutations share the company `aioffice:membership` transaction lock, serializable isolation and the same optimistic membership version. A role change increments the version; audited no-ops retain it. A stale request cannot overwrite a concurrent access/role decision. Other roles, user identity, membership activation and durable tasks remain intact.

The signed request context is independently reauthorized before the transaction, after acquiring the lock and before save; retries are also freshly authorized. Exact actor/request replays return the original receipt even after later changes. Reusing an operation ID for a different request/actor is refused.

## Audit and compatibility

The additive `AddCompanyAdministratorAudit` migration stores immutable actor/target, operation/hash, before/after administrator state, complete sorted role snapshots and membership versions. Scoped keys, unique operation IDs and restricted foreign keys preserve attribution. Runtime gets SELECT/INSERT on the audit and is denied destructive table/column operations; operator ownership stays separate. No down migration erases history; recovery requires a reviewed forward repair.

Before reading receipts and before saving, the pinned connection repeats global effective-rights and audit append-only proofs, including indirect execution/impersonation/trigger/column escalation. Proof results are never cached. SQL reads role keys as bounded stored UTF-16 bytes, decoded strictly, so driver repair cannot silently alter the old roles recorded by the audit. The raw SQL is constant; authority selectors stay parameterized. Relational query translation is tested before any connection opens; actual SQL behavior still requires its owned runtime proof.

The strict authenticated `/api/company/members/{userId}/administrator` endpoint shares the existing company boundary and no-store middleware. It bounds JSON bytes, rejects missing/extra/duplicate/malformed fields and returns bounded status/conflict messages. Existing member listing and access request/response shapes are unchanged. No API-only checkpoint establishes browser or production acceptance.

## Remaining delivery

Build the issued-session same-origin BFF and shipping member controls, with bounded responses and shared-session fences, stable unknown-result retry and immediate fresh role reconciliation. Prove mutation/audit atomicity, SQL permission and concurrency adversaries plus actual browser grant/remove/lost-reply behavior. Obtain independent frozen security/runtime review and close exact PR/main gates before merge. Full invitations/custom-role policy/customer ERP/production and clean Windows/signing remain separate #233 acceptance.
