# Codex — AI Office: tự ghi nhận yêu cầu và tự báo nhóm Kỹ thuật

**Đính chính yêu cầu của chủ dự án — 2026-10-10.** Prompt này thay thế prompt yêu cầu người duyệt và gửi báo cáo về nhóm khách. Không kết hợp lại các bước sai đó.

Repo: `thanhtuyen662002/minh-huy-ai-office`.

## 1. Mục tiêu bắt buộc

Triển khai CODE cho luồng tự động sau:

```text
Tin nhắn mới trong các nhóm khách được cho phép
→ connector
→ agent tiếp nhận/tổng hợp + bộ não đúng nhóm
→ tự ghi phiếu công việc/ghi chú vào SQL Server
→ event sau commit
→ agent báo việc đọc các phiếu đã lưu
→ tự gửi báo cáo vào NHÓM KỸ THUẬT nội bộ đã cấu hình.
```

Không có người duyệt trước khi tạo phiếu. Không có người duyệt trước khi gửi báo việc. Không yêu cầu bấm “Tổng hợp”, “Ghi nhận”, “Duyệt” hay “Gửi” để luồng bình thường chạy. KHÔNG gửi báo cáo về nhóm khách. Chưa triển khai trả lời khách tự động.

IT là bên nhận việc để xử lý; IT không phải người phê duyệt đầu vào. IT có thể nhận việc, sửa/gộp/tách phiếu hoặc cập nhật tiến độ SAU khi hệ thống đã tự ghi nhận và thông báo. Thiếu thông tin phải tạo ghi chú cần làm rõ và báo IT, không làm cả batch nằm chờ duyệt.

Hai “agent” là hai bước của workflow có checkpoint; không bắt buộc hai server, hai nhà cung cấp model hoặc hai chatbot độc lập. Agent báo việc có thể dùng template deterministic để giảm chi phí và tránh bịa trạng thái.

## 2. Kiểm repo trước khi sửa

Đọc AGENTS.md, README, PROJECT_STATE, FOUNDATION, ROADMAP, RESUME_PROTOCOL, workstream đang nhận; kiểm main, issues và PR mới nhất. Không reset về commit được nêu trong lịch sử chat.

Epic #275 và issues #276–#279 giữ ID nhưng acceptance phải phản ánh đính chính này. PR #280 chứa bản kế hoạch hiểu sai đã merge ở mốc được kiểm tra; tìm PR đính chính mới và `docs/features/customer-group-intake/OWNER_CORRECTION_AUTO_IT.md`. Không dùng mô tả lịch sử “reviewed same-group digest” làm yêu cầu hiện hành.

Respect lease đang có, đặc biệt các phần recovery/security/installer nếu còn hoạt động. Một implementation issue một branch/Draft PR; không tự merge hoặc chiếm nhánh khác. Code review, CI và quyền quản trị cấu hình vẫn giữ; chúng KHÔNG phải bước người duyệt từng tin trong sản phẩm.

## 3. Giữ stack, tích hợp bộ não thật

Giữ ASP.NET Core/C#, SQL Server, RabbitMQ, Redis, Next.js, AI Gateway, Context Engine và worker có authorization/audit của repo. Node.js chỉ làm bridge nếu connector cần. Không thêm Fastify/PostgreSQL/BullMQ backend thứ hai.

Đọc code thực ở Shared.Contracts/ConversationIngestion.cs, CustomerChatIngress.cs, AiContextEngine.cs, Agent.Worker/WorkExecutionServiceCollectionExtensions.cs và các store/API/BFF liên quan. Chỉ tái dùng khi contract phù hợp, không fake UserId/ERP datasource để đi qua gate.

Bộ não MVP phải có SQL stores, migrations, DI, worker call sites và tests, không chỉ thư mục Markdown:
- Tin nguồn/revision, sender, reply-to và khoảng coverage.
- Phiếu đã ghi nhận, thông tin thiếu, lịch sử bổ sung của đúng nhóm.
- Glossary/module/dự án được cấp quyền nếu có.
- Context builder giới hạn tokens, có source refs/version và scope.

Context checkpoint metadata không thay nội dung nguồn: persist nguồn bảo vệ rồi resolve đúng revision. Không đọc owner task archive, DM hay nhóm khác để bù trí nhớ. Full wiki/vector/ERP/Windows installer không chặn MVP này.

## 4. Tách nhóm NGUỒN và nhóm ĐÍCH

Thêm mapping do backend quản lý:
`SourceGroupBinding → TechnicalDestinationBinding + RouteVersion + AllowedScope`.

Nhóm khách chỉ là nguồn đọc trong MVP. Nhóm Kỹ thuật là đích báo việc, không tự trở thành nguồn intake. Mặc định chặn mọi outbound của feature về nhóm khách. Mỗi nguồn phải có route/grant rõ ràng; thiếu route thì giữ phiếu trong DB và báo lỗi cấu hình qua dashboard/log an toàn, tuyệt đối không fallback về nhóm nguồn.

