# Repository integration mapping — correction v2

Product authority: [OWNER_CORRECTION_AUTO_IT.md](OWNER_CORRECTION_AUTO_IT.md). The owner explicitly rejected manual note/report approval and returning the digest to source customer groups. The target is automatic IT-group notification after SQL commit. Earlier mapping text is historical in PR #280, not a runtime approval requirement.

The architectural/code baseline inspected before the planning merge was `420db0672fb4b61f28f7ad0de5e63d3bd2ca97ca`; main observed for this correction is `9155b987d028ad097e36b5cfebdc2ee8efe6fcb5` (merged plan #280). Re-read live files/PRs before coding; do not reset to a historical SHA.

- `docs/architecture/FOUNDATION.md`: retain ASP.NET Core/C#, SQL Server, RabbitMQ, Redis, Next.js, gateway/context/authorized worker.
- `src/Shared.Contracts/ConversationIngestion.cs`: useful manifest/sequence/source validation after batch creation; not a complete group source/destination authority contract.
- `src/Shared.Contracts/CustomerChatIngress.cs`: user-conversation authority cannot impersonate a group connector as an admin user. Add source service grants and internal notification routes explicitly.
- `src/Shared.Contracts/AiContextEngine.cs`: context source/manifest budgeting; checkpoint does not contain all raw source content. Protected source storage and scoped resolution remain required.
- `src/Agent.Worker/WorkExecutionServiceCollectionExtensions.cs`: execute behind authorization/audit wrapper; never expose raw executor or fake ERP datasource to bypass it.
- Durable source stores, per-source batch, CustomerRequest business notes, NotesCommitted outbox, route grants and automatic technical notifier must be implemented/tested; contracts alone are not feature completion.

Preserve open implementation leases, including #274/#239 if still current. This is a new plan/prompt correction, not application-code takeover. PROJECT_STATE/workstream files remain with their owners. #275–#279 are the tracking issues; #233 is not canceled.

Connector personal-account feasibility/risk remains an external gate; no new live capability, login, account approval or send was demonstrated in this correction. The video-inspired idea retained is source-backed notes and scoped memory, not a claim that the video exposed a particular connector backend.
