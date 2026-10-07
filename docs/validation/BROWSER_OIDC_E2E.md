# Browser OIDC acceptance evidence

Issue260/Draft261; full product issue233 remains open. Exact implementation `bac65ab46e9ab8070f18428a4c27c72ca8bcb292` passed [Build37648878161](https://github.com/thanhtuyen662002/minh-huy-ai-office/actions/runs/37648878161) and [Governance37648878122](https://github.com/thanhtuyen662002/minh-huy-ai-office/actions/runs/37648878122) on2026-10-07. Required stack job112886663552 and web job112886663851 passed. The web suite ran832 tests, including20 actual Redis controls; local skips are not acceptance.

## Real stack and Chromium result

The disposable GitHub fixture runs SQL Server, Keycloak26.8, Redis, RabbitMQ, Core, Worker and the built standalone FE. After retained SQL/identity/source/audit/race/broker/restart controls, pinned Playwright1.63.0 starts actual Chromium. Only this owned installation's Web is enabled for browser flow; local password UI is disabled. Neither customer ERP nor production infrastructure is accessed.

| Acceptance | Observed result |
| --- | --- |
| Provider-page Code/S256 login | Actual Keycloak form, verified callback303, scoped Core tenant/company/user context and no-store200. |
| Private cookies | Opaque SID/binding have bounded canonical shape, HttpOnly/Lax/path; inaccessible to browser JS, no raw token/legacy cookie. This fixture explicitly uses loopback HTTP; HTTPS Secure/`__Host-` behavior is covered in unit contracts and still requires approved deployment qualification. |
| Company/member/source access | Real scoped API/BFF data and visible source/member UI; foreign company401, local password route503 while current session stays200. |
| Metadata mutation | Supported narrow `/metadata` PUT200; policy fields retained, original four metadata fields restored in finally. Actual mutation403 after SQL admin revocation. |
| Callback refusal | Original encrypted transaction/binding/issued SID replay401, wrong state401, no cookie mutation and current issued authority retained; oversized task413. |
| Fresh role authority | Exact scoped SQL admin role removed, same SID context loses admin, members/registration/source mutation403; private member UI cleared. Full original role rows restored in finally; current authority recovers. |
| Logout/re-login | Browser logout clears session; explicitly presented old binding/SID401. Provider SSO re-login creates a different opaque SID. |
| Browser task | Actual UI task POST202 and durable scoped task GET200; foreign-company submission401. This proves task admission/persistence, not a customer business answer or a real AI-provider response. |
| Expiry/private-state clearing | Only the fixture's current issued Redis row receives a one-second session deadline. Natural Redis TIME denies it; populated user message and unsent draft disappear. Provider reauthentication recovers with empty private UI. |
| Production configuration | Both disabled and enabled synthetic production Compose contracts pass; HTTPS/same Core issuer/TLS Redis required, local HTTP/proof/password disabled. No production connectivity or deployment is inferred. |

The successful sanitized workspace PNG is artifact11495082669, `browser-workspace-bac65ab46e9ab8070f18428a4c27c72ca8bcb292` (7-day retention). It was visually inspected: source selection, member navigation, message/input area and logout render correctly. The current screenshot renders truthful `Đã đăng nhập` session copy and a browser account footer. It has no unsupported worker-health claim. Full exact-head Chromium verifies the rendered UI and empty recovered private state. Local Compose now defaults to browser PKCE with password UI disabled; retained compatibility smoke explicitly opts its owned fixture into the old local profile, validates both profiles and preserves all previous controls.

## Failure closure and limits

The first actual callback failed on473. Keycloak25+ puts access-token `sub` in its `basic` scope; the old client allowed only profile/email. Adding basic produced actual provider/callback/scoped authority PASS at293 while keeping required signed access/ID subject equality. Native select options have no visible layout box when closed; a local real Chrome control confirmed this and2f3's visible selector/exact attached owned option passed actual company/member/source proof. Later harness corrections use the supported metadata endpoint and accept an empty supported403 body while retaining mandatory denial status and strict positive payload checks. No security assertion was removed to make the gate pass.

Independent full frozen review of `bac65ab46e9ab8070f18428a4c27c72ca8bcb292` found no remaining implementation/security blocker for260:408 focused web tests,46 .NET tests,3 production-contract tests, TypeScript and syntax PASS. It verified the exact hosted runs, required quality job112888860747,832 web/20 real Redis controls, every retained stack/Chromium marker and current screenshot. The final documentation-only checkpoint still requires exact-head gates and delta review before matching-head merge/main CI. Actual approved production OIDC/HTTPS/TLS Redis, multi-company picker/member administration, Medcom/Novo inventory/stock-movement schema and least-privilege qualification, clean Windows lifecycle/signing and the remaining233 matrix are incomplete. Santino is deferred by the user. No customer/production writes, credential rotations or OS changes occurred.
