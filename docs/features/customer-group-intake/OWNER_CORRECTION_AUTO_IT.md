# Owner correction v2 — tự ghi việc, tự báo nhóm Kỹ thuật

Ngày: 2026-10-10. Epic #275, issues #276–#279 giữ ID. Chủ dự án đính chính: **không có nhân viên duyệt; tin các nhóm khách → connector → agent → DB → agent → nhóm Kỹ thuật báo việc cho IT**.

Đây thay thế các yêu cầu sai trong PR #280/bản prompt cũ: manual Generate summary, duyệt thành phiếu, approve-and-send và gửi về nhóm khách gốc. PR #280 đã merge; không sửa lịch sử hoặc coi tài liệu cũ là yêu cầu hiện hành. Code review/CI/quyền thiết lập ban đầu vẫn giữ; chúng không phải runtime approval.

## Luồng chính

```text
Nhóm khách A/B/... được cho phép
  → connector lọc nhóm nguồn
  → Core.Api xác thực và lưu tin/revision bền vững
  → tự đóng batch từ sự kiện tin mới
  → Agent tiếp nhận + bộ não theo từng nhóm
  → code kiểm nguồn/quyền → tự commit phiếu/ghi chú vào SQL
  → NotesCommitted event qua outbox/RabbitMQ
  → Agent báo việc đọc lại các phiếu đã commit
  → tạo report theo route nội bộ được cấp quyền
  → tự gửi vào NHÓM KỸ THUẬT, không có bước bấm duyệt
```

Agent tiếp nhận và Agent báo việc là hai giai đoạn có checkpoint; có thể dùng cùng worker/platform/model adapter. Bước báo việc có thể dùng template deterministic, không cần thêm model call. SQL là nguồn trạng thái; không gửi báo “đã ghi nhận” từ kết quả LLM khi DB chưa commit.

## Quyết định bắt buộc

1. **Nguồn khác đích:** SourceGroupBinding → TechnicalDestinationBinding/RouteVersion/AllowedScope. Backend giữ mapping; AI không chọn destination. Thiếu route thì giữ phiếu và báo lỗi cấu hình, không fallback về nhóm nguồn. Nhóm Kỹ thuật không vào intake mặc định, tránh echo loop.
2. **Không runtime human approval:** tự tạo note và tự enqueue report sau validation. Không có `awaiting_approval`, nút approve, candidate acceptance do nhân viên, hoặc manual trigger là điều kiện chạy bình thường. IT nhận việc rồi mới cập nhật/sửa/gộp/tách khi cần.
3. **Thông tin thiếu không bị bỏ:** tự tạo attention note `needs_clarification` hoặc `extraction_failed` có evidence và báo IT. Đây là nội dung công việc cần IT xem, không là trạng thái chờ duyệt để được thông báo. Quyền/route không hợp lệ vẫn block an toàn, không dùng cờ thiếu thông tin để lách quyền.
4. **Tự động theo sự kiện:** debounce có checkpoint SQL, giới hạn chờ tối đa chống starvation khi nhóm nhắn liên tục. Giá trị thử đề xuất 30 giây yên lặng hoặc 120 giây từ tin đầu, mốc đến trước; là tuning config chưa chốt, không lịch ChatGPT và không kích hoạt trong phiên lập tài liệu.
5. **Nhiều nhóm khách → một nhóm IT có grant:** mỗi nguồn xử lý và lưu nhớ riêng, mỗi item report ghi nhóm/khách/mã phiếu/nguồn. Renderer chỉ ghép các packet mà đích có quyền xem. Không tự merge hồ sơ/phiếu khác nhóm; mặc định không ghép cross-tenant. Cross-company cần grant nguồn-đích tường minh và thiết kế được review, không suy từ tên nhóm IT.
6. **Status thật:** `new/open`, `needs_clarification`, nguồn `ai_extracted/customer_reported`; không tự thành `human_confirmed`, `resolved`, assignee hay deadline IT cam kết. Worker completed không công việc completed. Mong muốn của khách giữ riêng.
7. **Gửi từ dữ liệu đã lưu:** report đọc request revisions đã commit, không toàn object nhạy cảm. Chỉ fields phù hợp audience, mask secrets, refs/link nội bộ có auth theo policy. Nhóm IT nội bộ không được mặc định có quyền đọc mọi dữ liệu.
8. **Kiểm bằng code, không ký duyệt người:** sender kiểm route/source/ACL/deletion versions, binding health, idempotency, cost/size cap và kill switch. Stale body tự rebuild/revalidate; revoked access block. Unknown send giữ unknown/reconcile, không blind retry. Không cam kết exactly-once đến provider.
9. **Không trả khách trong MVP:** feature không có outbound tới customer_source. Báo tiến độ/trả lời khách tự động là scope sau, cần yêu cầu riêng. Không tự sửa ERP/production hoặc khởi chạy coding agent từ tin khách.
10. **Giữ nền AI Office:** C#/ASP.NET Core, SQL Server, RabbitMQ, AI Gateway/Context Engine/authorized worker, Next.js; Node bridge riêng nếu cần. Không dựng backend thứ hai. Full wiki/vector/ERP/Windows setup không chặn bộ não tối thiểu: nguồn tin + phiếu cũ + lịch sử có provenance + glossary được cấp quyền.

## Các thay đổi hợp đồng

Thêm GroupNotificationRoute và TechnicalDestinationGrant. Task-note transaction ghi Request/Evidence/Revision + NotesCommitted outbox. Bước báo việc ghi NotificationBatch/NotificationItem/OutboundTechnicalReport gồm snapshot request revisions, route/grant version, content hash, stable idempotency và send status. Không dùng DigestApproval như prerequisite trong flow này.

Nguồn tin, per-group committed sequence, exact frozen batch, late-arrival delta, revision/invalidation, spoofed service auth, least privilege, bounded context và recovery vẫn bắt buộc. Mỗi tin có disposition; quá token limit thì chunk đủ coverage. Listener gap/media chưa hiểu xuất hiện trong báo cáo IT dưới dạng phạm vi/thông tin chưa đủ, không giả đầy đủ và không chờ nhân viên bấm để báo.

Automatic trigger không đồng nghĩa auto-enroll account hoặc gửi ngay từ phiên viết tài liệu. Owner cấu hình tài khoản/nguồn/đích, xử lý dữ liệu, cấp quyền và bật feature một lần khi triển khai. Sau đó đường bình thường chạy không có người duyệt. Kiểm chứng connector thật và code release gates vẫn cần; không giả account test đã thực hiện.

## Phiên bản tài liệu và nghiệm thu

Đọc CODEX_PROMPT.md cho lệnh triển khai chi tiết. README/CONTRACTS_AND_FLOWS/DELIVERY_AND_ACCEPTANCE/backlog và fixtures cùng feature đã được điều chỉnh theo v2. Bản cũ còn trong lịch sử Git, không được dùng fixture bắt buộc approve/same-group để chặn yêu cầu mới.

Test chốt: sau setup mock không có thao tác người nào; tự nhận tin, tự có notes SQL, tự báo nhóm IT; số outbound về nhóm khách bằng 0. Source/destination được phân biệt cả khi tên trùng. DB rollback không sinh success report; replay/restart không nhân bản; mơ hồ/model failure sinh attention gửi IT; thiếu route không fallback. QA/reviewer kiểm thử trước release không phải nhân viên duyệt từng việc lúc vận hành.

Mới sửa kế hoạch/prompt/fixtures, không có runtime code, login tài khoản hoặc gửi tin thật trong thay đổi này.
