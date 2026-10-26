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

## Tài liệu

- [OWNER_CORRECTION_AUTO_IT.md](OWNER_CORRECTION_AUTO_IT.md): quyết định sản phẩm và luồng chính.
- [CODEX_PROMPT.md](CODEX_PROMPT.md): prompt triển khai code thay prompt cũ.
- [CONTRACTS_AND_FLOWS.md](CONTRACTS_AND_FLOWS.md): data/state/event boundaries.
- [DELIVERY_AND_ACCEPTANCE.md](DELIVERY_AND_ACCEPTANCE.md): issue sequencing và gate no-click.
- [backlog.json](backlog.json), [golden batch](examples/golden-batch.json), [cases](acceptance-cases.jsonl): v2, không yêu cầu approval.
- [VALIDATION.md](VALIDATION.md): kết quả kiểm tra planning assets, không application tests.

Repo dùng C#/SQL Server/RabbitMQ/Next.js và gateway/context/worker hiện có; Node connector nếu cần. Không bật session, gửi tin hoặc deploy khi cập nhật kế hoạch. Tất cả live capabilities vẫn cần evidence thực.