Một nhóm Kỹ thuật có thể nhận việc từ nhiều nhóm khách nếu được cấp quyền cho từng nguồn. Xử lý/ghi nhớ từng nhóm riêng; chỉ ghép các packet công việc đã được kiểm quyền vào report theo đích. Mọi item luôn ghi rõ nhóm/khách nguồn và mã phiếu. Không merge phiếu giữa hai nhóm chỉ vì nội dung giống nhau.

External account/group/sender/message IDs là opaque. Không dùng tên nhóm làm khóa, không đổi case/trim làm biến dạng ID. Kiểm semantics SQL equality theo quy tắc hiện có. AI không có trường destination/tenant/role trong output. Đích lấy từ route SQL, không từ chat hoặc suy luận model.

Quyền service ingest/extract/notify riêng, không giả danh người dùng portal/admin. Nhóm nội bộ cũng phải có audience grant; không tự cho phép lộ mọi dữ liệu chỉ vì tên nhóm là “Kỹ thuật”. Cấu hình nguồn/đích/quyền ban đầu là thiết lập vận hành, không phê duyệt từng phiếu/báo cáo.

## 5. Nhận tin và kích hoạt tự động

Connector nhận mọi text mới được phép trong nhóm khách đã enroll, không cần @mention. Lọc DM/nhóm không được phép trước spool/persist/model. Giữ session mã hóa, một fenced listener/account, heartbeat, spool có giới hạn. Internal ACK sau SQL durable commit. Dispatcher/outbox chuyển job refs qua RabbitMQ.

Tự đóng batch theo event-driven debounce có checkpoint SQL. Giá trị khởi đầu ĐỀ XUẤT để thử: 30 giây không có tin mới, hoặc tối đa 120 giây từ tin đầu chưa xử lý, mốc nào đến trước. Các giá trị này là cấu hình tuning, chưa được owner chốt và không phải lịch ChatGPT. Kiểm fake clock/restart; liên tục có tin cũng không bị đói xử lý. Không dựng timer chỉ trong RAM.

Freeze exact message IDs/revisions và committed ingest cutoff. Không dùng MAX(identity) thiếu kiểm soát commit-order. Late messages vào batch delta; lịch sử backfill không tạo báo cáo ngoài ý muốn. Duplicate events không nhân phiếu.

Media/ảnh/audio chưa hiểu phải giữ metadata và tạo ghi chú cần làm rõ cho IT. Listener gap phải được ghi thật; vẫn báo IT các việc đã nhận kèm “phạm vi tin chưa đầy đủ”, không im lặng chờ người duyệt gap. Sai quyền/route không an toàn vẫn fail closed.

## 6. Agent 1 tự ghi phiếu

Phân loại actionable/request bổ sung/xã giao/echo/needs_clarification; một tin nhiều việc thì tách, nhiều tin cùng việc thì gom có evidence. Trước khi gọi model, lấy giới hạn các phiếu liên quan CÙNG nhóm; giữ nguồn hiện tại để không tạo lại phiếu chỉ từ lịch sử.

Model trả proposal có title/type/problem/outcome/source refs/missing fields/requested_deadline_text/suggested_relation. Code validate shape, nguồn, scope, quote/ý nghĩa, limits. Không có nguồn thì không tạo fact khẳng định.

Sau validation, tự commit CustomerRequest/RequestEvidence/RequestRevision và NotesCommitted outbox event. Trạng thái new/open, origin=ai_extracted; verification_level thể hiện là khách báo/AI suy ra, không IT đã xác nhận. Nội dung mơ hồ vẫn persist attention note `needs_clarification`, tự chuyển IT cùng bản báo việc. Không có draft→human_approve prerequisite.

Assignee chưa có thì unassigned/IT queue. Deadline khách mong muốn khác committed_due_at; không tự hứa. Worker completed khác việc IT resolved. Tin “đã xong” của khách không tự đóng phiếu. Tự liên kết/cập nhật khi có request code/reply mapping chắc và cùng scope; trường hợp không chắc lưu liên quan có điều kiện/cần làm rõ, không destructive merge hoặc chặn cả pipeline.

Retry/model output lỗi: bounded attempts, sau đó tự tạo attention record `extraction_failed` có refs/coverage, báo IT rằng cần xem nguồn; không làm rơi tin hoặc nói đã hiểu. Secrets trong nội dung phải mask/quarantine trước model/report theo policy, không dán raw message nhạy cảm vào cảnh báo.

Idempotency theo source-batch/candidate lineage hoặc event revision; re-run không tạo phiếu mới vô hạn. Transaction rollback không phát NotesCommitted. CustomerRequest là business record, không runtime AI task hay GitHub issue chứa tin khách.

## 7. Agent 2 đọc DB và tự báo IT

