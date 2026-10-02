# Local AI Office Web UI

The local pilot is intended to be used from the browser. PowerShell remains an infrastructure/debug path, not the normal operator workflow.

## Start

1. Ensure Core.Api, RabbitMQ, Agent.Worker and local Keycloak are running.
2. Copy `apps/web/.env.local.example` to `apps/web/.env.local`.
3. From `apps/web`, run:
   ```powershell
   npm install
   npm run dev
   ```
4. Open `http://localhost:3000`.
5. Sign in with the local pilot Keycloak user.

## Browser flow

The root workspace supports:

- local sign in/sign out;
- authoritative tenant/company/user/role context from Core.Api;
- real authorized data-source listing;
- data-source connection test;
- read-only data-source selection;
- AI task submission;
- task polling;
- assistant answer rendering;
- provider/model/token usage display;
- bounded ERP evidence display.

The browser never receives the AI provider credential, ERP connection string or Core.Api bearer token. The local Next.js BFF exchanges the local credentials with Keycloak and stores the access token in an HttpOnly, SameSite cookie.

## Local-only boundary

The password-grant BFF is deliberately gated by `AIOFFICE_LOCAL_UI_ENABLED=true` and is for the local pilot only. Production identity remains the approved OIDC/browser flow and must not reuse the local password-grant route.

The UI does not expose arbitrary SQL or ERP writes.
