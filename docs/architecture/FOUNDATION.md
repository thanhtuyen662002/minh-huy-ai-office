# Architecture Foundation

## Product
Minh Huy AI Office is a multi-tenant agent platform for ERP/accounting/support/operations. It must survive worker/model/session failure, schema evolution, ERP customization and provider change.

## Initial stack
- Web: Next.js + TypeScript + shadcn/ui on Vercel.
- Backend: ASP.NET Core / C#.
- Main state DB: SQL Server.
- ERP access: Dapper/tool adapters; EF Core for platform CRUD.
- Messaging: RabbitMQ.
- Cache/ephemeral coordination: Redis.
- Realtime: SignalR.
- AI: provider-neutral AI Gateway.
- Files: object storage.
- Deployment: Docker/Compose initially.
- Observability: OpenTelemetry.

## Planes
1. Identity Plane — users, companies, memberships, roles.
2. Resource Plane — DBs, ERP systems, APIs, credentials references.
3. Context Plane — conversation, task memory, company memory, context assembly.
4. Control Plane — permission, policy, model/skill/workflow registries.
5. Execution Plane — manager, scheduler, queues, workers, tools.
6. Data Plane — SQL Server, ERP DBs, files, external APIs.
7. Change/Release Plane — GitHub, migrations, CI/evals, releases, canary/rollback.
8. Observability/Billing — audit, usage, costs, traces.

## Core invariants
- Agent depends on capabilities, not implementation details.
- Task state never lives only in an LLM context.
- User/company/database routing is dynamic.
- Every tool call is authorized mechanically.
- Writes are idempotent where repeat execution can cause duplicate effects.
- Every durable task is resumable from checkpoints.
- Every release is attributable to source commit + migration + workflow/skill versions.
- Schema/API evolution defaults to compatibility windows.
- Production self-upgrade is controlled by gates and rollback.

## Runtime task identity
Every meaningful execution should be traceable by:
TenantId + CompanyId + UserId + ConversationId + TaskId + AgentRole + WorkerId + ReleaseId + TraceId.

## Database resources
Agents never receive raw connection strings as business context. They request logical data sources such as `company.erp.production`; Tool Gateway resolves and authorizes the concrete connection.

## Context
Context Engine assembles only what the agent needs: system policy, user/company scope, task memory, selected conversation messages, retrieved knowledge, current tool results. Raw prompts may be retained as encrypted/retention-controlled audit snapshots, not primary memory.
