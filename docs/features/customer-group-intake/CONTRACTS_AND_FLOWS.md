# Hợp đồng và luồng kỹ thuật — Group Intake MVP

Đây là đặc tả để coding agents implement, không API/schema DB đã triển khai. Review/additive contracts ở #276; không thay ngầm nghĩa của CustomerChatAuthority hiện tại.

## 1. Runtime và trust boundaries

```text
Zalo group message (untrusted content)
  → personal Node bridge: session + group allowlist + encrypted bounded spool
  → private Core.Api ingress: service authentication / grant / fencing
  → SQL receipt + versioned message + dispatch outbox (one transaction)
  → RabbitMQ job references (not full transcript)
  → Agent.Worker: frozen batch + group-scoped context → AI Gateway
  → validate structured candidates / source references → SQL drafts
  → operator review → CustomerRequest + revisions + audit (durable)
  → deterministic customer-safe digest → exact preview + approval
  → SQL outbound report intent → sender authorization → bridge → same group
  → provider acceptance/unknown/failure persisted; no invented delivery
```

DB là nguồn trạng thái. Redis/RabbitMQ không thay checkpoint SQL. Bridge auth có mTLS hoặc HMAC raw bytes, timestamp/nonce, binding allowlist, session generation và fencing epoch. Server derive TenantId/CompanyId/GroupBindingId từ grant và receipt; không tin model/group-message payload tự chọn scope.

Một service principal có capability `group.ingest`/`group.summarize`/`group.report.send` riêng được cấp/revoke/audit trong platform. Sender Zalo trong nhóm là external participant, **không** portal UserId hoặc company administrator. Một owner/staff user chạy manual action phải qua membership/role/group ACL hiện hành. Không dùng cookie của owner cho worker để giả danh quyền admin.

## 2. Identity và khóa nhận/gửi

`GroupBinding = TenantId + CompanyId + ConnectorAccountId + ExternalGroupId + BindingVersion`. Mỗi account/group active chỉ được bind tới một scope authoritative; rebind cần pause/drain/review, không đổi đích các intent cũ. Group display name chỉ để hiển thị, không là khóa. Không tự hợp nhất hai nhóm có cùng tên/khách.

Giữ provider IDs như opaque string, phân biệt hoa/thường/khoảng trắng theo contract provider; không parse thành GUID, không trim/lowercase để định danh. Với SQL Server phải kiểm semantics equality bằng binary/ordinal và length khi cần, không dựa collation mặc định có thể bỏ qua case/trailing spaces. IDs nội bộ theo chuẩn GUID platform, FK tenant/company/group phải cùng scope.

Inbound key tối thiểu `(binding_id, provider_message_id, provider_revision_or_event_discriminator)`. Body hash dùng phát hiện same-ID conflicting payload, không dùng làm key duy nhất vì hai tin cùng text vẫn có thể khác. Lưu reply-to và sender riêng; không đồng nhất GroupId với CustomerId.

Outbound destination luôn lấy từ approved `GroupBinding` gốc. AI output không có destination/group ID, phone, raw tool URL hoặc credentials. Nhóm IT thứ hai chỉ có route/projection/approval riêng sau MVP; không forward nguyên nháp nội bộ sang nhóm khách.

## 3. Dữ liệu cần bổ sung bằng migrations

