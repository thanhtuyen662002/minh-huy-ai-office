# MVP ưu tiên — Nhóm khách → ghi nhận yêu cầu cho IT → một báo cáo về nhóm

**Yêu cầu chủ dự án, 2026-10-09. Epic #275, triển khai theo #276 → #277 → #278 → #279.** Đây là kế hoạch tích hợp vào AI Office, chưa phải chức năng đã chạy. Không đóng epic chỉ vì tài liệu đã merge.

## 1. Đích sử dụng đầu tiên

Không xây chatbot trả lời mọi tin ngay. Bản đầu làm được:

**Đọc tin mới trong các nhóm Zalo khách đã cho phép → tổng hợp thành yêu cầu/ghi chú có nguồn → IT xem, nhận việc và cập nhật trạng thái → người phụ trách duyệt một báo cáo gộp → gửi về đúng nhóm gốc.**

Zalo dùng tài khoản cá nhân qua connector riêng, không OA và không dot điều khiển giao diện web. Giữ ASP.NET Core/C#, SQL Server, RabbitMQ, AI Gateway/Context Engine/worker, Next.js và cơ chế quyền hiện có. Node.js chỉ dành cho bridge nếu connector yêu cầu; không thêm một backend Fastify/PostgreSQL/BullMQ cạnh tranh.

Ý nghĩa “một báo cáo vào nhóm” trong MVP: **một tin tổng hợp cho một nhóm, trong một đợt đã duyệt**, không một tin cho mỗi yêu cầu và không gom dữ liệu nhiều khách vào một nhóm chung. Mặc định đích là nhóm khách đã phát sinh yêu cầu. Báo cáo nội bộ cho nhóm IT khác là cấu hình riêng ở giai đoạn sau, không tự suy đích nhận.

## 2. Thứ tự ưu tiên

| Mức | Phạm vi | Giá trị / điểm dừng nghiệm thu |
|---|---|---|
| U0 | Connector + inbox chỉ đọc | Thấy tin nhóm đúng phạm vi, ai nói, lúc nào, nguồn nào; hiện rõ khi mất đồng bộ. Chưa gửi gì. |
| U1 | Tổng hợp + task note IT | Một yêu cầu gộp nhiều tin, có bằng chứng; IT duyệt, sửa, gộp/tách, nhận việc và theo dõi. Đã có giá trị nội bộ dù sender chưa sẵn sàng. |
| U2 — MVP tối thiểu hoàn chỉnh | Một báo cáo có duyệt về nhóm gốc | Bản công khai an toàn, không gửi nhầm/trùng logical report; có trạng thái gửi, dừng và đối soát lỗi. |
| Sau U2 | Bộ não sâu hơn và trả lời khách | Wiki có duyệt, tra cứu kiến thức, bản nháp trả lời, rồi mới cân nhắc auto-reply ít rủi ro. |

**Không chờ** toàn bộ AI Office, bộ cài Windows, billing, ERP business adapter, kho vector hoặc wiki hoàn chỉnh. Nhưng phải có quyền tenant/company/group, lưu trữ bền vững, pipeline AI giới hạn và kiểm soát gửi trước khi dùng thật. Các lỗi bảo mật/khôi phục đang có lease phải hoàn tất an toàn; không chiếm nhánh #274 hay #239. Lead ưu tiên track này cho năng lực tiếp theo không tranh ownership; các phần AI Office còn lại vẫn giữ backlog #233.

## 3. Các luồng sử dụng

### F1 — Kết nối và chọn nhóm

Owner tự đăng nhập QR trên màn quản trị được bảo vệ; chọn nhóm bằng ID và tên để đối chiếu. Gán nhóm với tenant/company, khách hàng/dự án và đội IT tiếp nhận do người có quyền quyết định. Bật quyền đọc trước; quyền gửi báo cáo riêng và mặc định tắt. Ghi căn cứ cho phép xử lý dữ liệu và thông báo phù hợp cho nhóm. Không tự nhập toàn danh bạ, nhóm gia đình hoặc lịch sử cũ.

**Đọc tất cả tin text mới được phép trong nhóm**, không đợi @mention: mục tiêu là không bỏ sót yêu cầu. Điều này khác trigger auto-reply chỉ khi được gọi ở giai đoạn sau. Quyền đọc không đồng nghĩa quyền gửi.

