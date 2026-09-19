# Security Baseline

- Default deny for tool/data access.
- Tenant/company/user/task scope included in authorization decisions.
- Logical data source references; credentials stored in approved secret providers, never prompts/Git.
- Secret-bearing application/database fields store opaque `secretref://provider/resource` references, never resolved passwords, tokens or connection strings.
- Resolved secrets exist only at the runtime boundary that needs them and must not be logged, audited, cached as business data or sent to models.
- SQL writes require policy checks; destructive operations require elevated approval.
- Row/query/time limits and transaction boundaries enforced in Tool Gateway.
- Audit every tool execution with who/what/company/task/result while redacting sensitive values.
- Redact/encrypt sensitive prompt/tool snapshots and apply retention policy.
- Remote customer SQL should use private networking/VPN/tunnel rather than public 1433.
- Separate dev/staging/prod credentials, secret namespaces and databases.
- Object storage for large files; DB stores metadata.
- Idempotency keys for repeat-sensitive business mutations.
