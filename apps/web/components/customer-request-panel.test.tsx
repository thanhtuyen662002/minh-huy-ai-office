import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { expect, it, vi } from "vitest";
import { CustomerRequestPanel } from "./customer-request-panel";
import { assigneeUser, memberDirectory, requestCompany as company, requestDetail, requestTenant, requestTicket, requestUser } from "../lib/customer-request-test-fixture";
import { requestConflict, requestTaken } from "../lib/customer-requests";

const page = (url: string, items: unknown[], hasMore = false) => {
  const query = new URL(url, "http://web.test").searchParams;
  return { items, offset: Number(query.get("offset")), limit: Number(query.get("limit")), hasMore };
};
type Override = (url: string, init?: RequestInit) => Response | Promise<Response> | undefined;
function fixture(override: Override = () => undefined, { isAdmin = true, assigneeUserId = requestTicket.assigneeUserId as string | null } = {}) {
  const active = { current: true }, onUnauthorized = vi.fn(), validate = vi.fn(async () => active.current);
  let ticket = { ...requestTicket, assigneeUserId }, detail = { ...requestDetail, request: { ...requestDetail.request, assigneeUserId } };
  const request = vi.fn(async (_generation: number, url: string, init?: RequestInit) => {
    const custom = override(url, init); if (custom !== undefined) return custom;
    if (url.startsWith("/api/local/company/members?")) return Response.json(memberDirectory);
    if (init?.method === "PATCH") {
      const patch = JSON.parse(String(init.body));
      ticket = { ...ticket, ...patch, rowVersion: "AAAAAAAAAAI=" };
      detail = { ...detail, request: ticket, events: [...detail.events, { eventType: "status_changed", actorType: "user", actorId: requestUser, actorName: null,
        oldValue: "new", newValue: ticket.status, timestamp: ticket.updatedAt }] };
      return Response.json(ticket);
    }
    return Response.json(url.includes(`/${requestTicket.code}?`) ? { ...detail, request: ticket }
      : page(url, [ticket]));
  });
  const props = { tenantId: requestTenant, companyId: company, userId: requestUser, isAdmin, generation: 1,
    isCurrent: () => active.current, validate, request, onUnauthorized };
  return { ...render(<CustomerRequestPanel {...props} />), props, request, active, validate, onUnauthorized };
}
async function listView() {
  fireEvent.click(await screen.findByRole("button", { name: "YC-0001" }).then(() => screen.getByRole("button", { name: "Danh sách" })));
  return screen.findByRole("table");
}
async function open() {
  fireEvent.click(await screen.findByRole("button", { name: "YC-0001" }));
  return screen.findByRole("article", { name: "Phiếu YC-0001" });
}