### F2 — Nhận và lưu tin

Bridge lọc nhóm trước persist/spool/model; nhận account ID, group ID, sender ID, message ID, text/metadata và thời gian. Internal ingest xác thực bridge rồi backend ánh xạ scope, chống trùng và lưu SQL; công việc vào RabbitMQ sau durable commit. UI hiển thị luồng tin và khoảng mất kết nối. Không hứa nhận đủ lịch sử khi connector offline.

### F3 — Tổng hợp thành yêu cầu nháp

Nhân viên bấm **Tổng hợp tin mới**. Backend đóng băng danh sách tin/revision của đợt chưa xử lý, gọi worker/AI để phát hiện yêu cầu, gom các tin cùng vấn đề, tách các vấn đề độc lập và chỉ rõ thông tin thiếu. Mỗi tin có phân loại cuối: evidence của yêu cầu, ngữ cảnh, không actionable, hoặc cần người xem. Không vứt phần vượt token limit.

MVP không bắt buộc cron: tổng hợp và gửi do người thao tác. Tự tạo bản nháp khi nhóm yên một khoảng hoặc theo giờ là khả năng sau, cadence do owner cấu hình. **Không lập lịch hoặc tự gửi trong lần viết kế hoạch này.** Không có yêu cầu mới thì không mặc định đăng “không có gì” làm phiền nhóm.

### F4 — IT tiếp nhận task note

Người phụ trách xem nguồn, sửa tiêu đề/nội dung, gộp/tách nháp rồi bấm **Ghi nhận yêu cầu**. Lúc này tạo `CustomerRequest` bền vững có mã tham chiếu, không tạo issue GitHub chứa dữ liệu khách. IT có bảng yêu cầu theo nhóm/khách, loại, trạng thái, người phụ trách; mở tin nguồn, thêm ghi chú nội bộ, nhận việc, đánh dấu cần thêm thông tin hoặc hoàn thành theo quyền.

Task note là **phiếu nghiệp vụ**, không phải task suy luận AI. Worker chạy xong không được tự đánh dấu yêu cầu khách đã xử lý xong. Không khởi chạy IT coding agent, sửa production hay ghi ERP từ nội dung yêu cầu.

### F5 — Duyệt và gửi một báo cáo gộp

Từ các yêu cầu đã lưu, backend tạo bản công khai theo mẫu. Người duyệt thấy chính xác tên/ID nhóm đích, khoảng tin đã tổng hợp, các mã yêu cầu, nội dung gửi và cảnh báo thiếu dữ liệu. Bấm **Duyệt và gửi** chỉ cấp quyền cho đúng body/destination/version đó. Sender đọc đích từ batch binding trong DB, không từ AI hoặc tên nhóm.

Mẫu chỉ nêu yêu cầu đã ghi nhận, trạng thái và điều cần làm rõ. Không tự chẩn đoán nguyên nhân, hướng dẫn khách sửa hệ thống, hứa deadline, công khai ghi chú IT hoặc nói “đã sửa” khi chưa có xác nhận. Đây là **báo cáo tiếp nhận**, chưa phải luồng trả lời nghiệp vụ từng tin.

### F6 — Bổ sung, sửa và xử lý lại

Khách bổ sung vào vấn đề cũ: đề xuất cập nhật phiếu cùng nhóm với bằng chứng; không tạo trùng tự động. Tin mới đến sau batch cutoff vào đợt tiếp theo. Nguồn bị sửa/thu hồi: đánh dấu các nháp/report phụ thuộc hết hiệu lực, yêu cầu review; phiếu đã nhận có lịch sử sửa chứ không bị AI âm thầm xóa. Báo cáo đã gửi không thể coi là tự thu hồi; đính chính cần duyệt riêng.

## 4. Chức năng bắt buộc của màn hình

