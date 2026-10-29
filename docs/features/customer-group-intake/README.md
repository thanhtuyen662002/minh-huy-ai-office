# Nhóm khách → tự ghi phiếu DB → tự báo nhóm Kỹ thuật

**Yêu cầu hiện hành v2, 2026-10-10: KHÔNG có nhân viên duyệt, KHÔNG gửi báo cáo về nhóm khách.** Đọc [đính chính của owner](OWNER_CORRECTION_AUTO_IT.md) trước. Bản reviewed same-group digest từ PR #280 là hiểu sai đã được thay thế.

Luồng: các nhóm khách được cấu hình → connector → Agent tiếp nhận/bộ não đúng nhóm → tự lưu yêu cầu/ghi chú vào SQL → event sau commit → Agent báo việc đọc DB → tự gửi báo cáo vào nhóm Kỹ thuật nội bộ đã cấu hình.

IT nhận thông báo để xử lý, không cần duyệt đầu vào. Việc thiếu thông tin vẫn được ghi và báo IT với nhãn cần làm rõ; không nằm im ở hàng chờ phê duyệt. Thiết lập quyền/route ban đầu và code review/CI không phải approval từng tin.

## Triển khai ngay trong AI Office

#276 chốt connector + source-to-IT route/service contracts. #277 lưu tin/coverage. #278 automatic batching + bộ não + tự tạo phiếu/attention + NotesCommitted. #279 agent đọc DB + auto IT-only notification + recovery/pilot. Full wiki và trả lời khách làm sau.

Nguồn/đích là hai loại binding khác nhau. Một nhóm IT có thể nhận từ nhiều nhóm khách được grant; từng item phải rõ nguồn. AI không chọn nhóm nhận. Không có route thì lưu phiếu và báo lỗi, không gửi về nhóm nguồn. Giữ source/customer boundaries và tách dữ liệu không được phép khỏi notification.

## Summarized customer request bridge (#295)

