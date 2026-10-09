# Giao việc, nghiệm thu và rollout

Epic **#275** là track ưu tiên chủ dự án mới yêu cầu. Bốn issue triển khai **#276–#279** là nguồn acceptance/status/lease; [backlog.json](backlog.json) chia 15 bước, trong đó 12 bước phục vụ MVP và 3 bước sau MVP. Không có coding agent nào được khởi chạy bởi plan.

## 1. Đường triển khai ngắn nhất

| Issue | Các bước | Bằng chứng cần trước chuyển tiếp |
|---|---|---|
| #276 — contract/feasibility | GI-00 scope; GI-01 connector; GI-02 authority/contracts | Hợp đồng mock để core đi tiếp; controlled account chứng minh read trước live intake, send được kiểm riêng. External credentials thiếu không block code độc lập. |
| #277 — intake/inbox | GI-03 durable SQL/revisions/outbox; GI-04 bridge và UI chỉ đọc | Tin nhóm vào đúng scope, restart/replay không mất phần đã commit, coverage gaps hiển thị, không gửi khách. |
| #278 — IT notes | GI-05 batch; GI-06 extraction; GI-07 review/task-note board | Một nguồn nhiều tin → nháp đúng → phiếu IT có nguồn; gộp/tách, assignee, status, nội bộ/public tách riêng. |
| #279 — one digest + pilot | GI-08 safe report; GI-09 approve/send; GI-10 tests; GI-11 controlled pilot | Một report đã duyệt đúng nhóm, pending/unknown không gửi lại mù, recovery/isolation và UAT được chứng minh. |

GI-12 full wiki, GI-13 reply draft, GI-14 auto-reply có gate riêng sau. Không để các bước này thành dependency U0/U1/U2. WhatsApp và đồng bộ sang tracker ngoài chưa nằm trong MVP nhóm Zalo; thiết kế adapter mở rộng được nhưng không cần triển khai mọi channel trước dùng.

Phát triển hai luồng độc lập trước: connector qualification/fixtures và backend group-domain/contracts. Frontend dùng mock sau contract freeze; QA chuẩn bị các nhóm tình huống song song. Tối đa ba luồng coder để giảm tranh migrations/contracts; respect workstream lease hiện có. Khi shared task submission từ #274 chưa merge, không chép patch chưa review sang nhánh này.

## 2. Sẵn sàng ở từng mức

**Mock IT notes:** GI-07 và dependencies có code/tests; sample run có manifest nguồn, candidate review, stored requests. Không cần live Zalo, ERP datasource hoặc full wiki. Test structural kit không thay test application.

**Live read U0:** owner cho phép account/group, review rủi ro connector/terms/purpose/provider data processing, pin capability, nhóm được enroll, role/group ACL, persistent inbox/spool/coverage/retention có bằng chứng. Mặc định send OFF. Không gọi đây là đã hoàn thành MVP một báo cáo.

**Live notes U1:** U0 + bounded extraction, provenance, UI IT, memory/privacy guards và đánh giá chất lượng yêu cầu. IT có thể dùng phiếu trong AI Office trong khi live sender vẫn đang kiểm. Người phụ trách xem hết phần needs_review/media/gap; không để model tự bỏ sót mà báo hoàn chỉnh.

**Reviewed live MVP U2:** U1 + safe projection/approval/fixed routing, idempotent report outbox, sender capability actual evidence, kill switch, unknown-send reconcile, restore/drill và người trực. Đây mới là đích tối thiểu người dùng yêu cầu. Không bật auto-reply khi U2 pass; mode đó chưa được nghiệm thu.

## 3. Quality gates đề xuất (chưa đo)

- Tối thiểu **100 cửa sổ hội thoại tổng hợp có nhãn**, chứa nhiều người/chủ đề, sửa/phủ định/deadline mơ hồ, xã giao, lặp, tệp/ảnh, source injection và late events. Tách development/held-out; không đưa đáp án vào prompt cho test. Ít nhất **30 cửa sổ được reviewer độc lập** đối chiếu tin gốc.
- Actionable-request recall mục tiêu **≥95%**: số yêu cầu thật được nháp ghi nhận có đủ nghĩa / tổng yêu cầu thật trong gold. Missing/needs_review được đo riêng, không tính tất cả abstain là pass. Precision mục tiêu **≥90%**: nháp actionable đúng / mọi nháp actionable; báo over-split/over-merge/duplicate riêng.
- **100% request evidence refs hợp lệ cùng scope** trong kiểm tra deterministic. Đo semantic support riêng; đoạn quote có thật chưa chứng minh tóm tắt đúng. Zero invented deadline/assignee/resolution trong các case chặn phát hành. Số % này là mục tiêu test, không xác suất thành công của dự án.
- Zero cross-tenant/company/group leakage, wrong destination, send without valid approval, source-deletion resurrection hoặc unsafe action trong blocking suite. Một lỗi critical chặn live dù tổng điểm cao.
- Replay 100 lần cùng event/click/job → một logical receipt/request acceptance/report; provider end-to-end exactly-once không được hứa. Unknown send phải được đối soát, không tự coi fail để gửi lại.
- Sau SQL/RabbitMQ/worker restart phải thấy state bền vững và continued coverage đúng. Test actual SQL/issued role grants/late-body revocation, không chỉ mock stores. Giữ nguyên các quality gates hiện có của repo; không sửa workflow để làm xanh.

