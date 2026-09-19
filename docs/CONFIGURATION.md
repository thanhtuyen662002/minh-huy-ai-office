# Environment, Configuration and Secret References

Minh Huy AI Office treats configuration metadata and secret values as different classes of data. Git, normal application configuration, application tables, prompts and audit events may contain **secret references**; they must not contain resolved credentials.

## Supported deployment environments

Runtime components accept exactly three environment classes:

- `Development`
- `Staging`
- `Production`

Unknown environment names fail closed at startup so a misspelled production environment cannot silently inherit the wrong policy.

## Configuration precedence

ASP.NET Core components use the standard provider order. Later sources override earlier sources:

1. committed `appsettings.json` for non-secret defaults and configuration shape;
2. committed `appsettings.{Environment}.json` when environment-specific **non-secret** defaults are needed;
3. Development user-secrets where appropriate for developer-local metadata;
4. process/container environment variables;
5. command-line overrides.

Production deployment automation selects the environment and injects references. A higher-precedence source may replace a reference or non-secret setting, but must not turn a reference field into a plaintext credential.

Web/client configuration follows the same separation: only explicitly public values may be exposed to browser bundles. Secret references and secret values are server-side only.

## Secret reference contract

Secret-bearing settings use an opaque URI:

```text
secretref://<provider>/<resource>
```

Examples:

```text
secretref://env/AIOFFICE_DB_CONNECTION
secretref://azure-key-vault/minh-huy/production/sql/aioffice
secretref://vault/customer-123/erp/read-only
```

Rules:

- scheme is always `secretref`;
- provider is a logical resolver name, not a credential;
- resource identifies the secret inside that provider;
- user-info, query strings and URI fragments are rejected because they can accidentally carry credentials;
- plaintext connection strings, API keys and passwords are invalid where a `SecretReference` is required;
- reference strings are safe metadata, but resolved values are sensitive and must be kept out of logs, prompts, normal database columns and audit payloads.

The first runtime adapter is `env`, which resolves a portable environment-variable name. Future providers such as Vault or cloud secret managers plug into `ISecretResolver` without changing stored reference syntax or business modules.

## Platform SQL Server

The API reads:

```text
AIOffice:PlatformDatabase:ConnectionSecretRef
```

Environment variable form:

```text
AIOffice__PlatformDatabase__ConnectionSecretRef=secretref://env/AIOFFICE_DB_CONNECTION
```

The process receives the actual `AIOFFICE_DB_CONNECTION` value from an ignored local environment file, service manager, container secret injection, or an approved secret-manager adapter. The API resolves it in memory and passes it to the persistence layer; it is not written back to configuration or the platform database.

## Storage contract for future tables

Any table that needs a credential stores a reference field such as `CredentialSecretRef`, never a password/token/connection string. Resolver/provider configuration is infrastructure metadata. Rotation changes the underlying secret or reference under controlled release policy; agents continue to request logical resources rather than credentials.

## Operational rules

- Separate dev/staging/prod identities, secret namespaces and databases.
- Never commit a populated `.env`, certificate private key or secret export.
- Never log resolved secret values or include them in exception messages.
- Do not send resolved values to an LLM context.
- Secret-manager adapters must fail closed when a provider is unavailable or a reference is invalid.
- Production credential rotation follows the release/rotation mechanism; no ad-hoc source edits.