The separate `zalo-intake` bot can record each already summarized request with `POST /internal/customer-requests`. This is a backend-only light bridge alongside raw-message ingress (#276/#277). It makes no AI calls and sends nothing to Zalo. Recording is automatic, with no per-item approval. Every ticket retains its source group, evidence, customer desired due text and immutable `ai_extracted` origin; nonempty open questions initialize `needs_clarification`, otherwise `new`. A customer desired date stays free text and creates no IT commitment.

Enable and enroll a service using the [customer request configuration](../../CONFIGURATION.md#summarized-customer-request-bridge-295). The bridge is off by default (404). The company and tenant come only from that service's configuration. Example request body:

```json
{
  "idempotencyKey": "summary-customer-a-20261026-001",
  "sourceGroupExternalId": "authorized-customer-group-id",
  "sourceGroupName": "Khách hàng A",
  "customerName": "Customer A",
  "reportedBy": "Lan",
  "summary": "Không lưu được đơn hàng khi chọn kho mới.",
  "customerDesiredDueText": "Khách mong muốn xử lý trước chiều mai",
  "sourceWindowFrom": "2026-10-26T01:00:00Z",
  "sourceWindowTo": "2026-10-26T02:00:00Z",
  "items": [
    { "content": "Kiểm tra lỗi lưu đơn ở kho mới", "priority": "high", "kind": "bug" }
  ],
  "evidence": [
    {
      "quote": "Chọn kho mới thì không lưu được đơn",
      "sourceMessageId": "message-123",
      "senderName": "Lan",
      "messageTimestamp": "2026-10-26T01:15:00Z"
    }
  ],
  "openQuestions": ["Phiên bản ERP và mã kho nào?"]
}
```

Use `application/json`, at most 65,536 UTF-8 bytes, with no query string. All shown fields are required except `customerDesiredDueText` (omitted or null is allowed). UTC timestamps must use a zero offset; the source window must be ordered. Limits: 1–50 items, 0–100 evidence entries, 0–50 open questions. Strings are nonblank: source ID/idempotency key/source name/customer/reporter/message ID/sender ≤200 UTF-16 units, summary ≤8,000, item content ≤4,000, quote ≤2,000, due text/question ≤1,000. Opaque IDs/keys cannot have leading/trailing whitespace or control characters. Priority is `high|medium|low`; kind is `bug|feature|question|data`. Unknown fields, duplicate properties, malformed UTF-8, missing/null required fields and invalid values return 400; oversized bodies return 413.

Send these single-value headers (UUIDs in lowercase canonical `D` format; time as canonical decimal Unix seconds):

- `X-AIOffice-Request-Service`: enrolled service UUID.
- `X-AIOffice-Request-Timestamp`: current UTC Unix seconds, at most five minutes past or future.
- `X-AIOffice-Request-Nonce`: fresh UUID for **every HTTP attempt**, including idempotency retries.
- `X-AIOffice-Request-Signature`: uppercase hexadecimal HMAC-SHA256.

The signing key is the decoded 32-byte Base64 environment secret. Sign the following ASCII bytes, separated by literal LF (`\n`), **without a trailing LF**:

```text
aioffice-customer-request-v1
<service UUID>
POST
/internal/customer-requests
<timestamp>
<nonce UUID>
<uppercase hexadecimal SHA256 of exact UTF-8 request-body bytes>
```

The method, exact path, timestamp, nonce, body hash and service identity are bound to this dedicated domain. Replayed nonces are rejected with 403, including across API restarts/instances; the SQL nonce cache retains accepted nonces for ten minutes and cleans expired entries during subsequent ingestion for that service. An idempotency retry uses a **new nonce/signature** and the same `idempotencyKey`. It returns 200 with the original ticket (no content overwrite or duplicate). Normal creation also returns 200, for example `{ "code": "YC-0001", "id": "<ticket UUID>", "status": "needs_clarification" }`. Codes start at `YC-0001` independently per company. No service secret or secret reference appears in responses.

Company members use the existing authenticated portal API and `X-AIOffice-Company-Id`, matching the URL:

- `GET /api/companies/{companyId}/customer-requests?status=new&customer=Customer&offset=0&limit=50`: customer name substring filter, exact status filter, stable newest-first paging, response `{ items, offset, limit, hasMore }`; limit 1–100 and offset 0–100,000.
- `GET /api/companies/{companyId}/customer-requests/{code}`: response `{ request, reportedBy, customerDesiredDueText, sourceWindowFrom, sourceWindowTo, items, evidence, openQuestions, events }`. `request` includes source identity, status, origin, assignee, timestamps and Base64 SQL `rowVersion`.
- `PATCH /api/companies/{companyId}/customer-requests/{code}`: JSON such as `{ "rowVersion": "<current Base64 SQL rowversion>", "status": "in_progress", "assigneeUserId": "<active company member UUID>" }`, at most 2,048 bytes. Status is `new|needs_clarification|in_progress|done|rejected`. Either status or assignee must be present. Omitted assignee keeps its value; explicit null unassigns. Any active company member may update. A stale version returns 409 with no changes; a successful change returns the updated list item and records each changed field as a user event. Creation is a service event. No-op updates create no event.

Portal responses are `no-store`. Inactive, revoked or foreign membership returns 403; unavailable ticket codes within the authorized company return 404. Bounded operational failures return 503; reconcile/retry the same summary key with fresh signature headers. No UI or bot changes are included.

### Status digest for the bot (#313)

`GET /internal/customer-requests/digest` (optionally `?since=<UTC Unix seconds>`) lets the same enrolled service read a ticket status digest to post into the internal technical group. It uses the same headers, key domain and nonce rules as ingestion, signs method `GET`, the exact path including the `since` query when present, and the SHA256 of an empty body. No other query field is accepted. `since` defaults to 24 hours ago and must be in the past and at most 31 days old. The route is off with the bridge (404).

Only tickets of the service's company and allowed source groups are included. Example response:

```json
{
  "generatedAt": "2026-10-29T10:30:00+00:00",
  "since": "2026-10-28T10:30:00+00:00",
  "counts": { "new": 2, "needs_clarification": 1, "in_progress": 3, "done": 5, "rejected": 0 },
  "open": [
    { "code": "YC-0003", "customerName": "Customer A", "sourceGroupName": "Khách hàng A", "summary": "Không lưu được đơn hàng…",
      "status": "in_progress", "assigneeName": "Minh", "createdAt": "2026-10-26T02:00:00+00:00", "ageDays": 3 }
  ],
  "openTotal": 6,
  "closedSince": [
    { "code": "YC-0001", "customerName": "Customer A", "sourceGroupName": "Khách hàng A", "summary": "…", "status": "done",
      "assigneeName": null, "closedAt": "2026-10-29T08:00:00+00:00" }
  ],
  "closedTotal": 1
}
```

`open` lists not-done, not-rejected tickets oldest first; `closedSince` lists tickets currently done or rejected whose last such status change happened since `since`, newest first. Each list holds at most 50 entries (totals give the full counts), summaries are cut to 120 characters, and no evidence quotes or message IDs are returned.

### Status commands from the technical group (#316)

Tech staff change a ticket's status in the internal technical Zalo group with a message such as `#xong YC-0012`. The `zalo-intake` bot parses the command (out of scope here) and calls `POST /internal/customer-requests/status` as the same enrolled service. Headers, key domain, nonce rules and the off-by-default flag (404) are the same as ingestion; sign method `POST`, the exact path `/internal/customer-requests/status` (no query string) and the SHA256 of the exact JSON body. Example body:

```json
{
  "code": "YC-0012",
  "status": "done",
  "actorName": "Minh Kỹ thuật",
  "actorExternalId": "<Zalo user id>",
  "sourceMessageId": "<technical group message id>"
}
```

All fields are required and nonblank, at most 2,048 bytes: `code` ≤24, the other strings ≤200 UTF-16 units; `actorExternalId`/`sourceMessageId` are opaque IDs without surrounding whitespace or control characters. `status` is `new|needs_clarification|in_progress|done|rejected`. Unknown fields, duplicate properties and invalid values return 400. A ticket outside the service's company or allowed source groups returns 404, exactly like a missing code.

Response: `{ "code": "YC-0012", "status": "done", "previousStatus": "in_progress", "changed": true }`. A change records a `status_changed` event with actor type `zalo_member`, the service UUID as actor ID, and the Zalo actor name/ID and source message ID; it updates the ticket's `updatedAt` and `rowVersion` under the same company lock as the portal PATCH, so a portal edit holding the old `rowVersion` then gets 409. Each `sourceMessageId` applies at most once per ticket: a retry (with a fresh nonce/signature) returns the current state with `changed: false` and `previousStatus` equal to `status`, and records nothing. Setting the current status is also a no-op without an event. The portal detail events include `actorName` (null for service and user events), shown in the web event history as the Zalo member's name.

## Tài liệu

- [OWNER_CORRECTION_AUTO_IT.md](OWNER_CORRECTION_AUTO_IT.md): quyết định sản phẩm và luồng chính.
- [CODEX_PROMPT.md](CODEX_PROMPT.md): prompt triển khai code thay prompt cũ.
- [CONTRACTS_AND_FLOWS.md](CONTRACTS_AND_FLOWS.md): data/state/event boundaries.
- [DELIVERY_AND_ACCEPTANCE.md](DELIVERY_AND_ACCEPTANCE.md): issue sequencing và gate no-click.
- [backlog.json](backlog.json), [golden batch](examples/golden-batch.json), [cases](acceptance-cases.jsonl): v2, không yêu cầu approval.
- [VALIDATION.md](VALIDATION.md): kết quả kiểm tra planning assets, không application tests.

Repo dùng C#/SQL Server/RabbitMQ/Next.js và gateway/context/worker hiện có; Node connector nếu cần. Không bật session, gửi tin hoặc deploy khi cập nhật kế hoạch. Tất cả live capabilities vẫn cần evidence thực.
