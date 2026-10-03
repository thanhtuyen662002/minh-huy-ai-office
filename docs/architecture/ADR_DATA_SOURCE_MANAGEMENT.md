# Company authority for data-source management

Data-source registry reads are available to an active company member. Create and update operations require the exact `admin` role resolved by the server from the current tenant, company, user and active membership. Roles carried by an issued identity token or submitted by a caller cannot grant this permission.

The service resolves authority for every mutation before validation or persistence, including edits to connection references, read/write permissions and enabled state. Revocation therefore takes effect for existing sessions. Inactive users, companies or memberships and roles in another company cannot grant access. Denied mutations return HTTP 403 and leave stored metadata unchanged.

The local frontend relays POST and PUT through its HttpOnly session, requires a matching request origin, validates company/source identifiers and forwards Core API authorization failures. Responses are not cacheable and do not forward upstream cookies. Registry projections omit stored connection references.

Origin validation uses the received request Host and protocol. Next.js can construct its internal URL with the container hostname; that URL must not reject a browser using the published loopback address. Caller-supplied forwarded hosts do not grant access, and malformed host authorities are rejected.

The real Compose gate backs up the original role assignments and timestamps, removes only the current owner's admin assignment, reuses the already issued session for API and frontend requests, and verifies unchanged source fingerprints. It restores the exact assignments in a transaction, exercises successful admin create/update and removes only the test fixture. This verifies authorization behavior; complete company/member/source onboarding and credential management remain separate product requirements under #233.
