# Scoped company member directory

The supported admin inventory reads current company memberships joined to users in the same tenant. Active and inactive member/user status and scoped role assignments are visible to the company's current active `admin`. Identity provider, subject and credentials are omitted. The request cannot supply tenant, actor or role authority.

The service resolves current administration before queries and again before returning data. Authorization must match tenant, company and actor exactly and the ordinal `admin` role. Pagination is stable by user ID, limited to 100 entries and an offset of 1000. Role queries cover only the selected page and refuse collections over 256 roles per member rather than publishing a truncated authorization inventory.

API/BFF success, invalid requests, missing configuration and denial responses are not cacheable. The frontend keys private inventory by company/session generation, validates the session before and after reads, clears displayed data on reload/failure, reconciles 403 authority changes and immediately clears expired 401 sessions. Late responses after scope changes cannot repopulate the screen.

This read-only inventory is a prerequisite for audited member invitations and administration mutations. It does not authorize assigning new roles or identities, complete production browser OIDC, or qualify real customer deployment.
