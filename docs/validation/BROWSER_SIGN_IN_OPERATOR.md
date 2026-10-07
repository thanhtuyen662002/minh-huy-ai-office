# Browser sign-in operator setup

Issue260 implements Authorization Code/S256 sign-in; issue233 tracks full product acceptance. This is a configuration procedure, not a record of a production deployment. Use an approved isolated environment first and record the exact release, provider/client, HTTPS host, Redis topology and passing acceptance evidence. Real Medcom/Novo inventory and stock-movement qualification is separate; Santino is deferred.

## Private deployment inputs

Use `.env.production.example` as the name contract. Populate the ignored private file from approved secret sources. These variables belong only to the Web server runtime; never use `NEXT_PUBLIC_*`, Git, tickets or browser storage for them.

| Variable | Required value |
| --- | --- |
| `AIOFFICE_BROWSER_OIDC_ENABLED` | `true` after the complete approved boundary is configured. |
| `AIOFFICE_AUTHORITY` | Exact HTTPS issuer, including its realm/path and canonical spelling; shared with Core. |
| `AIOFFICE_BROWSER_OIDC_PUBLIC_ORIGIN` | Exact public HTTPS app origin without trailing slash, path, query or credentials. |
| `AIOFFICE_BROWSER_OIDC_AUTHORIZATION_ENDPOINT` | Operator-pinned HTTPS provider authorization endpoint. |
| `AIOFFICE_BROWSER_OIDC_TOKEN_ENDPOINT`, `AIOFFICE_BROWSER_OIDC_JWKS_URI` | Operator-pinned HTTPS backchannel endpoints reachable from Web. |
| `AIOFFICE_BROWSER_OIDC_CLIENT_ID` | Dedicated browser client identity. |
| `AIOFFICE_BROWSER_OIDC_CLIENT_SECRET` | Empty for a public PKCE client; approved private secret for a confidential client. |
| `AIOFFICE_BROWSER_OIDC_TRANSACTION_KEY` | Stable cryptographically random32-byte canonical base64url key, shared only by the authorized Web replicas. |
| `AIOFFICE_BROWSER_CORE_API_ORIGIN` | Exact approved HTTPS Core origin reachable from Web; no path, query or credentials. |
| `AIOFFICE_BROWSER_SESSION_REDIS_URL` | Private `rediss://` endpoint and approved credentials/database0..15; reachable from Web. |
| `AIOFFICE_COMPANY_ID`, `AIOFFICE_COMPANY_NAME` | Initial UI company context/display only. Core resolves actual membership and roles on every authorized operation; a user-facing multi-company picker remains separate product work. |

Production Compose pins local HTTP and disposable CI diagnostics to `false`, and keeps password grant disabled. Its ordinary plaintext Redis service cannot be used as production browser session authority. Qualify the configured Redis topology: standalone process restart invalidates prior browser rows; asynchronous HA rollback/promotion still needs separate invalidation/failover evidence. SQL remains the durable business/task store.

Generate the browser key once using the approved cryptographic secret generator. Keep it stable across repairs and Web restarts/replicas. Changing it invalidates pending transactions and encrypted sessions; plan a deliberate reauthentication window. No browser key should be derived from a customer ERP password. Local bootstrap's separate protected installation derivation is for its disposable/local profile only.

## Provider and ingress contract

Register the exact callback `<PUBLIC_ORIGIN>/api/local/session/oidc/callback`. Require Code flow, S256 PKCE and RS256 ID tokens. Disable implicit/direct-password/service-account grants on the dedicated client. Issue a Bearer JWT access token with the configured Core API audience and trusted provider identity claim. Signed access and ID subjects must agree; the ID audience must be this browser client alone, with any `azp` exactly the same client. Core uses its current SQL membership/roles, never token-supplied company or role grants.

For Keycloak25+, keep its `basic` client scope or an equivalent verified subject mapper so the access token contains `sub`; ID tokens alone are insufficient. The local owned client uses basic/profile/email and access-only API-audience/provider mappers. The installer does not provision or change an external production provider. [Keycloak subject mapper reference](https://www.keycloak.org/docs/26.8.0/upgrading/#sub-claim-is-added-to-access-token-via-protocol-mapper).

The approved ingress must terminate HTTPS and preserve the exact public app Host. OIDC mutation Origin must equal the configured public origin. Forwarded headers never select trusted endpoints or redirects. Web port3000 and Core port8080 remain host-loopback; expose only the approved HTTPS routes. Do not expose SQL, Redis, RabbitMQ or telemetry through public ingress. Browser cookies use `__Host-` names, Secure, HttpOnly, SameSite=Lax and Path=/.

## Acceptance before routing customer traffic

Run the repository production contract check on a privately rendered Compose config using the documented deployment procedure. The rendered file contains secret values; keep it in the approved private temporary location and remove it after validation. CI validates only synthetic operator inputs. A passing contract check does not prove connectivity or provider behavior.

With real HTTPS ingress, provider and TLS Redis, verify provider-page sign-in, scoped company/source/member/task operations, another-company denial, same-session role revocation, private no-store replies and cookie privacy. Replay the original callback transaction, submit wrong state, log out and present old cookies, then expire a session and require private chat/draft clearing plus fresh provider reauthentication. Validate retained owner/source/grant/data identity after repair/restart. Record only sanitized results and exact version/run references; provider bodies, callback codes, tokens, cookies and credentials stay private.

Keep issue260 acceptance and the whole233 requirement matrix open until their respective actual gates pass. No production deployment or customer write is performed by this document.