it("renders list and evidence-backed AI detail with Vietnamese labels through reads only", async () => {
  const view = fixture();
  const table = await listView();
  expect(within(table).getAllByRole("columnheader").map(item => item.textContent)).toEqual([
    "Mã phiếu", "Khách hàng", "Nhóm khách", "Tóm tắt", "Trạng thái", "Người phụ trách", "Ngày tạo",
  ]);
  await open();
  expect(screen.getByText(/AI tóm tắt từ Zalo/).textContent).toContain("chưa được nhân viên xác nhận");
  expect(screen.getByText(requestDetail.items[0].content)).toBeTruthy();
  expect(screen.getByText("Ưu tiên: Cao · Loại: Lỗi")).toBeTruthy();
  expect(screen.getByRole("heading", { name: "Còn chưa rõ / cần hỏi lại" })).toBeTruthy();
  expect(screen.getByText(requestDetail.openQuestions[0])).toBeTruthy();
  expect(screen.getByRole("blockquote").textContent).toBe(requestDetail.evidence[0].quote);
  expect(screen.getByText(/Lan ·/).textContent).toContain(new Date(requestDetail.evidence[0].messageTimestamp).toLocaleString("vi-VN"));
  expect(screen.getByText(requestDetail.customerDesiredDueText!)).toBeTruthy();
  expect(screen.getByText(/chưa phải cam kết/)).toBeTruthy();
  expect(screen.getByText("Tạo phiếu · Mới")).toBeTruthy();
  expect(screen.queryByRole("option", { name: "Đã ngừng hoạt động" })).toBeNull();
  expect(view.request.mock.calls.every(([, url, init]) => url.includes(`companyId=${company}`) && !init?.method && init?.cache === "no-store")).toBe(true);
});
it("filters by status and customer, pages newest-first on the server, and resets paging on new filters", async () => {
  const items = Array.from({ length: 25 }, (_, index) => ({ ...requestTicket, code: `YC-${1000 + index}`,
    id: `60000000-0000-4000-8000-${String(index + 1).padStart(12, "0")}` }));
  const view = fixture(url => {
    if (url.startsWith("/api/local/customer-requests?")) {
      const query = new URL(url, "http://web.test").searchParams;
      if (query.get("limit") === "100") return Response.json(page(url, [requestTicket]));
      return Response.json(page(url, items, query.get("offset") === "0"));
    }
  });
  await listView();
  fireEvent.change(screen.getByLabelText("Lọc trạng thái"), { target: { value: "needs_clarification" } });
  fireEvent.change(screen.getByLabelText("Khách hàng"), { target: { value: "  Minh & An  " } });
  fireEvent.click(screen.getByRole("button", { name: "Lọc phiếu" }));
  await screen.findByRole("table");
  fireEvent.click(screen.getByRole("button", { name: "Trang sau" })); await screen.findByText("Trang 2"); await screen.findByRole("table");
  let calls = view.request.mock.calls.filter(([, url]) => url.startsWith("/api/local/customer-requests?"));
  const query = new URL(calls[calls.length - 1][1], "http://web.test").searchParams;
  expect(Object.fromEntries(query)).toEqual({ companyId: company, status: "needs_clarification", customer: "Minh & An", offset: "25", limit: "25" });
  expect((screen.getByRole("button", { name: "Trang sau" }) as HTMLButtonElement).disabled).toBe(true);
  fireEvent.click(screen.getByRole("button", { name: "Lọc phiếu" })); await screen.findByRole("table");
  calls = view.request.mock.calls.filter(([, url]) => url.startsWith("/api/local/customer-requests?"));
  expect(new URL(calls[calls.length - 1][1], "http://web.test").searchParams.get("offset")).toBe("0");
});
it("saves status with RowVersion and omitted assignee, then reloads history and the new version", async () => {
  const view = fixture(); await open();
  fireEvent.change(screen.getByLabelText("Trạng thái"), { target: { value: "in_progress" } });
  fireEvent.click(screen.getByRole("button", { name: "Lưu thay đổi" }));
  await screen.findByText("Đổi trạng thái: Mới → Đang xử lý");
  expect(JSON.parse(String(view.request.mock.calls.find(([, , init]) => init?.method === "PATCH")![2]!.body)))
    .toEqual({ rowVersion: requestTicket.rowVersion, status: "in_progress" });
  fireEvent.change(screen.getByLabelText("Trạng thái"), { target: { value: "done" } });
  fireEvent.click(screen.getByRole("button", { name: "Lưu thay đổi" })); await screen.findByText("Đổi trạng thái: Mới → Xong");
  const patches = view.request.mock.calls.filter(([, , init]) => init?.method === "PATCH");
  expect(JSON.parse(String(patches[1][2]!.body)).rowVersion).toBe("AAAAAAAAAAI=");
});
it("assigns active directory members and explicitly sends null to unassign", async () => {
  const view = fixture(); await open(); await screen.findByRole("option", { name: "Huy" });
  fireEvent.change(screen.getByLabelText("Người phụ trách"), { target: { value: assigneeUser } });
  fireEvent.click(screen.getByRole("button", { name: "Lưu thay đổi" }));
  await screen.findByText("Đã lưu thay đổi."); await screen.findByRole("article");
  fireEvent.change(screen.getByLabelText("Người phụ trách"), { target: { value: "" } });
  fireEvent.click(screen.getByRole("button", { name: "Lưu thay đổi" })); await screen.findByText("Đã lưu thay đổi."); await screen.findByRole("article");
  expect(view.request.mock.calls.filter(([, , init]) => init?.method === "PATCH").map(([, , init]) => JSON.parse(String(init!.body))))
    .toEqual([{ rowVersion: requestTicket.rowVersion, assigneeUserId: assigneeUser }, { rowVersion: "AAAAAAAAAAI=", assigneeUserId: null }]);
});
it("shows the exact conflict text, freezes stale edits, and reloads without retrying PATCH", async () => {
  let conflict = false;
  const view = fixture((url, init) => init?.method === "PATCH" ? (conflict = true, Response.json({ error: "PRIVATE" }, { status: 409 }))
    : conflict && url.includes("/YC-0001?") ? Response.json({ ...requestDetail, request: { ...requestTicket, status: "done", rowVersion: "AAAAAAAAAAM=" } }) : undefined);
  await open(); fireEvent.change(screen.getByLabelText("Trạng thái"), { target: { value: "in_progress" } });
  fireEvent.click(screen.getByRole("button", { name: "Lưu thay đổi" })); await screen.findByText(requestConflict);
  expect((screen.getByRole("button", { name: "Lưu thay đổi" }) as HTMLButtonElement).disabled).toBe(true);
  fireEvent.click(screen.getByRole("button", { name: "Tải lại phiếu" })); await screen.findByRole("article");
  expect((screen.getByLabelText("Trạng thái") as HTMLSelectElement).value).toBe("done");
  expect(screen.queryByText(requestConflict)).toBeNull();
  expect(view.request.mock.calls.filter(([, , init]) => init?.method === "PATCH")).toHaveLength(1);
});
it.each([403, 503])("handles directory %i without preventing a status change", async status => {
  fixture(url => url.startsWith("/api/local/company/members?") ? Response.json({}, { status }) : undefined);
  await open(); await screen.findByText(status === 403 ? /Bạn không có quyền xem danh bạ/ : /Không tải được đầy đủ danh bạ/);
  expect((screen.getByLabelText("Người phụ trách") as HTMLSelectElement).disabled).toBe(true);
  expect((screen.getByLabelText("Trạng thái") as HTMLSelectElement).disabled).toBe(false);
  fireEvent.change(screen.getByLabelText("Trạng thái"), { target: { value: "rejected" } });
  fireEvent.click(screen.getByRole("button", { name: "Lưu thay đổi" })); await screen.findByText("Đổi trạng thái: Mới → Từ chối");
});
it("loads later directory pages and only offers active company members", async () => {
  fixture(url => url.startsWith("/api/local/company/members?") ? Response.json(url.includes("offset=100")
    ? { ...memberDirectory, offset: 100 }
    : { ...memberDirectory, items: Array.from({ length: 100 }, (_, index) => ({ ...memberDirectory.items[0], userId: `70000000-0000-4000-8000-${String(index + 1).padStart(12, "0")}`, displayName: `Kỹ thuật ${index}` })), hasMore: true }) : undefined);
  await open(); await screen.findByRole("option", { name: "Huy" });
  expect(screen.getByRole("option", { name: "Kỹ thuật 99" })).toBeTruthy();
});
it("supports loading, empty, missing, forbidden and recoverable error states", async () => {
  const releases: (() => void)[] = [];
  const view = fixture(url => url.startsWith("/api/local/customer-requests?")
    ? new Promise(resolve => { releases.push(() => resolve(Response.json(page(url, [])))); }) : undefined);
  expect(screen.getByText("Đang tải yêu cầu khách hàng…")).toBeTruthy();
  await waitFor(() => expect(releases).toHaveLength(4));
  await act(async () => releases.forEach(release => release()));
  await screen.findByText(/Chưa có yêu cầu khách hàng/); view.unmount();
  for (const status of [403, 404, 503]) {
    const next = fixture(() => Response.json({ private: "PRIVATE" }, { status }));
    await screen.findByRole("alert"); expect(screen.queryByText("PRIVATE")).toBeNull(); expect(screen.queryByRole("table")).toBeNull(); next.unmount();
  }
});
it("clears rendered private data on refusal and invokes sign-out on 401", async () => {
  let denied = false;
  const view = fixture(() => denied ? Response.json({}, { status: 401 }) : undefined); await open(); denied = true;
  fireEvent.click(screen.getByRole("button", { name: "Tải lại phiếu" }));
  await waitFor(() => expect(view.onUnauthorized).toHaveBeenCalled());
  expect(screen.queryByRole("article")).toBeNull(); expect(screen.queryByText(requestDetail.evidence[0].quote)).toBeNull();
});
it("aborts and discards late detail when company, user or tenant changes", async () => {
  let release!: (response: Response) => void, signal: AbortSignal | null | undefined;
  const view = fixture((url, init) => url.includes("/YC-0001?") ? (signal = init?.signal, new Promise(resolve => { release = resolve; })) : undefined);
  fireEvent.click(await screen.findByRole("button", { name: "YC-0001" })); await waitFor(() => expect(release).toBeTruthy());
  const foreign = "88888888-8888-4888-8888-888888888888";
  view.rerender(<CustomerRequestPanel {...view.props} tenantId={foreign} companyId={foreign} userId={foreign} generation={2}
    request={async (_generation, url) => Response.json(url.startsWith("/api/local/company/members?") ? { ...memberDirectory, companyId: foreign }
      : page(url, []))} />);
  expect(signal?.aborted).toBe(true);
  await act(async () => release(Response.json(requestDetail)));
  await screen.findByText(/Chưa có yêu cầu khách hàng/); expect(screen.queryByRole("article")).toBeNull();
});
it("does not publish a response if final session validation fails", async () => {
  const view = fixture(url => url.includes("/customer-requests?") ? (view.active.current = false, Response.json(page(url, [requestTicket]))) : undefined);
  await waitFor(() => expect(view.active.current).toBe(false)); expect(screen.queryByRole("table")).toBeNull();
});
it("serializes explicit saves and aborts the pending write on unmount", async () => {
  let release!: (response: Response) => void, signal: AbortSignal | null | undefined;
  const view = fixture((_url, init) => init?.method === "PATCH" ? (signal = init.signal, new Promise(resolve => { release = resolve; })) : undefined);
  await open(); fireEvent.change(screen.getByLabelText("Trạng thái"), { target: { value: "done" } });
  const form = screen.getByRole("form", { name: "Cập nhật phiếu" });
  fireEvent.submit(form); fireEvent.submit(form);
  await waitFor(() => expect(release).toBeTruthy());
  expect(view.request.mock.calls.filter(([, , init]) => init?.method === "PATCH")).toHaveLength(1);
  view.unmount(); expect(signal?.aborted).toBe(true);
  await act(async () => release(Response.json({ ...requestTicket, status: "done" })));
});
it.each([403, 503])("handles PATCH %i without repeating a write or retaining forbidden detail", async status => {
  const view = fixture((_url, init) => init?.method === "PATCH" ? Response.json({}, { status }) : undefined);
  await open(); fireEvent.change(screen.getByLabelText("Trạng thái"), { target: { value: "done" } });
  fireEvent.click(screen.getByRole("button", { name: "Lưu thay đổi" })); await screen.findByRole("alert");
  if (status === 403) expect(screen.queryByRole("article")).toBeNull();
  else {
    expect(screen.getByText(/Chưa xác nhận được thay đổi đã lưu/)).toBeTruthy();
    expect((screen.getByRole("button", { name: "Lưu thay đổi" }) as HTMLButtonElement).disabled).toBe(true);
  }
  expect(view.request.mock.calls.filter(([, , init]) => init?.method === "PATCH")).toHaveLength(1);
});
it("shows the tech-group Zalo member who changed a status", async () => {
  fixture(url => url.includes(`/${requestTicket.code}?`) ? Response.json({ ...requestDetail, request: { ...requestTicket, status: "done" },
    events: [...requestDetail.events, { eventType: "status_changed", actorType: "zalo_member", actorId: requestUser, actorName: "Minh Kỹ thuật",
      oldValue: "new", newValue: "done", timestamp: requestTicket.updatedAt }] }) : undefined);
  await open();
  expect(screen.getByText("Đổi trạng thái: Mới → Xong")).toBeTruthy();
  expect(screen.getByText(/Nhóm Kỹ thuật Zalo: Minh Kỹ thuật/)).toBeTruthy();
});it("shows a board with one column per open status and places tickets by their own status", async () => {
  const doing = { ...requestTicket, code: "YC-0002", id: "55555555-5555-4555-8555-555555555556", status: "in_progress" as const };
  const view = fixture(url => url.startsWith("/api/local/customer-requests?") ? Response.json(page(url,
    new URL(url, "http://web.test").searchParams.get("status") === "in_progress" ? [doing] : [requestTicket])) : undefined);
  await screen.findByRole("button", { name: "YC-0001" });
  const statuses = view.request.mock.calls.filter(([, url]) => url.startsWith("/api/local/customer-requests?"))
    .map(([, url]) => new URL(url, "http://web.test").searchParams.get("status"));
  expect(statuses).toEqual(["new", "needs_clarification", "in_progress", "done"]);
  expect(within(screen.getByRole("region", { name: "Cột Mới" })).getAllByRole("button").map(item => item.getAttribute("aria-label"))).toEqual(["YC-0001"]);
  expect(within(screen.getByRole("region", { name: "Cột Đang xử lý" })).getByRole("button", { name: "YC-0002" })).toBeTruthy();
  expect(within(screen.getByRole("region", { name: "Cột Xong" })).getByText("Không có phiếu")).toBeTruthy();
});
it("moves a ticket by dropping it on another column with its RowVersion", async () => {
  const view = fixture();
  const card = await screen.findByRole("button", { name: "YC-0001" });
  const target = screen.getByRole("region", { name: "Cột Đang xử lý" });
  fireEvent.dragStart(card, { dataTransfer: { effectAllowed: "" } });
  const reads = view.request.mock.calls.length;
  fireEvent.dragOver(target); fireEvent.drop(target);
  // The card moves before the server answers and the board is never locked or reloaded.
  expect(within(target).getByRole("button", { name: "YC-0001" })).toBeTruthy();
  expect(screen.queryByText("Đang lưu thay đổi…")).toBeNull();
  expect((screen.getByRole("button", { name: "Tải lại phiếu" }) as HTMLButtonElement).disabled).toBe(false);
  await waitFor(() => expect(within(target).getByRole("button", { name: "YC-0001" }).getAttribute("aria-busy")).toBe("false"));
  expect(view.request.mock.calls.slice(reads).map(([, , init]) => init?.method)).toEqual(["PATCH"]);
  expect(view.request.mock.calls.filter(([, , init]) => init?.method === "PATCH").map(([, , init]) => JSON.parse(String(init!.body))))
    .toEqual([{ rowVersion: requestTicket.rowVersion, status: "in_progress", assigneeUserId: requestUser }]);
  fireEvent.dragStart(await screen.findByRole("button", { name: "YC-0001" }), { dataTransfer: { effectAllowed: "" } });
  fireEvent.drop(screen.getByRole("region", { name: "Cột Mới" }));
  expect(view.request.mock.calls.filter(([, , init]) => init?.method === "PATCH")).toHaveLength(1);
});
it("opens detail beside the board, applies quick status actions and closes without reloading the board", async () => {
  const view = fixture(); await open();
  expect(screen.getByRole("region", { name: "Cột Mới" })).toBeTruthy();
  fireEvent.click(screen.getByRole("button", { name: "Bắt đầu xử lý" }));
  await screen.findByText("Đổi trạng thái: Mới → Đang xử lý");
  expect(JSON.parse(String(view.request.mock.calls.find(([, , init]) => init?.method === "PATCH")![2]!.body)))
    .toEqual({ rowVersion: requestTicket.rowVersion, status: "in_progress", assigneeUserId: requestUser });
  const reads = view.request.mock.calls.length;
  fireEvent.click(screen.getByRole("button", { name: "Đóng chi tiết phiếu" }));
  expect(screen.queryByRole("article")).toBeNull();
  expect(view.request.mock.calls).toHaveLength(reads);
  expect(within(screen.getByRole("region", { name: "Cột Đang xử lý" })).getByRole("button", { name: "YC-0001" })).toBeTruthy();
});
it("closes detail on an outside press without swallowing the press", async () => {
  fixture(); await open(); await screen.findByRole("article");
  const outside = document.createElement("button"); document.body.append(outside);
  const pressed = vi.fn(); outside.addEventListener("pointerdown", pressed);
  fireEvent.pointerDown(screen.getByRole("article")); expect(screen.getByRole("article")).toBeTruthy();
  fireEvent.pointerDown(outside);
  expect(pressed).toHaveBeenCalled(); expect(screen.queryByRole("article")).toBeNull();
  outside.remove();
});
it("keeps the other columns when one board column fails with a server error", async () => {
  fixture(url => url.startsWith("/api/local/customer-requests?") && url.includes("status=new")
    ? Response.json({ private: "PRIVATE" }, { status: 503 }) : undefined);
  expect((await within(await screen.findByRole("region", { name: "Cột Mới" })).findByRole("alert")).textContent).toMatch(/Không tải được cột này/);
  expect(screen.getByRole("region", { name: "Cột Đang xử lý" })).toBeTruthy();
  expect(screen.queryByText("PRIVATE")).toBeNull();
});

