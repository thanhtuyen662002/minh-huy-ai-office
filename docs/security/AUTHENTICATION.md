# HTTP authentication and authorization context

Core.Api treats the ASP.NET Core authenticated principal as the only HTTP identity boundary. Caller-controlled headers never assert `TenantId`, `CompanyId`, or `UserId`.

## Production configuration

Configure both values below through the deployment configuration/secret-management layer; do not commit credentials or tokens:

- `AIOffice:Authentication:Authority` — HTTPS OpenID Connect/OAuth issuer/authority used by the JWT bearer handler.
- `AIOffice:Authentication:Audience` — API audience expected in access tokens.

Both values are required to enable the JWT bearer pipeline. Tokens are validated for issuer, audience, lifetime, and signing key. The authenticated principal must contain the stable identity-provider claim `idp` and subject claim `sub` (or the standard name-identifier fallback for subject).

The request may select a company with `X-AIOffice-Company-Id`, but this value is untrusted input. Core.Api resolves `(identity provider, subject, company)` server-side and creates an `AuthorizationContext` only when the user, company, and membership are active. Roles are read in the same database consistency boundary. A malformed company selector, missing trusted identity, inactive membership, or cross-company attempt fails closed.

Observability headers remain telemetry hints only and are never authorization inputs.

## Local and integration tests

Tests must replace the ASP.NET Core authentication scheme with a deterministic test authentication handler/fixture. The fixture may emit controlled `idp`/`sub` claims for a test principal, but it must live only in test code and must never be registered by production startup. Integration tests must still pass company selection through the normal untrusted selector and membership resolver so spoofed tenant/user headers cannot bypass server-side authorization.

Do not add a development backdoor, static bearer token, fake production user, or header-based trusted identity mode.