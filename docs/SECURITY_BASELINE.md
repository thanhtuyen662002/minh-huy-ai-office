# Security Baseline

- Default deny for tool/data access.
- Tenant/company/user/task scope included in authorization decisions.
- Logical data source references; credentials stored in secret manager, never prompts/Git.
- SQL writes require policy checks; destructive operations require elevated approval.
- Row/query/time limits and transaction boundaries enforced in Tool Gateway.
- Audit every tool execution with who/what/company/task/result.
- Redact/encrypt sensitive prompt/tool snapshots and apply retention policy.
- Remote customer SQL should use private networking/VPN/tunnel rather than public 1433.
- Separate dev/staging/prod credentials and databases.
- Object storage for large files; DB stores metadata.
- Idempotency keys for repeat-sensitive business mutations.
