# Contracts v2 — autonomous intake and internal IT routing

Authority: [owner correction](OWNER_CORRECTION_AUTO_IT.md); detailed implementation: [CODEX_PROMPT.md](CODEX_PROMPT.md). Earlier manual-review/same-source-group contracts in PR #280 are superseded, not additional requirements.

## Boundaries

Connector→verified source receipt→SQL inbox/outbox→automatic frozen batch→group brain/AI Gateway→code validation→SQL CustomerRequest/attention + NotesCommitted transaction→notification worker reads committed revisions→internal route authorization→outbound technical report→connector.

No business human approval or manual Generate/Accept/Approve/Send prerequisite. Operational setup grants and normal code/CI/release review remain. IT edits/status updates after receiving work are not a gate for auto intake/notification.

## Data contracts to implement

| Record | Invariants |
|---|---|
| SourceGroupBinding | tenant/company/account/external group, intake grant, role=customer_source, listen flag; no outbound permission for this feature |
| TechnicalDestinationBinding | explicit account/external IT group, audience scope, role=technical_internal; not auto-enrolled as input |
| GroupNotificationRoute | source→destination, per-source disclosure grant, version/revocation, enabled policy; no name matching or model-supplied target |
| GroupMessage/Revision/Receipt/Gap | source IDs/sender/reply/time, ordinal provider identity, durable committed sequence/revision/hash, coverage/deletion generation |
| SummaryBatch/BatchMessage | exact committed message IDs/revisions, input/model/policy digest, checkpointed due time and cutoff; late arrivals go to delta |
| CustomerRequest/RequestEvidence/RequestRevision | auto-created business note, source group, evidence, AI/customer authority, new/needs_clarification, unknown fields explicit; no invented SLA/resolution |
| NotesCommitted outbox | atomic with note transaction; no publish on rollback; stable event ID, notes/revision refs only |
| NotificationBatch/Item | immutable selection of committed note revisions grouped by authorized technical destination, individual source labels/grants |
| OutboundTechnicalReport | fixed destination + route/grant/source/deletion versions, content digest, idempotency key, dispatch/accepted/failed/unknown; no DigestApproval prerequisite |

Raw internal credentials, complete private note objects and sensitive messages do not enter report/model. Scope derives from trusted service grants, not external sender as portal user/admin. Cross-group matches are not identity/merge authorization. Aggregation renderer may combine already authorized packets; extraction contexts remain scoped per source. Same-title groups remain distinct.

## State and failure behavior

Input→batch scheduled→extracting→notes_committed→notification_pending→dispatching→accepted/unknown/failed/blocked. `needs_clarification`/`extraction_failed` is an attention-note category that is automatically notified to IT; it is not awaiting_approval. CustomerRequest business progress is separate from worker completion.

Proposed tuning: debounce 30 seconds or max-wait 120 seconds; values configurable/tested, not user-specified SLA or a ChatGPT schedule. Timers/checkpoints must survive restart. Continuously arriving input must still produce bounded windows. Historical backfill is explicitly no-send. Each message has disposition; chunk rather than silently discard input exceeding model budget.

Missing data/model failure after bounded retries yields scoped attention record + IT notification. Gap yields incomplete-coverage notice with known work, not false completeness. Invalid auth/route fails closed; notes remain, configuration alert appears in existing safe operations channel/dashboard. Never fallback to source group.

Source/route/grant/version changes invalidate queued reports. Rebuild/revalidate automatically for content changes; revoked access blocks. Unknown send is reconciled without blind retry. Echo from IT output is not input. No direct customer replies, autonomous coding, ERP writes or money execution.

## API/DI requirements

Reuse Core.Api/BFF auth, scoped SQL repositories, migrations, authorized worker and AI Gateway. Add service ingest and source/destination route admin APIs, GET inbox/notes/coverage/report status, optional post-notification IT task updates. Automatic batch and send are worker responsibilities; UI reload/read has no side effect. Do not introduce approval endpoints as a dependency for this feature.

Production registration/call-path tests must prove the brain is called and committed notes actually trigger notification. One implementation issue per reviewed branch/PR; respect active leases and root governance. Feature enablement and actual account operation remain separate from this plan-only correction.

## Connector current enrollment and recovery

`POST /internal/group-ingress/enrollment` is enabled with the existing default-off
group intake gate. It uses the separately scoped `aioffice-group-enrollment-v1`
HMAC domain and the same five canonical service headers. The fixed host supplies
exact source scope and external identity; neither a participant nor a model can
select a credential reference. A portal session does not authenticate this API.

The backend reads current company/source/account/Ingest service grant and current
receive qualification inside its owned Serializable authority transaction,
checks declared scope before key resolution, then rechecks current authority and
qualification after the service key await. Its strict bounded no-store metadata
DTO contains versions, artifact/observation evidence and UTC checked time. It
contains no credential reference, source content, portal user or fabricated lease.

The connector reconstructs qualification only from a validated response from its
pinned host, rejecting wrong scope/identity/service epoch, future or more than
10-second-old metadata and unqualified profiles. Operational backlog replay
fetches enrollment on every call and renews the actual owned owner/epoch through
the separately signed listener API before disk load or spool-key resolution.
Current stored source/grant/deletion/credential versions must match. One owned
10-second deadline spans fetch/renew/replay; metadata freshness is checked before
load, after spool-key resolution and before acknowledgement/deletion. A failed
metadata fetch, denied renewal or unknown commit preserves encrypted bytes.

These are short current snapshots; server authority and ownership are checked
again at final SQL acceptance. Fetching metadata does not activate a provider,
listener worker, polling loop, model call or broker delivery. Live artifact
qualification, host private-volume provisioning and the operational connector
registration remain acceptance work.
