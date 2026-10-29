import { fireEvent, render, screen, within } from "@testing-library/react";
import { expect, it, vi } from "vitest";
import { WorkspaceOverview } from "./workspace-overview";
import { assigneeUser, memberDirectory, requestCompany as company, requestTicket } from "../lib/customer-request-test-fixture";

function data(respond: (status: string) => Response) {
  const request = vi.fn(async (_generation: number, url: string, _init?: RequestInit) => respond(new URL(url, "http://web.test").searchParams.get("status")!));
  return { companyId: company, generation: 1, isCurrent: () => true, validate: vi.fn(async () => true), request, onUnauthorized: vi.fn() };
}
const ticket = (code: string, status: string, extra: object = {}) => ({ ...requestTicket, code, status,
  id: `55555555-5555-4555-8555-${code.slice(3).padStart(12, "0")}`, ...extra });

it("counts tickets per status, lists those needing attention and opens Công việc", async () => {
  const now = new Date().toISOString();
  const pages: Record<string, unknown[]> = {
    new: [ticket("YC-0003", "new", { createdAt: "2026-10-27T01:00:00+00:00" }), ticket("YC-0002", "new")], needs_clarification: [ticket("YC-0001", "needs_clarification")],
    in_progress: [ticket("YC-0004", "in_progress")], done: [ticket("YC-0005", "done", { updatedAt: now }), ticket("YC-0006", "done")],
  };
  const source = data(status => Response.json({ items: pages[status], offset: 0, limit: 100, hasMore: false }));
  const onOpen = vi.fn();
  render(<WorkspaceOverview companyName="Minh Huy" onOpen={onOpen} data={source} />);
  const attention = await screen.findByRole("region", { name: "Việc cần chú ý" });
  expect((await within(attention).findAllByText(/^YC-/)).map(item => item.textContent)).toEqual(["YC-0001", "YC-0002", "YC-0003"]);
  expect(screen.getByText("Việc mới").nextSibling?.textContent).toBe("2");
  expect(screen.getByText("Đang xử lý").nextSibling?.textContent).toBe("1");
  expect(screen.getByText("Xong hôm nay").nextSibling?.textContent).toBe("1");
  expect(source.request.mock.calls.every(([, url, init]) => url.includes(`companyId=${company}`) && url.includes("limit=100") && init?.cache === "no-store")).toBe(true);
  fireEvent.click(screen.getByRole("button", { name: "Mở Công việc" }));
  expect(onOpen).toHaveBeenCalledWith("cong-viec");
});
it("explains a disabled ticket feature and signs out on 401", async () => {
  render(<WorkspaceOverview companyName="Minh Huy" onOpen={vi.fn()} data={data(() => Response.json({}, { status: 404 }))} />);
  await screen.findByText("Tính năng phiếu yêu cầu khách hàng chưa được bật.");
  const denied = data(() => Response.json({}, { status: 401 }));
  render(<WorkspaceOverview companyName="Minh Huy" onOpen={vi.fn()} data={denied} />);
  await vi.waitFor(() => expect(denied.onUnauthorized).toHaveBeenCalled());
});
it("greets the signed-in member, reports open tickets and shows the bots", async () => {
  const pages: Record<string, unknown[]> = { new: [ticket("YC-0002", "new")], needs_clarification: [ticket("YC-0001", "needs_clarification")],
    in_progress: [ticket("YC-0004", "in_progress")], done: [] };
  const source = { ...data(status => status ? Response.json({ items: pages[status], offset: 0, limit: 100, hasMore: false }) : Response.json(memberDirectory)),
    userId: assigneeUser, onGlance: vi.fn() };
  const zalo = { channel: "zalo" as const, enabled: true, dryRun: false, summarySchedule: ["08:00", "17:30"], timeZone: "Asia/Ho_Chi_Minh" as const,
    techGroupExternalId: "t", customerGroups: [{ externalId: "g", displayName: "Nhóm A", customerName: "Cty A" }], configVersion: "1",
    status: { loginState: "logged_in" as const, groups: [], lastSummaryAt: null, lastError: null, receivedAt: new Date().toISOString() } };
  const onOpen = vi.fn();
  render(<WorkspaceOverview companyName="Minh Huy" onOpen={onOpen} data={source} bots={[zalo]} />);
  await screen.findByRole("heading", { name: "Chào Huy, hôm nay có 2 việc cần bạn xem" });
  expect(source.onGlance).toHaveBeenCalledWith({ displayName: "Huy", openTickets: "3" });
  const bots = screen.getByRole("region", { name: "Kênh và bot" });
  expect(within(bots).getByText("Bot Zalo · 1 nhóm khách")).toBeTruthy();
  expect(within(bots).getByText("Đang chạy")).toBeTruthy();
  fireEvent.click(within(bots).getByRole("button", { name: "Cài đặt" }));
  expect(onOpen).toHaveBeenCalledWith("ket-noi");
  expect(screen.getByRole("region", { name: "Hoạt động gần đây" })).toBeTruthy();
});
it("still loads when the member directory is not readable", async () => {
  const source = { ...data(status => status ? Response.json({ items: [], offset: 0, limit: 100, hasMore: false }) : Response.json({}, { status: 403 })),
    userId: assigneeUser, onGlance: vi.fn() };
  render(<WorkspaceOverview companyName="Minh Huy" onOpen={vi.fn()} data={source} />);
  await screen.findByRole("heading", { name: "Chào bạn, hôm nay chưa có việc mới cần xem" });
  expect(source.onGlance).toHaveBeenCalledWith({ displayName: null, openTickets: "0" });
});