[G01–G24](acceptance-cases.jsonl) là scenario specifications cần chuyển thành test, chưa phải tests đã chạy. Dữ liệu minh họa [golden-batch.json](examples/golden-batch.json) gồm sáu tin, hai phiếu và một report; deadline “15h” còn cần làm rõ. Không đưa tin khách thật vào public Git hoặc GitHub Issues.

## 4. Pilot thực tế

Một nhóm được owner chỉ định trước; listen-only, đối chiếu với tin đã nhận thực tế. Khi đủ U1 chuyển sang tạo nháp/phiếu có người review. Khi đạt U2 gửi từng report đã được bấm duyệt. Mục tiêu quan sát tối thiểu **10 report batches trong ít nhất 3 ngày làm việc**, có tình huống restart, bổ sung, hủy/sửa nháp và một diễn tập unknown-send bằng fault injection được phép. Không stress/flood tài khoản thật.

Ghi số yêu cầu bỏ sót/nhầm, thời gian IT cần sửa nháp, số note trùng, report bị chặn, gap minutes, usage/cost và thời gian từ tin mới đến draft. Thời gian manual review đo riêng, không gọi đó là latency AI. Mở nhóm kế tiếp sau review số liệu, không tự tăng số nhóm theo đồng hồ.

Ước lượng lập lịch ban đầu nếu có 2–3 coding agents, review kỹ thuật hằng ngày, nền hiện có phù hợp và account test được cấp: 3–5 ngày làm việc cho spike/contracts; khoảng tuần 2 có inbox/task-note thử nội bộ; khoảng tuần 3–4 có luồng report được duyệt và pilot. Đây là **estimate có điều kiện**, không cam kết; chưa tính chờ account/quyền/hạ tầng hoặc lease nền cần sửa. Re-estimate sau GI-01 và một mock end-to-end, không suy thời gian từ số task.

## 5. Đầu vào owner cần chốt, không hỏi lại điều đã biết

Đã biết: Zalo cá nhân, connector, nhóm khách, IT notes trước report trước auto-reply. Cần chốt: một nhóm pilot cụ thể và scope company/project; ai duyệt report/ai nhận IT; nhóm có cho phép đưa nội dung qua model provider nào; retention và nội dung nhạy cảm phải che; ngôn ngữ/template; ngân sách; giới hạn một tin đã kiểm ở connector. Ban đầu trigger manual nên **không cần chọn giờ gửi** để bắt đầu.

Không yêu cầu người dùng dán session/cookie/OTP vào chat. Secret trong secret manager hoặc protected local credential store phù hợp hiện trạng deployment. Group roster không tự trở thành portal members. Phát hiện token/password trong tin thì mask/quarantine theo policy trước model/public projection; evidence access hạn chế, không log raw text.

## 6. Rollback và vận hành

Feature flags đề xuất, tất cả disable mặc định khi scaffold: `GroupIntakeEnabled`, `GroupDigestSendingEnabled`, `GroupSummaryAutoDraftEnabled`, `CustomerAutoReplyEnabled`. Tên cấu hình phải được implement/validate theo conventions repo; plan không tự tạo runtime flag. Enable theo group grant, không bật toàn account.

Outage/session restriction: tắt send, giữ intake được phép nếu an toàn, báo coverage gap. Không reconnect storm/cố né hạn chế. Có task draft nhưng report fail: IT vẫn thấy note đã commit, không rollback xóa công việc để “gửi lại từ đầu”. Approval/source/role revoked: hủy pending intent hoặc block; sau thay đổi phải review lại. Body quá dài: compact/review hoặc block, không tách nhiều tin.

Rollback deployment giữ migration tương thích; restore với senders OFF và suppression trước reconnect. Purge theo yêu cầu phủ raw/draft/request evidence/report preview/cache/spool/jobs/backup suppression. Report đã gửi ra nhóm là dữ liệu đã chia sẻ, không hứa xóa khỏi thiết bị người nhận.

## 7. Prompt giao coding agents

```text
Read AGENTS.md and current repo/workstream/PR state first.
Read docs/features/customer-group-intake/README.md and your issue #276–#279.
Owner priority: group intake → reviewed IT notes → one reviewed same-group digest.
Do NOT start conversational auto-reply, full wiki/ERP, Windows setup or a second backend as dependencies.
Respect the current #274/#239 leases; one implementation issue/branch/Draft PR.
Contract/mock work can continue when live credentials are blocked; report the difference.
Every note needs same-group evidence. AI chooses neither destination nor authority.
Internal notes and public report are separate projections. SQL business note completion
is not runtime task completion. Never send from GET/reload or blindly retry an unknown send.
Run focused and retained gates; record actual commands, commit, skipped checks and rollback.
No account login, live send or production deploy without the relevant explicit permission/gate.
```

Validation kit command (có thật trong plan): `python docs/features/customer-group-intake/validate_plan.py --self-test`. Chỉ kiểm cấu trúc kế hoạch/fixture, không chạy AI/SQL/Zalo. Application commands dùng scripts hiện có do agent kiểm lúc implement; không báo passed nếu chưa chạy.