Chỉ chạy từ NotesCommitted/updates đã persist. Đọc đúng request revisions từ SQL. Không dùng trí nhớ model để nói đã tạo phiếu khi SQL chưa commit.

Tạo một report gộp mỗi dispatch window/đích, phần việc từ các nhóm được grant tách thành từng mục rõ nguồn. Nội dung: mã phiếu, khách/nhóm nguồn, vấn đề khách báo, việc IT cần xem, thông tin thiếu, urgency/deadline KHÁCH ĐỀ NGHỊ, trạng thái DB và link nội bộ có auth nếu policy cho phép. Không gửi secrets, raw credentials hoặc dữ liệu ngoài audience scope.

Không bịa nguyên nhân/giải pháp/đã sửa. Không cần thêm một lần LLM nếu template đủ. Sau kiểm tra deterministic tự lưu OutboundTechnicalReport và enqueue sender. Không approval record/approve-and-queue endpoint hoặc nút bấm là điều kiện chạy.

Send intent chốt route version, target account/group, source grants, request revision set, body digest và idempotency. Recheck quyền/route/source deletion/kill switch ngay trước send. Stale content thì tự rebuild/revalidate; quyền bị thu hồi thì block an toàn, không yêu cầu “duyệt lại” như quy trình sản phẩm.

Một logical report chỉ được claim một lần; redelivery/reload không send lại. Timeout sau có thể provider nhận thì unknown/reconcile, không blind retry. Không hứa exactly-once end-to-end.

Report quá dài thì deterministic compact vẫn ghi counts/refs và thông tin thiếu; chưa đạt giới hạn thì giữ backlog/đánh cờ lỗi vận hành, không fallback gửi vào nhóm khách, không ngầm bỏ việc. No new work/no material update thì không spam. Echo báo IT bị loại ngay ở ingress; không tạo vòng agent tự giao việc cho chính mình.

## 8. Vai trò của con người và UI

Không có người duyệt trong luồng intake→note→IT notification. UI dùng xem và điều hành: nguồn/đích, inbox/gaps, notes tự tạo, evidence, assignment/status và delivery/error/kill switch. IT có thể sửa sau khi nhận thông báo nhưng không cần hiện diện để report được gửi.

Không khởi chạy coding agent hay sửa ERP/production theo yêu cầu khách. Giải quyết công việc là bước sau dành cho IT. Không tự bật customer-auto-reply.

## 9. Kiểm thử và triển khai theo issues

#276: source/destination route + service authority + connector qualification/contracts.
#277: durable group ingress/coverage + dispatch/restart.
#278: automatic batch + brain integration + auto-persist notes/attention events.
#279: SQL-driven reporter + automatic IT-only sender + pilot/recovery.

Thiếu account/model credentials thì build bằng fake provider/connector trong môi trường test; dùng SQL/queue thật khi có môi trường. Không nói fake model/sender là live integration đã chạy.

Golden test không có bất kỳ click/approval nào sau setup:
- Hai nhóm khách khác ID, có thể cùng tên, đều có grant tới một nhóm IT.
- Một incident qua nhiều tin, một change request, một câu deadline mơ hồ.
- Timer tự đóng batch, SQL có notes/attention cùng evidence.
- NotesCommitted tự kích report; đích IT khác cả hai nhóm nguồn.
- Nhóm khách không nhận outbound. IT nhận việc có nguồn rõ.
- DB lỗi không có thông báo “đã ghi nhận”; restart/replay không tạo lại notes/report.

Tests bổ sung: route thiếu/revoked không fallback; customer text đổi destination bị chặn; source edits invalidation; media và model failure tự có attention; late-commit coverage; internal-group echo; multi-source grants; secrets masking; deletion/spool/restore; unknown sends; all model-sourced IDs checked by backend.

Unit/contract + integration SQL/RabbitMQ + production DI/call path + UI read-only tests. Các đánh giá người review test/eval là QA trước release, không runtime human approval. Tách chất lượng semantic, test deterministic, connector thật và production readiness.

## 10. Quyền vận hành và bàn giao

Thiết kế pipeline phải tự động sau khi owner cấu hình hợp lệ và bật feature. Việc viết prompt/kế hoạch không tự cấp secret, login tài khoản, đăng tin thử hoặc triển khai production ngay trong phiên coding. Không tạo ChatGPT automation hoặc bật lại agent loops.

Giữ AGENTS.md về migrations, review code, leases, CI exact-head và HANDOFF. Không bắt release code tự merge vì workflow runtime không cần người duyệt.

Triển khai code/test từng phần nhỏ, không kết thúc ở tài liệu/interfaces rỗng. Báo commit/PR, luồng đã chạy, commands thật, evidence SQL/queue/model/connector, skipped/blockers, cấu hình chạy demo và next action. Không gọi “đã tự động” khi vẫn phải bấm duyệt/gửi.

Bắt đầu bằng đối chiếu ngắn repo và issue đang sẵn sàng; sau đó sửa code trực tiếp.
