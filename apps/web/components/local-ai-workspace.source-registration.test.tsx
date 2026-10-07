import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { LocalAiWorkspace } from "./local-ai-workspace";
const companyA = "22222222-2222-2222-2222-222222222222";
const companyB = "66666666-6666-6666-6666-666666666666";
const bindingId = "33333333-3333-3333-3333-333333333333";
const created = { id: "44444444-4444-4444-4444-444444444444", logicalName: "NEW ERP", environment: "Production",
  purpose: "Reporting", kind: "sql-server", allowRead: true, allowWrite: false, isEnabled: true, maxConcurrency: 2 };
function deferred() {
  let resolve!: (response: Response) => void;
  const promise = new Promise<Response>(done => { resolve = done; }); return { promise, resolve };
}
function backend(override: (url: string, init?: RequestInit) => Promise<Response> | Response | undefined = () => undefined) {
  const state = { roles: ["admin"], stored: [] as typeof created[], posts: [] as Record<string, unknown>[], empty: false };
  const fetcher = vi.fn(async (url: string, init?: RequestInit) => {
    const custom = override(url, init); if (custom !== undefined) return custom;
    const company = new URL(url, "http://fixture.invalid").searchParams.get("companyId");
    if (url.startsWith("/api/local/session?")) return Response.json({ tenantId: "tenant", companyId: company, userId: "owner", roles: state.roles });
    if (url.startsWith("/api/local/data-sources/registration-options?")) return Response.json({
      items: state.empty ? [] : [{ bindingId, label: "PRIVATE COMPANY A", version: 9223372036854775807, versionToken: "9223372036854775807" }],
      offset: 0, limit: 100, hasMore: false,
    });
    if (url.startsWith("/api/local/data-sources/read-only-registration?")) {
      state.posts.push(JSON.parse(String(init?.body))); state.stored = [created]; return Response.json(created);
    }
    if (url.startsWith("/api/local/data-sources?")) return Response.json(company === companyB ? [] : state.stored);
    if (url === "/api/local/session/logout") return Response.json({ ok: true });
    throw new Error("Unexpected route " + url);
  });
  vi.stubGlobal("fetch", fetcher); return { state, fetcher };
}
async function open() {
  const view = render(<LocalAiWorkspace companyId={companyA} companyName="Fixture" />);
  await waitFor(() => expect(view.container.querySelector("nav")).toBeTruthy());
  fireEvent.click(view.container.querySelector("nav button:nth-child(2)")!);
  fireEvent.click(await screen.findByRole("button", { name: "Thêm nguồn chỉ đọc" }));
  await screen.findByRole("option", { name: "PRIVATE COMPANY A" });
  fireEvent.change(screen.getByLabelText("Tên nguồn mới"), { target: { value: "NEW ERP" } });
  fireEvent.change(screen.getByLabelText("Mục đích nguồn mới"), { target: { value: "Reporting" } });
  return view;
}
const submit = () => fireEvent.submit(screen.getByRole("form", { name: "Đăng ký nguồn chỉ đọc" }));
beforeEach(() => vi.stubGlobal("BroadcastChannel", undefined));
afterEach(() => vi.unstubAllGlobals());
it("registers only opaque IDs/metadata and confirms fresh source list before success", async () => {
  const { state, fetcher } = backend(); await open(); submit();
  await screen.findByText("Đã đăng ký nguồn chỉ đọc và xác nhận lại danh sách.");
  expect(state.posts).toHaveLength(1);
  expect(state.posts[0]).toMatchObject({ bindingId, bindingVersion: "9223372036854775807", logicalName: "NEW ERP", purpose: "Reporting", maxConcurrency: 2 });
  expect(Object.keys(state.posts[0]).sort()).toEqual(["bindingId", "bindingVersion", "operationId", "logicalName", "purpose", "environment", "maxConcurrency"].sort());
  const post = fetcher.mock.calls.findIndex(([url]) => url.includes("read-only-registration"));
  expect(fetcher.mock.calls.slice(post + 1).some(([url]) => url.startsWith("/api/local/data-sources?"))).toBe(true);
  expect(await screen.findByRole("heading", { name: "NEW ERP" })).toBeTruthy();
});
it("serializes duplicate submit and retries a lost response with byte-identical operation/body", async () => {
  const pending = deferred(); const bodies: string[] = [];
  let first = true;
  backend((url, init) => {
    if (!url.includes("read-only-registration")) return undefined;
    bodies.push(String(init?.body));
    if (first) { first = false; return pending.promise; }
    return Response.json(created, { status: 503 });
  });
  await open(); submit(); submit(); await waitFor(() => expect(bodies).toHaveLength(1));
  await act(async () => pending.resolve(Response.json({}, { status: 503 })));
  await screen.findByRole("alert"); expect((screen.getByLabelText("Tên nguồn mới") as HTMLInputElement).disabled).toBe(true);
  submit(); await waitFor(() => expect(bodies).toHaveLength(2)); expect(bodies[0]).toBe(bodies[1]);
});
it("clears private choices and sends no POST after administration is revoked", async () => {
  const { state } = backend(); await open(); state.roles = ["viewer"]; submit();
  await waitFor(() => expect(screen.queryByRole("form", { name: "Đăng ký nguồn chỉ đọc" })).toBeNull());
  expect(screen.queryByText("PRIVATE COMPANY A")).toBeNull(); expect(state.posts).toHaveLength(0);
});
it("ignores a registration result delivered after company changes", async () => {
  const pending = deferred();
  backend(url => url.includes("read-only-registration") ? pending.promise : undefined);
  const view = await open(); submit();
  await screen.findByRole("button", { name: "Đang đăng ký…" });
  view.rerender(<LocalAiWorkspace companyId={companyB} companyName="B" />);
  await act(async () => pending.resolve(Response.json(created)));
  await waitFor(() => expect(screen.queryByText("PRIVATE COMPANY A")).toBeNull());
  expect(screen.queryByText("Đã đăng ký nguồn chỉ đọc và xác nhận lại danh sách.")).toBeNull();
  expect(screen.queryByRole("heading", { name: "NEW ERP" })).toBeNull();
});
it("keeps registration absent for viewers", async () => {
  const { state } = backend(); state.roles = ["viewer"];
  const view = render(<LocalAiWorkspace companyId={companyA} companyName="Fixture" />);
  await waitFor(() => expect(view.container.querySelector("nav")).toBeTruthy());
  fireEvent.click(view.container.querySelector("nav button:nth-child(2)")!);
  expect(screen.queryByRole("button", { name: "Thêm nguồn chỉ đọc" })).toBeNull();
});
it("clears private choices and draft immediately on registration 401", async () => {
  backend(url => url.includes("read-only-registration") ? Response.json({}, { status: 401 }) : undefined);
  await open(); submit();
  await waitFor(() => expect(screen.queryByRole("form", { name: "Đăng ký nguồn chỉ đọc" })).toBeNull());
  expect(screen.queryByText("PRIVATE COMPANY A")).toBeNull();
  expect(screen.queryByDisplayValue("NEW ERP")).toBeNull();
  expect(await screen.findByLabelText("Tên đăng nhập")).toBeTruthy();
});
it("clears existing choices and draft when options 403 observes role loss after preflight", async () => {
  let revokeDuringOptions = false;
  const { state } = backend(url => {
    if (url.includes("registration-options") && revokeDuringOptions) {
      state.roles = ["viewer"];
      return Response.json({}, { status: 403 });
    }
  });
  await open();
  expect(screen.getByDisplayValue("NEW ERP")).toBeTruthy();
  revokeDuringOptions = true;
  fireEvent.click(screen.getByRole("button", { name: "Tải lại kết nối" }));
  await waitFor(() => expect(screen.queryByRole("form", { name: "Đăng ký nguồn chỉ đọc" })).toBeNull());
  expect(screen.queryByText("PRIVATE COMPANY A")).toBeNull();
  expect(screen.queryByDisplayValue("NEW ERP")).toBeNull();
  expect(state.posts).toHaveLength(0);
});