| Màn hình | Hành động MVP |
|---|---|
| Kết nối/nhóm | QR owner-only, trạng thái kết nối/gap, chọn nhóm, quyền đọc/gửi, tạm dừng |
| Inbox nhóm | Tin có người gửi/thời gian/nguồn; báo nội dung chưa đọc được; chọn Tổng hợp tin mới |
| Nháp yêu cầu | Xem evidence, sửa, gộp/tách, loại tin xã giao, xác nhận thông tin thiếu |
| IT task notes | Mã yêu cầu, khách/nhóm, tiêu đề, loại, nguồn, người nhận, trạng thái, ghi chú và audit |
| Báo cáo nhóm | Preview riêng không có internal notes, duyệt gửi, trạng thái pending/accepted/unknown/failed; không đọc lại là gửi lại |

Không làm graph UI đẹp, tự phân công theo suy đoán, SLA tự cam kết, toàn bộ Kanban SaaS hay export nhiều hệ thống trước các chức năng trên.

## 5. Ví dụ đầu ra (dữ liệu giả)

Tin khách: “Báo cáo tồn kho không mở được”; “Chi nhánh A, từ sáng nay”; “Thêm cột mã lô vào xuất Excel giúp bên em”; “Mong xem trước 15h”.

IT nhận hai phiếu:

- **YC-DEMO-001:** Khách báo không mở được báo cáo tồn kho; chi nhánh A; cần log/ảnh lỗi. Trạng thái Mới. Deadline khách mong muốn 15h chỉ gắn đúng yêu cầu nếu nguồn xác định được; chưa có deadline IT cam kết.
- **YC-DEMO-002:** Đề nghị thêm cột mã lô vào file Excel; cần xác định mẫu export. Trạng thái Cần làm rõ. Hai phiếu có message refs riêng, không đoán quan hệ của câu “trước 15h”.

Một báo cáo gộp về đúng nhóm có thể là:

> Tổng hợp yêu cầu đã ghi nhận — đợt DEMO-01\n1. YC-DEMO-001: Khách báo không mở được báo cáo tồn kho ở chi nhánh A. Trạng thái: Mới.\n2. YC-DEMO-002: Đề nghị bổ sung cột mã lô vào file Excel. Trạng thái: Cần làm rõ mẫu export.\nThông tin còn thiếu: ảnh/log lỗi, mẫu Excel và yêu cầu nào cần trước 15h.\nChưa có thời hạn xử lý được IT xác nhận.

Mẫu đầy đủ và kết quả kỳ vọng ở [examples/golden-batch.json](examples/golden-batch.json). Không gửi mã nội bộ chứa đường dẫn riêng tư hoặc nội dung debug nhạy cảm vào nhóm.

## 6. Bộ não thứ hai ở MVP này

Lớp tối thiểu gồm: **tin gốc có nguồn, các yêu cầu đã ghi nhận, lịch sử cập nhật đã xác nhận, glossary/module/dự án của khách và template báo cáo được duyệt**. Đây đã là bộ nhớ làm việc hữu ích, không cần nạp mọi tài liệu vào vector mới tổng hợp được.

Tri thức wiki dùng để nhận đúng thuật ngữ/nhóm sản phẩm, không dùng để tự bổ sung một yêu cầu khách chưa nói. Hội thoại và task notes riêng lưu theo quyền nhóm trong SQL; không biến thành shared wiki. Sau khi U2 dùng ổn, thêm raw→wiki→review→publish/RAG vào Context Engine, rồi mới làm trả lời khách. Không bê nguyên kiến trúc 1:1/stack của repo AI-Assistants sang đây.

## 7. Bắt đầu triển khai

Đọc [REPO_MAPPING.md](REPO_MAPPING.md), [CONTRACTS_AND_FLOWS.md](CONTRACTS_AND_FLOWS.md), [DELIVERY_AND_ACCEPTANCE.md](DELIVERY_AND_ACCEPTANCE.md). GitHub issues #276–#279 là nguồn trạng thái triển khai; [backlog.json](backlog.json) chỉ phân rã phụ thuộc/phạm vi, không thay lease issue/PR. Mỗi implementation issue có branch/Draft PR và HANDOFF theo AGENTS.md hiện hành.

Không có login, gửi tin thật, migration, deploy hoặc automation nào được thực hiện bởi bộ tài liệu này. Mọi ngưỡng/timeline là mục tiêu/ước lượng cần kiểm chứng, không kết quả đã đạt.
