# Hiện trạng và điểm tích hợp đã đối chiếu

Baseline đã đọc: `420db0672fb4b61f28f7ad0de5e63d3bd2ca97ca` của `thanhtuyen662002/minh-huy-ai-office`, 2026-10-09. Không chạy application hoặc suy trạng thái production từ việc có class/contract. Đọc lại main/PR lúc coding vì repo vẫn đang phát triển.

## Code và tài liệu đã kiểm

| Đường dẫn / evidence | Điều có thật | Cách tận dụng / giới hạn |
|---|---|---|
| `docs/architecture/FOUNDATION.md` | C#/ASP.NET Core, SQL Server, RabbitMQ, Redis, Next.js, AI Gateway, context, audit | Giữ nguyên stack và quyền hiện có; không thêm backend song song |
| `src/Shared.Contracts/ConversationIngestion.cs` | Manifest có Tenant/Company/Task/Conversation, sequence UTC, attachment ownership | Tái dùng validation khi đã dựng batch job; chưa có sender/group/account trong shape. Không ép mọi tin vừa vào phải có AI task hoặc lấy group ID làm UserId |
| `src/Shared.Contracts/CustomerChatIngress.cs` | Trusted user authority/version, persist trước dispatch, replay checks | Là contract theo user/conversation, không chứng minh quyền của nhóm nhiều người. Thêm group service authority riêng có grant, không fake portal user |
| `src/Shared.Contracts/AiContextEngine.cs` | Các source kinds, token budget, durable manifest checkpoint | Nạp selected group messages + glossary + scoped existing requests; manifest checkpoint không lưu nội dung nguồn nên cần own protected source store |
| `src/Agent.Worker/WorkExecutionServiceCollectionExtensions.cs` | Durable RabbitMQ pipeline qua authorization/audit decorator | Thêm bounded group-summary executor/capability; không resolve raw executor ngoài gate. Không buộc job tóm tắt có ERP datasource giả |
| `src/Agent.Worker/` tree | Worker, RabbitMQ transport, persistent handler, AI question/adapters | Integration points có thật; chưa chứng minh group summarizer hay report sender tồn tại |
| README; PR #271/main snapshot | Private owner-scoped task history/recovery đã có checkpoint main | Dùng pattern durable state; không mở lịch sử task cá nhân cho mọi thành viên nhóm. CustomerRequest là domain record riêng |
| `docs/ROADMAP.md`, `AGENTS.md`, `RESUME_PROTOCOL.md`, `PARALLEL_EXECUTION.md` | Governance, task state durable, one-issue/Draft-PR, owner của state/migrations | Đây là track ưu tiên mới do owner yêu cầu; không hủy scope #233 hoặc cướp lease đang chạy |

## Những phần phải xây, không được nói đã có

Group binding/connector auth; receipt/revision/coverage store; batch snapshot; extraction schema và kiểm evidence; task-note lifecycle/team ACL; safe digest projection/approval; outbound report intent/reconcile; UI nhóm/nháp/IT/report; kiểm thử thật. Có contract P1/P2 không có nghĩa UI/integration/production đã pass.

Mở PR tại thời điểm đọc: **#274** cho task submission/recovery, **#239** Windows installer. Không sửa/cherry-pick các phần chưa được chấp nhận, không đổi `docs/PROJECT_STATE.yaml` hoặc workstream đang leased. PR kế hoạch này thêm docs và đường dẫn roadmap; Lead tích hợp thay đổi ưu tiên sau review. Khi cần shared task API/migration từ #274 thì sequence sau accepted merge hoặc stacked PR được owner stream đồng ý; connector spike/fixtures có thể làm độc lập.

## Nguồn ngoài

- https://github.com/RFS-ADRENO/zca-js — upstream mô tả personal unofficial API, group receive/send, QR; cảnh báo account locked/banned và chỉ một web listener/account.
- https://zca-js.tdung.com/en/listeners/message — `threadId`, `ThreadType.Group`, `isSelf`. `isSelf` không tự phân biệt bot với nhân viên.
- https://zca-js.tdung.com/en/apis/sendMessage — sender nhận thread ID/type; phải pin version và kiểm controlled group, không coi code mẫu là bằng chứng account đã tương thích.

Đã đọc lại nguồn ngày 2026-10-09. Chưa xác minh trên tài khoản người dùng, chưa chốt quota/giới hạn độ dài/khả năng history/edit/receipt của từng version. Spike ghi supported/unsupported/unverified. Không có chính sách OA 48h/7d trong personal flow. Không có cam kết tránh khóa dù có rate limit/human approval; không vượt CAPTCHA hoặc đổi proxy/account né chặn.

## Phần học từ video / plan trước

Giữ nguyên ý nguồn gốc tách ghi chú biên soạn, liên kết và review; áp dụng trước cho request evidence và task history. Không khẳng định connector/backend cụ thể xuất hiện trong video. Không tải video/screenshot/chat khách vào repo public. Full wiki/retrieval phục vụ answer là giai đoạn sau, không prerequisite thu thập và tổng hợp yêu cầu.