it("keeps a ticket another member has taken locked for non-administrators", async () => {
  const view = fixture(undefined, { isAdmin: false, assigneeUserId: assigneeUser });
  const card = await screen.findByRole("button", { name: "YC-0001" });
  expect(card.getAttribute("draggable")).toBe("false");
  expect(within(card).getByLabelText("Người khác đã nhận")).toBeTruthy();
  fireEvent.dragStart(card, { dataTransfer: { effectAllowed: "" } });
  fireEvent.drop(screen.getByRole("region", { name: "Cột Xong" }));
  const article = await open();
  expect(within(article).getByRole("note").textContent).toContain("Chỉ người phụ trách hoặc quản trị viên");
  expect(within(article).queryByRole("button", { name: "Đánh dấu xong" })).toBeNull();
  expect(within(article).getByRole("combobox", { name: "Trạng thái" })).toHaveProperty("disabled", true);
  expect(view.request.mock.calls.some(([, , init]) => init?.method === "PATCH")).toBe(false);
});
it("lets a member move their own ticket and only offers themselves as assignee", async () => {
  fixture(undefined, { isAdmin: false, assigneeUserId: requestUser });
  const card = await screen.findByRole("button", { name: "YC-0001" });
  expect(card.getAttribute("draggable")).toBe("true");
  const article = await open();
  const options = within(within(article).getByRole("combobox", { name: "Người phụ trách" })).getAllByRole("option").map(item => item.getAttribute("value"));
  expect(options.every(value => value === "" || value === requestUser)).toBe(true);
});
it("explains when the server refuses a ticket someone else has taken", async () => {
  fixture((_url, init) => init?.method === "PATCH" ? Response.json({ error: "taken" }, { status: 423 }) : undefined, { isAdmin: false });
  const card = await screen.findByRole("button", { name: "YC-0001" });
  fireEvent.dragStart(card, { dataTransfer: { effectAllowed: "" } });
  fireEvent.drop(screen.getByRole("region", { name: "Cột Xong" }));
  expect((await screen.findByRole("alert")).textContent).toBe(`YC-0001: ${requestTaken}`);
  // The refused move is undone and the board re-read quietly, keeping the error visible.
  await waitFor(() => expect(within(screen.getByRole("region", { name: "Cột Mới" })).getByRole("button", { name: "YC-0001" })).toBeTruthy());
  expect(within(screen.getByRole("region", { name: "Cột Xong" })).queryByRole("button", { name: "YC-0001" })).toBeNull();
  await waitFor(() => expect((screen.getByRole("button", { name: "Tải lại phiếu" }) as HTMLButtonElement).disabled).toBe(false));
  expect(screen.getByRole("alert").textContent).toBe(`YC-0001: ${requestTaken}`);
});
it("keeps other cards draggable while one card is still saving", async () => {
  const other = { ...requestTicket, code: "YC-0002", id: "55555555-5555-4555-8555-555555555556", createdAt: "2020-01-01T00:00:00.000Z" };
  let finish: (response: Response) => void = () => undefined;
  const view = fixture((url, init) => {
    if (init?.method === "PATCH" && url.includes("YC-0001")) return new Promise<Response>(resolve => { finish = resolve; });
    if (init?.method === "PATCH") return Response.json({ ...other, ...JSON.parse(String(init.body)), rowVersion: "AAAAAAAAAAM=" });
    if (url.startsWith("/api/local/customer-requests?")) return Response.json(page(url,
      new URL(url, "http://web.test").searchParams.get("status") === "new" ? [requestTicket, other] : []));
  });
  const firstCard = await screen.findByRole("button", { name: "YC-0001" });
  const done = screen.getByRole("region", { name: "Cột Xong" });
  fireEvent.dragStart(firstCard, { dataTransfer: { effectAllowed: "" } });
  fireEvent.drop(done);
  await waitFor(() => expect(view.request.mock.calls.some(([, url, init]) => init?.method === "PATCH" && url.includes("YC-0001"))).toBe(true));
  const first = within(done).getByRole("button", { name: "YC-0001" });
  expect(first.getAttribute("draggable")).toBe("false"); expect(within(first).getByText("Đang lưu")).toBeTruthy();
  const second = screen.getByRole("button", { name: "YC-0002" });
  expect(second.getAttribute("draggable")).toBe("true");
  fireEvent.dragStart(second, { dataTransfer: { effectAllowed: "" } }); fireEvent.drop(done);
  await waitFor(() => expect(within(done).getByRole("button", { name: "YC-0002" }).getAttribute("aria-busy")).toBe("false"));
  await act(async () => finish(Response.json({ ...requestTicket, status: "done", rowVersion: "AAAAAAAAAAI=" })));
  await waitFor(() => expect(within(done).getByRole("button", { name: "YC-0001" }).getAttribute("aria-busy")).toBe("false"));
  expect(within(done).getAllByRole("button").map(item => item.getAttribute("aria-label"))).toEqual(["YC-0001", "YC-0002"]);
});
