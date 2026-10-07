# Browser OIDC acceptance evidence

Issue260/Draft261; full product issue233 remains open. Exact implementation `4e1880652cbe398f66f42cb760fd1b60f3b3a387` passed [Build37646560686](https://github.com/thanhtuyen662002/minh-huy-ai-office/actions/runs/37646560686) and [Governance37646560635](https://github.com/thanhtuyen662002/minh-huy-ai-office/actions/runs/37646560635) on2026-10-07. Required stack job112878733833 and web job112878734106 passed. The web suite ran832 tests, including20 actual Redis controls; local skips are not acceptance.

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

The successful sanitized workspace PNG is artifact11494403657, `browser-workspace-4e1880652cbe398f66f42cb760fd1b60f3b3a387` (7-day retention). It was visually inspected: source selection, member navigation, message/input area and logout render correctly. The screenshot exposed a static `Worker online` label despite no live health check; the follow-up changes it to authenticated-session copy and removes the local-pilot footer in browser mode. Those copy changes introduce no authorization/lifecycle behavior; existing UI tests and new-head hosted gates verify the rendered surface.

## Failure closure and limits

The first actual callback failed on473. Keycloak25+ puts access-token `sub` in its `basic` scope; the old client allowed only profile/email. Adding basic produced actual provider/callback/scoped authority PASS at293 while keeping required signed access/ID subject equality. Native select options have no visible layout box when closed; a local real Chrome control confirmed this and2f3's visible selector/exact attached owned option passed actual company/member/source proof. Later harness corrections use the supported metadata endpoint and accept an empty supported403 body while retaining mandatory denial status and strict positive payload checks. No security assertion was removed to make the gate pass.

Prior frozen reviews qualify bounded configuration, signed adapter, Redis/provisional/session/BFF/ingress/bootstrap/issuer/key/UI slices; final full-PR matching-head independent acceptance review remains required before merge. Actual approved production OIDC/HTTPS/TLS Redis, multi-company picker/member administration, Medcom/Novo inventory/stock-movement schema and least-privilege qualification, clean Windows lifecycle/signing and the remaining233 matrix are incomplete. Santino is deferred by the user. No customer/production writes, credential rotations or OS changes occurred.
