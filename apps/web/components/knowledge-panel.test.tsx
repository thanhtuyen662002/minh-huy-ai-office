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

const file = (name: string, text = "%PDF") => new File([text], name, { type: "application/pdf" });
it("uploads a file, shows its text for review and saves it as a page under the open customer", async () => {
  const fresh = "cccccccc-cccc-4ccc-8ccc-cccccccccccc";
  const view = fixture((url, init) => url.includes("/extract?") ? Response.json({ title: "Hướng dẫn", parts: ["# Khoá sổ\n- Bước 1"], truncated: false })
    : init?.method === "POST" ? Response.json({ ...page(fresh, "# Khoá sổ\n- Bước 1"), title: "Hướng dẫn" }) : undefined);
  await screen.findByRole("heading", { name: "Khoá sổ cuối tháng" });
  fireEvent.change(screen.getByLabelText("Chọn file để tạo trang"), { target: { files: [file("Hướng dẫn.pdf")] } });
  await waitFor(() => expect((screen.getByLabelText("Nội dung") as HTMLTextAreaElement).value).toBe("# Khoá sổ\n- Bước 1"));
  expect((screen.getByLabelText("Tiêu đề") as HTMLInputElement).value).toBe("Hướng dẫn");
  expect((screen.getByLabelText(/Khách hàng/) as HTMLInputElement).value).toBe("Cty Hoà Phát");
  expect(screen.getByText(/Đã lấy chữ từ file “Hướng dẫn.pdf”/)).toBeTruthy();
  const upload = view.request.mock.calls.find(([, url]) => url.includes("/extract?"))!;
  expect(upload[1]).toBe(`/api/local/knowledge-pages/extract?companyId=${company}&fileName=H%C6%B0%E1%BB%9Bng%20d%E1%BA%ABn.pdf`);
  expect(upload[2]).toMatchObject({ method: "POST", headers: { "Content-Type": "application/octet-stream" } });
  fireEvent.click(screen.getByRole("button", { name: "Lưu trang" }));
  await screen.findByRole("heading", { name: "Hướng dẫn" });
  const post = view.request.mock.calls.find(([, url, init]) => init?.method === "POST" && !url.includes("/extract?"))!;
  expect(JSON.parse(post[2]!.body as string)).toEqual({ title: "Hướng dẫn", body: "# Khoá sổ\n- Bước 1", customerName: "Cty Hoà Phát" });
});

it("saves a long file as numbered pages and retries only the parts that failed", async () => {
  let posts = 0;
  const view = fixture((url, init) => {
    if (url.includes("/extract?")) return Response.json({ title: "Bảng giá", parts: ["phần một", "phần hai", "phần ba"], truncated: true });
    if (init?.method !== "POST") return undefined;
    posts++;
    if (posts === 2) return Response.json({ error: "x" }, { status: 503 });
    const body = JSON.parse(init.body as string);
    return Response.json({ ...page(`dddddddd-dddd-4ddd-8ddd-00000000000${posts}`, body.body), title: body.title, customerName: null });
  });
  await screen.findByRole("heading", { name: "Khoá sổ cuối tháng" });
  fireEvent.click(screen.getByRole("button", { name: "Trang mới" }));
  fireEvent.click(screen.getByRole("button", { name: "Huỷ" }));
  fireEvent.change(screen.getByLabelText("Chọn file để tạo trang"), { target: { files: [file("Bảng giá.xlsx")] } });
  await screen.findByText(/sẽ được lưu thành 3 trang/);
  expect(screen.getByText(/chỉ lấy 2.000.000 ký tự đầu/)).toBeTruthy();
  fireEvent.change(screen.getByLabelText(/Khách hàng/), { target: { value: "" } });
  fireEvent.click(screen.getByRole("button", { name: "Lưu 3 trang" }));
  expect((await screen.findByRole("alert")).textContent).toMatch(/Đã lưu 1\/3 trang/);
  fireEvent.click(screen.getByRole("button", { name: "Lưu 2 trang" }));
  await waitFor(() => expect(posts).toBe(4));
  const titles = view.request.mock.calls.filter(([, url, init]) => init?.method === "POST" && !url.includes("/extract?"))
    .map(([, , init]) => JSON.parse(init!.body as string).title);
  expect(titles).toEqual(["Bảng giá (phần 1/3)", "Bảng giá (phần 2/3)", "Bảng giá (phần 2/3)", "Bảng giá (phần 3/3)"]);
  await waitFor(() => expect(screen.queryByText(/sẽ được lưu thành/)).toBeNull());
});

it("inserts a file into the page being edited and explains files it cannot read", async () => {
  fixture(url => url.includes("fileName=cu.doc") ? Response.json({ error: "x", reason: "legacy" }, { status: 422 })
    : url.includes("/extract?") ? Response.json({ title: "Ghi chú", parts: ["Nội dung file"], truncated: false }) : undefined);
  await screen.findByRole("heading", { name: "Khoá sổ cuối tháng" });
  fireEvent.click(screen.getByRole("button", { name: "Sửa" }));
  fireEvent.change(screen.getByLabelText("Chọn file để chèn nội dung"), { target: { files: [file("ghi-chu.txt")] } });
  await waitFor(() => expect((screen.getByLabelText("Nội dung") as HTMLTextAreaElement).value)
    .toBe("- [ ] Kiểm tra phiếu thu\nXem YC-0012 và YC-0012\n\nNội dung file"));
  fireEvent.change(screen.getByLabelText("Chọn file để chèn nội dung"), { target: { files: [file("cu.doc")] } });
  expect((await screen.findByRole("alert")).textContent).toMatch(/Lưu thành .docx/);
  fireEvent.change(screen.getByLabelText("Chọn file để chèn nội dung"), { target: { files: [new File([new Uint8Array(20 * 1024 * 1024 + 1)], "lon.pdf")] } });
  await waitFor(() => expect(screen.getByRole("alert").textContent).toMatch(/tối đa 20 MB/));
});
