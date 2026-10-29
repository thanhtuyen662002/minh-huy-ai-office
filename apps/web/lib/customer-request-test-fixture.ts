import type { CustomerRequest, CustomerRequestDetail } from "./customer-requests";
export const requestCompany = "a2222222-2222-4222-8222-222222222222";
export const requestTenant = "11111111-1111-4111-8111-111111111111";
export const requestUser = "33333333-3333-4333-8333-333333333333";
export const assigneeUser = "44444444-4444-4444-8444-444444444444";
export const requestTicket: CustomerRequest = { code: "YC-0001", id: "55555555-5555-4555-8555-555555555555", status: "new", origin: "ai_extracted",
  customerName: "Khách Minh An", summary: "Không lưu được đơn hàng\nở kho mới.", sourceGroupExternalId: "zalo-customer-a", sourceGroupName: "Nhóm Minh An",
  assigneeUserId: null, createdAt: "2026-10-26T01:00:00+00:00", updatedAt: "2026-10-26T01:00:00+00:00", rowVersion: "AAAAAAAAAAE=" };
export const requestDetail: CustomerRequestDetail = { request: requestTicket, reportedBy: "Chị Lan", customerDesiredDueText: "Trước chiều mai, nếu có thể",
  sourceWindowFrom: "2026-10-26T00:00:00+00:00", sourceWindowTo: "2026-10-26T01:00:00+00:00",
  items: [{ content: "Kiểm tra lỗi khi chọn kho", priority: "high", kind: "bug" }],
  evidence: [{ quote: "Chọn kho mới thì không lưu được đơn <script>alert(1)</script>", sourceMessageId: "m-123", senderName: "Lan", messageTimestamp: "2026-10-26T00:30:00+00:00" }],
  openQuestions: ["Phiên bản ERP và mã kho nào?"], events: [{ eventType: "created", actorType: "service", actorId: requestUser, actorName: null, oldValue: null, newValue: "new", timestamp: requestTicket.createdAt }] };
export const memberDirectory = { companyId: requestCompany, items: [
  { userId: assigneeUser, displayName: "Huy", userActive: true, membershipActive: true, roles: ["member"] },
  { userId: requestUser, displayName: "Đã ngừng hoạt động", userActive: false, membershipActive: true, roles: [] },
], offset: 0, limit: 100, hasMore: false };