| Record đề xuất | Fields/invariants |
|---|---|
| GroupBinding / GroupAccessGrant | scope, external group/account refs, display snapshot, purpose, allowed roles/team, listen_enabled/send_digest_enabled, authority_version, status; deny default |
| ConnectorSession / ConnectorLease / IngestionGap | encrypted secret ref, session generation, active lease epoch, heartbeat, gap interval/reason/evidence; một listener active/account |
| GroupMessage / MessageRevision | sender external ref, message ID/reply-to, text cipher/ref, media metadata, occurred_at/received_at, committed group sequence, revision/content hash, self-origin evidence, deletion version |
| GroupReceipt / DispatchOutbox | stable event key, auth evidence refs, persisted state, attempts, job ID; ACK sau transaction commit |
| SummaryBatch / BatchMessage / MessageDisposition | frozen exact IDs+revisions+hash, input digest, group/scope/config version, cutoff/coverage, review version, extraction state; mỗi message classified hoặc cần người xem |
| RequestCandidate | local stable key, title/type/summary/requested outcome, source refs per fact, proposed category/urgency, missing fields, related_request suggestion, draft/review status |
| CustomerRequest / RequestRevision / RequestEvidence | stable request code, group/customer-project mapping, reporter refs, public summary, internal notes separate, desired_by vs committed_due_at, priority proposed vs confirmed, assigned_team/user, business status, source refs, rowversion |
| GroupDigest / DigestItem | approved request IDs+revisions, public field whitelist, public item refs, batch ID, body digest/revision, omitted count if reviewed length compaction needed, scope/routing/ACL/source epochs |
| DigestApproval / OutboundReport | approver, exact content+destination+item revisions digest, expiry, idempotency key, policy version, send state, attempts/provider ID, accepted/unknown evidence |
| Audit / DeletionTombstone | actor/operation/object/version/reason; no raw credentials; invalidates derived drafts/reports/jobs/spool/cache/exports |

`CustomerRequest` không phải runtime WorkTask/TaskHistory. Một summary runtime job có thể sinh nhiều request notes; một request có nhiều lượt bổ sung. Hoàn thành runtime job chỉ là trích xuất xong. Worker không được tự chuyển trạng thái nghiệp vụ sang resolved. Nhân viên IT hiện có quyền nhận công việc không mặc nhiên có quyền đọc mọi nhóm hoặc owner task archive.

## 4. Batch snapshot và chống bỏ sót

MVP trigger: operator POST **Generate new summary**, không GET hoặc UI reload. Cấu hình schedule/auto-draft để tắt; owner chọn cadence sau. Manual action cần idempotency và CAS; hai cú bấm cùng tập tin trả về cùng batch hoặc conflict, không tạo hai đợt song song chồng nguồn.

Mỗi binding có bộ đếm durable thứ tự ingest được cập nhật cùng insert trong transaction có serialization/lock ngắn. Không dùng `MAX(identity)` tùy ý làm watermark khi transaction có thể commit đảo thứ tự; phải chứng minh không có tin đã được cấp số nhưng chưa commit bị nhảy qua. Batch chụp exact committed receipt IDs/revisions và `cutoff_ingest_seq`, giữ trước khi gọi AI.

Tin có occurred_at cũ nhưng nhận sau cutoff có ingest seq mới nên vào batch delta tiếp theo. Message edit/recall nếu connector quan sát được là mutation event mới trỏ source version; invalidate draft/approval liên quan trước send. Không giả hỗ trợ edit/history khi capability chưa test. Import lịch sử nằm trong job `backfill_no_send`, được đánh dấu, không kích hoạt report hàng loạt.

Tách con trỏ `ingested_through`, `extracted_through`, `reviewed_through`, `reported_batch_ids`; không chỉ một timestamp. Extraction complete khi mọi message có disposition; review có thể để request cần làm rõ nhưng vẫn giữ nguồn. Report pending/unknown không được coi delivered. Batch review chuyển tiếp khi mọi nguồn có disposition; yêu cầu chưa rõ có thể mở, không bị bỏ quên.

Nếu context quá lớn: chunk giữ speaker/time/reply relationships, persist per-chunk evidence, merge candidates ở đúng group. Giới hạn số calls/tokens bằng config đã đo; không silently truncate và báo đã tổng hợp hết. `coverage = complete | partial_gap | partial_media | processing | failed`; phải ghi rõ phạm vi chưa kiểm. Gap chưa đối soát mặc định chặn gửi; reviewer có thể gửi bản chỉ nói “phần tin đã nhận” với caveat đã duyệt, không tự đổi coverage thành complete.

## 5. Trích xuất và review yêu cầu

Đầu vào AI chỉ có policy tối thiểu, glossary đã duyệt, frozen messages và một tập request cùng nhóm liên quan. MVP không cần semantic vector search: lọc theo nhóm/mã yêu cầu/reply-to và tìm text có giới hạn; shared wiki/RAG sau.

Output `RequestExtractionProposal` chỉ gồm:

