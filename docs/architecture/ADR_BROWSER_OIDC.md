# Browser OIDC sign-in

Issue260 / Draft261 implements the product browser Authorization Code/S256 PKCE flow. This document records an active implementation, not completed browser or production acceptance.

## Trusted configuration and login transactions

`apps/web/lib/browser-oidc.ts` obtains public app origin, exact issuer, authorization/token/JWKS endpoints, client identity and transaction key exclusively from operator environment configuration. An invalid enabled configuration fails generically. Deployed endpoints require HTTPS. Explicit local HTTP mode requires a loopback app and browser identity; the only additional HTTP backchannel host is the fixed `identity:8080` local service. Request Host/forwarded headers never choose a callback or provider endpoint. The callback is fixed at `/api/local/session/oidc/callback`.

Configuration names are `AIOFFICE_BROWSER_OIDC_ENABLED`, `AIOFFICE_BROWSER_OIDC_PUBLIC_ORIGIN`, `AIOFFICE_BROWSER_OIDC_ISSUER`, `AIOFFICE_BROWSER_OIDC_AUTHORIZATION_ENDPOINT`, `AIOFFICE_BROWSER_OIDC_TOKEN_ENDPOINT`, `AIOFFICE_BROWSER_OIDC_JWKS_URI`, `AIOFFICE_BROWSER_OIDC_CLIENT_ID`, `AIOFFICE_BROWSER_OIDC_CLIENT_SECRET` (optional for a PKCE public client), `AIOFFICE_BROWSER_OIDC_TRANSACTION_KEY` (canonical base64url encoded32 random bytes), and `AIOFFICE_BROWSER_OIDC_LOCAL_HTTP` (local disposable/installed loopback use only). Raw origins have no trailing slash; issuer/endpoints are canonical absolute URLs without credentials/query/fragment. No production credentials are tracked here.

Every transaction creates independent256-bit random state, nonce and verifier, binds a canonical requested company and expires after300 seconds. AES-256-GCM authenticates and encrypts these values. HKDF separates the transaction encryption context by exact issuer/client/fixed callback; ciphertext cannot move between application/issuer/client configurations. The serialized cookie is bounded to2048 characters; malformed encodings, tampering, unknown fields, unsafe timestamps, extended lifetime or expired cookies fail closed. A10-second maximum future issuance allowance handles bounded server clock skew. Production transaction cookies use the `__Host-` name with Secure/HttpOnly/SameSite=Lax and Path=/; the route must enforce those properties when it is added.

The transaction is short-lived authentication coordination. It carries no platform task state, user/tenant authority or roles. The API must resolve fresh company/user/role authority after verified token exchange and before a session is issued. Stateless transaction cookies require a stable shared private transaction key across BFF instances; a key change invalidates only pending logins.

## Remaining acceptance

The configuration/transaction module does not yet enable a sign-in route. Start/callback, maintained-library signed ID-token/state/nonce/PKCE checks, access token/API validation, private session cookies and UI mode, retained local browser-client bootstrap/public issuer routing, actual Chromium/SQL/Keycloak sign-in/expiry/denial proof and independent exact-head/main closure remain required. Existing local password-grant and source/member/task controls stay active under their current explicit configuration until the browser flow is verified. No customer/production identity/configuration/data changes occur in this checkpoint.

Primary references: [OIDC Core](https://openid.net/specs/openid-connect-core-1_0.html), [openid-client API](https://github.com/panva/openid-client/tree/main/docs), [Keycloak public/backchannel hostname](https://www.keycloak.org/server/hostname).
