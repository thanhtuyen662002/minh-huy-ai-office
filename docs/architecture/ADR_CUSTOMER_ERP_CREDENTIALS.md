# Customer ERP credentials from the web UI (#296)

Company administrators can configure a customer SQL Server database without a
raw connection string or an operator-created environment binding. The existing
`secretref://env/PILOT_ERP_CONNECTION` path remains supported.

`ProtectedDataSourceSecrets` is a separate runtime-managed binding store. It does
not make `DataSourceSecretBindings` writable. Each protected binding has the same
ID as its source, a globally unique ID, tenant/company/source foreign key, and
optimistic version. The scoped binding service projects this binding only after
fresh directory and operator-store permission checks. Workers use the same
`ScopedDataSourceSecretResolver` before and after decryption, so rotation or
revocation while resolution awaits refuses the stale binding.
Legacy full source-management routes cannot clone, rebind, change the kind or
enable write policy for protected customer sources; ordinary metadata updates
remain available. Credentials can only change through verified rotation.

AES-256-GCM encrypts the server-built connection string. A fresh 12-byte nonce
and 16-byte authentication tag accompany every ciphertext. Versioned associated
data includes tenant, company and binding IDs; moving ciphertext to another
scope fails authentication. `AIOFFICE_SECRET_ENCRYPTION_KEY` must contain standard
base64 encoding exactly 32 bytes. Missing/malformed keys fail closed for protected
operations. Legacy environment references continue to resolve without this key.
Request fingerprints are HMAC-SHA256 under that key, rather than unkeyed password
hashes. No credential fields appear in source descriptors, audit rows or errors.

Create and rotation verify current SQL permissions in the same request before
committing source/binding/audit state in one serializable transaction. Operation
IDs are unique per company in the existing append-only registration audit.
Identical retries return the committed result without a second mutation; changed
requests conflict. Rotation also requires the lossless current binding version.
The SQL migration preserves the immutable operator binding and registration audit
permissions, gives runtime access only to the new credential table, and makes
connection-test audits append-only. Destructive down migration is refused.

Only current company administrators may list, test, create or rotate these
connections. Test/save/rotation share ten attempts per company per minute per API
instance with no queue. Only explicit TCP SQL Server host/port fields are accepted.
The connection has a five-second open timeout, no retries or pooling; the complete
SQL verifier has a fifteen-second deadline. Egress policy should restrict SQL
destinations in multi-instance deployments, where a shared gateway limit should
also enforce the aggregate company quota.

The existing conservative read-only verifier is unchanged: SQL Server 2022,
standalone user database, visible metadata, direct read grants and no delegation,
write, ownership or unsafe module permissions. Its fixed-role refusal includes
`db_datareader`. The UI therefore supplies `CREATE LOGIN`, server metadata grant,
`CREATE USER`, **direct `GRANT SELECT`**, and database metadata grant; this is an
intentional correction to the issue's suggested fixed-reader-role script. It
does not relax the accepted ERP security profile. Older SQL versions, availability
groups and customer-specific executable module profiles need separate qualification.

The form keeps credentials only in component memory. It remounts with company or
session generation changes; logout, expiry, role loss and late replies cannot
populate the next workspace. Successful save clears the password. A failed or
lost save response retains a frozen operation ID/body for a deliberate retry;
cancel clears that state. Save always re-verifies, even after a prior successful
UI test. Connection edits invalidate that test.

## Provisioning and recovery

Provision one random 32-byte key through the approved secret source to **both**
Core.Api and Agent.Worker, in standard base64. Production preflight requires it;
Compose forwards it to both services. Tracked environment templates contain only
an empty placeholder. The local installer derives a distinct purpose-separated
key from its protected high-entropy installation manifest and replays it stably.
No existing deployment or customer credentials are enrolled by this change.

Back up the encryption key securely alongside database recovery material, under
separate access controls. Losing it makes retained ciphertext unusable. Replacing
the key directly makes old bindings and operation fingerprints unreadable; a key
rollover requires a future controlled decrypt/re-encrypt migration, or deliberate
re-enrollment with new operations. Do not put the key in Git, browser configuration,
logs, chat, screenshots or ordinary support exports. Use valid TLS certificates
and leave transport encryption enabled for internet-facing ERP databases.

Migrations must run after any already-landed #294 identity migrations. This slice
adds separate tables and does not change company-selection or invitation contracts.
