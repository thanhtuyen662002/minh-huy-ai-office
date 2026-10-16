# Nhóm khách → tự ghi phiếu DB → tự báo nhóm Kỹ thuật

**Yêu cầu hiện hành v2, 2026-10-10: KHÔNG có nhân viên duyệt, KHÔNG gửi báo cáo về nhóm khách.** Đọc [đính chính của owner](OWNER_CORRECTION_AUTO_IT.md) trước. Bản reviewed same-group digest từ PR #280 là hiểu sai đã được thay thế.

Luồng: các nhóm khách được cấu hình → connector → Agent tiếp nhận/bộ não đúng nhóm → tự lưu yêu cầu/ghi chú vào SQL → event sau commit → Agent báo việc đọc DB → tự gửi báo cáo vào nhóm Kỹ thuật nội bộ đã cấu hình.

IT nhận thông báo để xử lý, không cần duyệt đầu vào. Việc thiếu thông tin vẫn được ghi và báo IT với nhãn cần làm rõ; không nằm im ở hàng chờ phê duyệt. Thiết lập quyền/route ban đầu và code review/CI không phải approval từng tin.

## Triển khai ngay trong AI Office

#276 chốt connector + source-to-IT route/service contracts. #277 lưu tin/coverage. #278 automatic batching + bộ não + tự tạo phiếu/attention + NotesCommitted. #279 agent đọc DB + auto IT-only notification + recovery/pilot. Full wiki và trả lời khách làm sau.

Nguồn/đích là hai loại binding khác nhau. Một nhóm IT có thể nhận từ nhiều nhóm khách được grant; từng item phải rõ nguồn. AI không chọn nhóm nhận. Không có route thì lưu phiếu và báo lỗi, không gửi về nhóm nguồn. Giữ source/customer boundaries và tách dữ liệu không được phép khỏi notification.

## Tài liệu

- [OWNER_CORRECTION_AUTO_IT.md](OWNER_CORRECTION_AUTO_IT.md): quyết định sản phẩm và luồng chính.
- [CODEX_PROMPT.md](CODEX_PROMPT.md): prompt triển khai code thay prompt cũ.
- [CONTRACTS_AND_FLOWS.md](CONTRACTS_AND_FLOWS.md): data/state/event boundaries.
- [DELIVERY_AND_ACCEPTANCE.md](DELIVERY_AND_ACCEPTANCE.md): issue sequencing và gate no-click.
- [backlog.json](backlog.json), [golden batch](examples/golden-batch.json), [cases](acceptance-cases.jsonl): v2, không yêu cầu approval.
- [VALIDATION.md](VALIDATION.md): kết quả kiểm tra planning assets, không application tests.

Repo dùng C#/SQL Server/RabbitMQ/Next.js và gateway/context/worker hiện có; Node connector nếu cần. Không bật session, gửi tin hoặc deploy khi cập nhật kế hoạch. Tất cả live capabilities vẫn cần evidence thực.
