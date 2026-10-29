import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { KnowledgePanel } from "./knowledge-panel";
afterEach(cleanup);
const company = "22222222-2222-4222-8222-222222222222", user = "33333333-3333-4333-8333-333333333333", other = "44444444-4444-4444-8444-444444444444";
const general = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", customer = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
const at = "2026-10-29T08:00:00Z";
const summary = (id: string, title: string, customerName: string | null) => ({ id, customerName, title, updatedBy: other, updatedAt: at });
const list = { items: [summary(customer, "Khoá sổ cuối tháng", "Cty Hoà Phát"), summary(general, "Quy trình hỗ trợ", null)], hasMore: false };
const page = (id: string, body: string, rowVersion = "AAAAAAAAB9A=") => ({ ...summary(id, id === general ? "Quy trình hỗ trợ" : "Khoá sổ cuối tháng",
  id === general ? null : "Cty Hoà Phát"), body, createdBy: other, createdAt: at, rowVersion });
const members = { companyId: company, items: [{ userId: other, displayName: "Chị Lan", userActive: true, membershipActive: true, roles: [] }], offset: 0, limit: 100, hasMore: false };

function fixture(override: (url: string, init?: RequestInit) => Response | undefined = () => undefined) {
  const unauthorized = vi.fn(), openTickets = vi.fn(), validate = vi.fn(async () => true);
  const request = vi.fn(async (_generation: number, url: string, init?: RequestInit) => override(url, init)
    ?? (url.includes("/company/members") ? Response.json(members)
      : url.includes(`/${general}?`) ? Response.json(page(general, "# Bước đầu\n- Gọi khách\n> Luôn ghi mã phiếu"))
      : url.includes(`/${customer}?`) ? Response.json(page(customer, "- [ ] Kiểm tra phiếu thu\nXem YC-0012 và YC-0012"))
      : Response.json(list)));
  const view = render(<KnowledgePanel companyId={company} userId={user} generation={1} isCurrent={() => true} validate={validate}
    request={request} onUnauthorized={unauthorized} onOpenTickets={openTickets} />);
  return { ...view, request, unauthorized, openTickets, validate };
}

it("groups pages into general and per-customer and opens the first page with its blocks", async () => {
  const view = fixture();
  const tree = screen.getByRole("complementary", { name: "Danh sách trang" });
  await within(tree).findByRole("button", { name: "Quy trình hỗ trợ" });
  expect(within(tree).getByText("Cty Hoà Phát")).toBeTruthy();
  await screen.findByRole("heading", { name: "Khoá sổ cuối tháng" });
  await screen.findByText(/Sửa bởi Chị Lan/);
  expect((screen.getByRole("checkbox") as HTMLInputElement).checked).toBe(false);
  expect(screen.getAllByText("YC-0012")).toHaveLength(1);
  fireEvent.click(screen.getByRole("button", { name: "Xem phiếu của Cty Hoà Phát" }));
  expect(view.openTickets).toHaveBeenCalledWith("Cty Hoà Phát");
  fireEvent.click(within(tree).getByRole("button", { name: "Quy trình hỗ trợ" }));
  await screen.findByRole("heading", { name: "Bước đầu" });
  expect(screen.getByText("Luôn ghi mã phiếu")).toBeTruthy();
  expect(view.request.mock.calls.every(([, , init]) => init?.cache === "no-store")).toBe(true);
});

it("ticks a todo by saving the page with the version it was read at", async () => {
  const view = fixture((url, init) => init?.method === "PUT" ? Response.json(page(customer, "- [x] Kiểm tra phiếu thu\nXem YC-0012", "AAAAAAAACAA=")) : undefined);
  fireEvent.click(await screen.findByRole("checkbox"));
  await waitFor(() => expect((screen.getByRole("checkbox") as HTMLInputElement).checked).toBe(true));
  const put = view.request.mock.calls.find(([, , init]) => init?.method === "PUT")!;
  expect(put[1]).toBe(`/api/local/knowledge-pages/${customer}?companyId=${company}`);
  expect(JSON.parse(put[2]!.body as string)).toEqual({ title: "Khoá sổ cuối tháng", body: "- [x] Kiểm tra phiếu thu\nXem YC-0012 và YC-0012",
    customerName: "Cty Hoà Phát", rowVersion: "AAAAAAAAB9A=" });
});

it("creates a page and shows it in the tree", async () => {
  const fresh = "cccccccc-cccc-4ccc-8ccc-cccccccccccc";
  const view = fixture((url, init) => init?.method === "POST"
    ? Response.json({ ...page(fresh, "Nội dung"), title: "Trang mới A", customerName: null }) : undefined);
  await screen.findByRole("heading", { name: "Khoá sổ cuối tháng" });
  fireEvent.click(screen.getByRole("button", { name: "Trang mới" }));
  fireEvent.change(screen.getByLabelText("Tiêu đề"), { target: { value: "  Trang mới A " } });
  fireEvent.change(screen.getByLabelText("Nội dung"), { target: { value: "Nội dung" } });
  fireEvent.click(screen.getByRole("button", { name: "Lưu trang" }));
  await screen.findByRole("heading", { name: "Trang mới A" });
  const post = view.request.mock.calls.find(([, , init]) => init?.method === "POST")!;
  expect(JSON.parse(post[2]!.body as string)).toEqual({ title: "Trang mới A", body: "Nội dung", customerName: null });
  expect(within(screen.getByRole("complementary")).getByRole("button", { name: "Trang mới A" })).toBeTruthy();
});

it("explains a conflicting edit and deletes only after a second click", async () => {
  const view = fixture((url, init) => init?.method === "PUT" ? Response.json({ error: "x" }, { status: 409 })
    : init?.method === "DELETE" ? new Response(null, { status: 204 }) : undefined);
  await screen.findByRole("heading", { name: "Khoá sổ cuối tháng" });
  fireEvent.click(screen.getByRole("button", { name: "Sửa" }));
  fireEvent.click(screen.getByRole("button", { name: "Lưu trang" }));
  expect((await screen.findByRole("alert")).textContent).toMatch(/người khác sửa/);
  fireEvent.click(screen.getByRole("button", { name: "Huỷ" }));
  fireEvent.click(screen.getByRole("button", { name: "Xoá" }));
  expect(view.request.mock.calls.some(([, , init]) => init?.method === "DELETE")).toBe(false);
  fireEvent.click(screen.getByRole("button", { name: "Xác nhận xoá" }));
  await waitFor(() => expect(view.request.mock.calls.some(([, url, init]) => init?.method === "DELETE"
    && url === `/api/local/knowledge-pages/${customer}?companyId=${company}&rowVersion=AAAAAAAAB9A%3D`)).toBe(true));
});

it.each([401, 403])("drops page content on refusal %i", async status => {
  let denied = false;
  const view = fixture(url => denied && url.includes("/knowledge-pages/") ? Response.json({ error: "PRIVATE" }, { status }) : undefined);
  await screen.findByRole("heading", { name: "Khoá sổ cuối tháng" });
  denied = true;
  fireEvent.click(screen.getByRole("button", { name: "Quy trình hỗ trợ" }));
  if (status === 401) await waitFor(() => expect(view.unauthorized).toHaveBeenCalled());
  else await screen.findByRole("alert");
  if (status === 403) expect(screen.queryByRole("button", { name: "Khoá sổ cuối tháng" })).toBeNull();
  expect(screen.queryByText("PRIVATE")).toBeNull();
});