```text
schema_version, candidate_key,
kind: incident | change_request | question | followup | needs_review,
title, customer_reported_problem, requested_outcome,
source_refs: [message_id + revision + fact/quote locator],
reported_environment, requested_deadline_text,
missing_fields, suggested_related_request_id,
proposed_urgency, uncertainty_reason
```

Không có TenantId/CompanyId/GroupId/UserRole, trusted status, resolved time, committed deadline hoặc lệnh thực thi trong AI output. Server kiểm source IDs tồn tại và thuộc input snapshot cùng scope, quote/range có thật, các con số/phủ định/hạn yêu cầu đúng nghĩa. Schema valid không đủ semantic correctness. Với `suggested_related_request_id`, server kiểm nhóm/quyền và người review xác nhận merge; similarity không tự gộp.

Một câu có hai việc phải tách; nhiều người cùng một việc có thể gom nếu evidence đủ. “Gấp” chỉ là urgency khách đề nghị, chưa là SLA hệ thống. “Bên em xử lý rồi” từ khách ghi customer_reported_resolved, không tự đóng phiếu IT. Deadline “mai/15h” gắn timezone/source time; không rõ yêu cầu/ngày thì giữ raw text và missing clarification, không suy deadline cam kết.

Nhận ảnh/audio chưa đọc được phải tạo attention flag/needs_review kèm metadata, không nói đã hiểu nội dung hoặc im lặng bỏ qua. MVP không tải URL tùy ý, không OCR/voice auto. Notification/escalation nội bộ cho nghi sự cố nghiêm trọng, không auto trả lời hoặc tự đổi production.

Review action có rowversion; save requests và evidence trong transaction. Stable candidate acceptance key `(batch_id, candidate_key)` ngăn retry/reload tạo nhiều phiếu; re-run extraction thay candidate revision, giữ quyết định review hoặc báo conflict. Gộp/tách đã duyệt có audit, không reuse code làm người nhận nhầm.

Trạng thái nghiệp vụ: `new → triaged → in_progress → waiting_customer | waiting_internal → resolved → closed`, reopen theo thao tác được quyền. `draft/accepted/rejected` là trạng thái candidate, không task business. Assignee/priority/committed due date chỉ người được quyền xác nhận; deadline được công khai phải có explicit publish permission.

## 6. Một báo cáo gộp, không auto-chat

Bản báo cáo customer-safe sinh từ **request rows/revisions đã lưu**, không từ một LLM tự kể đã tạo bao nhiêu phiếu. Body whitelist: mã public, tiêu đề đã duyệt, trạng thái public, thiếu thông tin đã chọn, thời hạn đã được IT xác nhận và cho phép công khai. Loại internal notes, debug/stacktrace/secrets/PII không cần, user IDs nội bộ, private console URLs, thông tin nhóm khác. Không dùng raw request object serialize rồi hy vọng redactor xóa đủ.

MVP dùng template deterministic, có thể cho reviewer sửa phần public trước khi ký digest; không cần lần gọi AI nữa để viết báo cáo. Nội dung “đã ghi nhận” chỉ dùng sau commit CustomerRequest; “đang xử lý/đã xong” theo business state được xác nhận, không theo worker completed.

**Một batch/group → một OutboundReport logical → một tin text.** Body vượt khả năng connector đã kiểm thì tạo lại bản gộp ngắn để review hoặc giữ blocked. Không tự chia thành nhiều tin để phá yêu cầu một báo cáo; nếu rút bớt item phải ghi tổng số và số chưa liệt kê, reviewer nhìn thấy, không cắt im lặng. Ban đầu batch nhỏ (config bound) giúp một tin đủ đọc. Không gửi raw file/đường dẫn public chứa dữ liệu khách để né giới hạn.

Approval gắn `batch_id + digest_revision + body_hash + fixed destination + request_revisions + routing/ACL/source/deletion epochs + expires_at`. Recheck người duyệt còn quyền và nhóm/account còn được cấp phép ngay trước send. Nội dung/note version/nguồn/route thay đổi thì approval cũ hết hiệu lực; không reuse chữ ký approval với body mới.

