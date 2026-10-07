# Exact opaque authentication identity

Signed identity provider and subject values are opaque identifiers. The authoritative directory uses explicit SQL Server binary candidate comparisons and an ordinal comparison of the stored provider/subject before returning any context or roles. The ordinal fence also rejects SQL trailing-space padding aliases. Supported lengths retain the existing schema bounds: provider100 and subject200 UTF-16 code units. Exact identifiers are preserved without case folding, trimming or normalization.

Private identity strings appear only in the internal query projection. Public DTOs remain context/roles; tenant/company membership joins, active-user/company checks and ambiguity refusal remain unchanged. No schema, credential or grant changes are required.

The actual disposable SQL/OIDC gate changes only the owned local fixture's identity fields. It proves an explicit case-insensitive SQL comparison still aliases the rows while the same issued API token/BFF cookie is denied. Each variant restores the exact original fields and verifies a full-row fingerprint plus successful original-session access. No customer identity data is changed.