Sender claim lease/fencing và kiểm kill switch với quyền `send_digest`, không dùng quyền `auto_reply`. Không bật consumer auto-chat cũ cho module này. Member join/remove hoặc binding change cần refresh audience policy; nhóm nhiều công ty hoặc dữ liệu nhạy cảm không được auto mở scope.

Self messages của hệ thống correlate theo outbox/provider IDs: lưu transport status, không vào yêu cầu mới và không kích vòng báo cáo. Self message từ nhân viên/cùng account nhưng không map được: giữ như human activity, invalidate pending preview để review nếu ảnh hưởng đợt; không discard toàn bộ isSelf, không coi đó là customer identity.

## 7. Outbox, retry và sự cố

Outbound states: `draft → awaiting_approval → approved → queued → dispatching → accepted` và `delivered/read` chỉ khi có evidence, hoặc `blocked/failed/unknown/canceled`.

Stable key `(group_binding_id, batch_id, report_kind)` cho logical report; body revision không tự tạo quyền gửi lần hai. Sender timeout sau có thể provider nhận → unknown; lưu attempt, giữ nhóm report đó blocked, đối soát bằng ID/status/nhân viên. Không blind retry, không bảo đảm exactly-once end-to-end. Sau acceptance không gọi lại send khi consumer redelivery; trả trạng thái đã biết. Worker success không provider acceptance; HTTP timeout không chắc thất bại.

Internal queue publishing cần transaction outbox + publisher confirmation/consumer idempotency theo pattern repo; không ACK rồi mới lưu tin. DB/broker down: spool có cap/expiry/metrics; đầy thì degraded và cảnh báo, không báo healthy. Listener offline có thể mất tin trước spool, phải log coverage gap; không dùng vòng reconnect tranh phiên web với owner.

Kill switch global/binding/report route; dừng outgoing trước, giữ ingest hợp lệ nếu an toàn. No-report-send default nếu phiên/account/group grant không rõ. Tin đã tới provider trước pause có thể không thu hồi được. Raw/candidate/request evidence tuân retention owner duyệt; deletion/revoke chặn đọc và pending sends ngay, purge derivatives/cache/spool/jobs, restore áp tombstones trước reconnect. Report đã gửi vào nhóm không thể được bảo đảm xóa khỏi thiết bị người nhận.

## 8. API đề xuất (chưa triển khai)

| Endpoint family | Quyền / side effect |
|---|---|
| POST /internal/group-connectors/{binding}/events; /heartbeat | Service auth, nonce/epoch, scope mapping, durable ACK; không browser token |
| POST /api/support/group-bindings; PATCH /{id} | Admin/enrollment/purpose, optimistic concurrency; listen và send riêng |
| GET /api/support/groups/{id}/messages; /coverage | Fresh group ACL, bounded cursor, no-store, opaque refs |
| POST /api/support/groups/{id}/summary-batches | Authorized staff, Idempotency-Key, frozen new-message manifest |
| GET /api/support/summary-batches/{id}; POST /{id}/review | Read no side effect; review scoped facts/merge/split CAS |
| GET /api/support/requests; GET/PATCH /{id}; POST /{id}/notes | Group/team/user ACL, public/internal fields tách, audit; không private owner-task archive shortcut |
| POST /api/support/summary-batches/{id}/digest-preview | Deterministic body từ persisted request revisions |
| POST /api/support/digests/{id}/approve-and-queue | Exact preview/route/hash+expiry, one logical intent; no arbitrary target |
| GET /api/support/digests/{id}/delivery; POST /{id}/reconcile | Status read-only; reconcile quyền riêng, không đồng nghĩa resend |

GI-02 phải tạo OpenAPI/DTO validation phù hợp convention Core/BFF hiện có; paths trên là proposal, không endpoint đã chạy. Cùng idempotency key khác body trả conflict. GET/reload/sign-in không gọi lại POST. Fresh authorization ở cả lúc bắt đầu và trước trả response/dispatch để chống revoke race; bounded payload/cursor/CSRF/no-store theo repo. Session/binding creds không trong browser/model/log. Không cần phơi task-note API công khai cho khách ở MVP.
