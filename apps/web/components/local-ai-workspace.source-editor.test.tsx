import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { LocalAiWorkspace } from "./local-ai-workspace";
import { LocalDataSource } from "../lib/local-ai-workspace";

// Synthetic context/registry persistence, not SQL or OIDC acceptance.
const companyA = "22222222-2222-2222-2222-222222222222";
const companyB = "66666666-6666-6666-6666-666666666666";
const sourceId = "33333333-3333-3333-3333-333333333333";
const original: LocalDataSource = { id: sourceId, logicalName: "ERP-A", kind: "SqlServer", environment: "Test", purpose: "Read-only ERP", allowRead: true, allowWrite: false, maxConcurrency: 2, isEnabled: true };
const success = "Đã lưu thay đổi và xác nhận lại nguồn dữ liệu.";
function deferred() {
  let resolve!: (value: Response) => void;
  const promise = new Promise<Response>(done => { resolve = done; });
  return { promise, resolve };
}
function backend(override: (url: string, init?: RequestInit) => Promise<Response> | Response | undefined = () => undefined) {
  const state = { roles: ["admin"], user: "A", allowWrite: true, puts: 0, gets: 0, stored: { ...original }, payloads: [] as Record<string, unknown>[] };
  const fetcher = vi.fn(async (url: string, init?: RequestInit) => {
    const custom = override(url, init);
    if (custom !== undefined) return custom;
    const company = new URL(url, "http://fixture.invalid").searchParams.get("companyId") ?? companyA;
    if (url.startsWith("/api/local/session?")) return Response.json({ tenantId: "tenant", companyId: company, userId: state.user, roles: state.roles });
    if (url === "/api/local/session/logout") return Response.json({ ok: true });
    if (url === "/api/local/session/login") { state.user = "B"; return Response.json({ ok: true }); }
    if (url.startsWith("/api/local/data-sources?")) {
      state.gets++;
      return Response.json([company === companyB || state.user === "B" ? { ...original, logicalName: "PUBLIC-B" } : state.stored]);
    }
    if (init?.method === "PUT") {
      state.puts++;
      const payload = JSON.parse(String(init.body)); state.payloads.push(payload);
      if (!state.allowWrite || !state.roles.includes("admin")) return Response.json({}, { status: 403 });
      state.stored = { ...state.stored, ...payload }; return Response.json(state.stored);
    }
    if (url.includes("connection-test")) return Response.json({ code: "success" });
    throw new Error(`Unexpected fixture route ${url}`);
  });
  vi.stubGlobal("fetch", fetcher);
  return { state, fetcher };
}
async function open() {
  const view = render(<LocalAiWorkspace companyId={companyA} companyName="Fixture" />);
  await screen.findByRole("option", { name: "ERP-A" });
  fireEvent.click(view.container.querySelector("nav button:nth-child(2)")!);
  fireEvent.click(await screen.findByRole("button", { name: "Chỉnh sửa" }));
  await screen.findByRole("form", { name: "Chỉnh sửa nguồn dữ liệu" });
  return view;
}
function change(name = "SAVED-A") { fireEvent.change(screen.getByLabelText("Tên nguồn"), { target: { value: name } }); }
function submit() { fireEvent.submit(screen.getByRole("form", { name: "Chỉnh sửa nguồn dữ liệu" })); }
beforeEach(() => vi.stubGlobal("BroadcastChannel", undefined));
afterEach(() => vi.unstubAllGlobals());

it("uses metadata-only PUT without protected fields and confirms an authoritative GET before success", async () => {
  const { state, fetcher } = backend(); const view = await open(); change("  SAVED-A  ");
  fireEvent.change(screen.getByLabelText("Mục đích"), { target: { value: "Saved purpose" } });
  fireEvent.change(screen.getByLabelText("Số tác vụ đồng thời"), { target: { value: "3" } });
  submit(); await screen.findByText(success);
  expect(state.payloads).toEqual([{ logicalName: "SAVED-A", purpose: "Saved purpose", maxConcurrency: 3, isEnabled: true }]);
  expect(fetcher.mock.calls.find(([, init]) => init?.method === "PUT")?.[0]).toContain(`/${sourceId}/metadata?`);
  const put = fetcher.mock.calls.findIndex(([, init]) => init?.method === "PUT");
  expect(fetcher.mock.calls.slice(put + 1).some(([url]) => url.startsWith("/api/local/data-sources?"))).toBe(true);
  expect(view.container.querySelector('input[name="connectionSecretReference"],input[name="allowWrite"],input[name="kind"]')).toBeNull();
  fireEvent.click(screen.getByRole("button", { name: "Làm mới" }));
  await waitFor(() => expect(state.gets).toBe(4));
  expect(await screen.findByRole("heading", { name: "SAVED-A" })).toBeTruthy();
});

it("disabling the selected source removes it from task selection and re-enabling makes it selectable/testable", async () => {
  const { state } = backend(); const view = await open();
  fireEvent.click(screen.getByLabelText("Bật nguồn dữ liệu")); submit(); await screen.findByText(success);
  fireEvent.click(view.container.querySelector("nav button:first-child")!);
  expect(view.container.querySelector("select")!.value).toBe("");
  expect(screen.queryByRole("option", { name: "ERP-A" })).toBeNull();
  expect(view.container.querySelector('textarea + button')!.hasAttribute("disabled")).toBe(true);
  fireEvent.click(view.container.querySelector("nav button:nth-child(2)")!); fireEvent.click(screen.getByRole("button", { name: "Chỉnh sửa" }));
  await screen.findByLabelText("Bật nguồn dữ liệu"); fireEvent.click(screen.getByLabelText("Bật nguồn dữ liệu")); submit(); await screen.findByText(success);
  expect(state.stored.isEnabled).toBe(true); fireEvent.click(screen.getByRole("button", { name: "Kiểm tra" })); await screen.findByText("Kết nối tốt");
  fireEvent.click(view.container.querySelector("nav button:first-child")!); expect(screen.getByRole("option", { name: "ERP-A" })).toBeTruthy();
});

it("cancel discards draft without mutations or success; reopening shows the registered values", async () => {
  const { state } = backend(); await open(); change("DISCARDED"); fireEvent.click(screen.getByRole("button", { name: "Hủy" }));
  expect(state.puts).toBe(0); expect(screen.queryByText(success)).toBeNull();
  fireEvent.click(screen.getByRole("button", { name: "Chỉnh sửa" })); await screen.findByLabelText("Tên nguồn"); expect((screen.getByLabelText("Tên nguồn") as HTMLInputElement).value).toBe("ERP-A");
});

it.each(["", "   ", "x".repeat(201)])("rejects invalid logical name before contacting mutation endpoint (%s)", async name => {
  const { state } = backend(); await open(); change(name); submit(); await screen.findByRole("alert"); expect(state.puts).toBe(0); expect(screen.queryByText(success)).toBeNull();
});
it.each(["0", "1025", "1.5", "", "NaN"])("rejects invalid concurrency before mutation (%s)", async value => {
  const { state } = backend(); await open(); fireEvent.change(screen.getByLabelText("Số tác vụ đồng thời"), { target: { value } }); submit(); await screen.findByRole("alert"); expect(state.puts).toBe(0);
});

it("members see no editor and issue no mutation", async () => {
  const { state } = backend(); state.roles = ["member"];
  const view = render(<LocalAiWorkspace companyId={companyA} companyName="Fixture" />); await screen.findByRole("option", { name: "ERP-A" }); fireEvent.click(view.container.querySelector("nav button:nth-child(2)")!);
  expect(screen.queryByRole("button", { name: "Chỉnh sửa" })).toBeNull(); expect(state.puts).toBe(0);
});
it("revocation discovered during save clears the editor before sending PUT", async () => {
  const { state } = backend(); await open(); change(); state.roles = ["member"]; submit();
  await waitFor(() => expect(screen.queryByRole("form", { name: "Chỉnh sửa nguồn dữ liệu" })).toBeNull()); expect(state.puts).toBe(0); expect(screen.queryByText(success)).toBeNull();
});
it("backend denial after context validation remains authoritative", async () => {
  const { state } = backend(); await open(); change(); state.allowWrite = false; submit();
  expect((await screen.findByRole("alert")).textContent).toContain("từ chối quyền"); expect(state.puts).toBe(1); expect(state.stored.logicalName).toBe("ERP-A"); expect(screen.queryByText(success)).toBeNull();
});

it.each([400, 404, 502])("HTTP %s reports failure, keeps draft and permits explicit retry", async status => {
  let failing = true; const { state } = backend((_, init) => init?.method === "PUT" && failing ? Response.json({}, { status }) : undefined);
  await open(); change(); submit(); await screen.findByRole("alert"); expect(screen.queryByText(success)).toBeNull(); expect((screen.getByLabelText("Tên nguồn") as HTMLInputElement).value).toBe("SAVED-A");
  failing = false; submit(); await screen.findByText(success); expect(state.puts).toBe(1);
});

it("repeated submit is serialized and success waits for authoritative reload", async () => {
  const pending = deferred(); let holdReload = false;
  const { state, fetcher } = backend((url, init) => {
    if (init?.method === "PUT") holdReload = true;
    if (url.startsWith("/api/local/data-sources?") && holdReload) return pending.promise;
  });
  await open(); change(); submit(); submit(); await waitFor(() => expect(state.puts).toBe(1));
  await waitFor(() => expect(fetcher.mock.calls.filter(([url]) => url.startsWith("/api/local/data-sources?")).length).toBe(3));
  expect(screen.queryByText(success)).toBeNull(); expect(screen.getByRole("button", { name: "Đang lưu…" }).hasAttribute("disabled")).toBe(true); expect(screen.getByRole("button", { name: "Hủy" }).hasAttribute("disabled")).toBe(true);
  await act(async () => pending.resolve(Response.json([state.stored]))); await screen.findByText(success); expect(state.puts).toBe(1);
});

it.each(["missing", "metadata-change"])("detects a changed metadata snapshot before PUT (%s)", async mode => {
  let changed = false; const { state } = backend(url => changed && url.startsWith("/api/local/data-sources?") ? Response.json(mode === "missing" ? [] : [{ ...original, ...(mode === "protected-change" ? { allowWrite: true } : { purpose: "Concurrent update" }) }]) : undefined);
  await open(); change(); changed = true; submit(); expect((await screen.findByRole("alert")).textContent).toContain("Nguồn đã thay đổi"); expect(state.puts).toBe(0);
});

// Independent review's exact interleaving: another manager changes policy and
// rotates a reference after the final client GET, before the metadata PUT.
// The fixture now models the admitted narrow server contract, not the rejected
// full-PUT assignments. Backend two-context/SQL tests prove the actual service.
it("metadata-only save retains protected fields changed after its last preflight GET (independent P1 regression)", async () => {
  let stored = { ...original };
  let credential = "secretref://env/FIXTURE-ORIGINAL";
  let sent: Record<string, unknown> | null = null;
  vi.stubGlobal("fetch", vi.fn(async (url: string, init?: RequestInit) => {
    if (url.startsWith("/api/local/session?")) return Response.json({ tenantId: "fixture", companyId: companyA, userId: "fixture-admin", roles: ["admin"] });
    if (url.startsWith("/api/local/data-sources?")) return Response.json([stored]);
    if (init?.method === "PUT") {
      expect(url).toContain(`/${sourceId}/metadata?`);
      stored = { ...stored, kind: "Postgres", environment: "Production", allowRead: false, allowWrite: true };
      credential = "secretref://env/FIXTURE-ROTATED";
      const payload = JSON.parse(String(init.body)); sent = payload;
      stored = { ...stored, logicalName: payload.logicalName, purpose: payload.purpose, maxConcurrency: payload.maxConcurrency, isEnabled: payload.isEnabled };
      return Response.json(stored);
    }
    throw new Error("Unexpected independent fixture route");
  }));
  const view = await open(); change("RENAMED-ONLY"); submit(); await screen.findByText(success);
  expect(Object.keys(sent!)).toEqual(["logicalName", "purpose", "maxConcurrency", "isEnabled"]);
  expect(credential).toBe("secretref://env/FIXTURE-ROTATED");
  expect(stored.allowRead).toBe(false); expect(stored.allowWrite).toBe(true);
  expect(stored.environment).toBe("Production"); expect(stored.kind).toBe("Postgres");
  fireEvent.click(view.container.querySelector("nav button:first-child")!);
  expect(view.container.querySelector("select")!.value).toBe("");
  expect(screen.queryByRole("option", { name: "RENAMED-ONLY" })).toBeNull();
});
it.each(["malformed-put", "failed-reload", "stale-reload"])("does not invent success when a write cannot be confirmed (%s)", async mode => {
  let written = false;
  backend((url, init) => {
    if (init?.method === "PUT") { written = true; if (mode === "malformed-put") return Response.json({ id: "wrong" }); }
    if (url.startsWith("/api/local/data-sources?") && written) return mode === "failed-reload" ? Response.json({}, { status: 502 }) : mode === "stale-reload" ? Response.json([original]) : undefined;
  });
  const view = await open(); change(); submit(); expect((await screen.findByRole("alert")).textContent).toContain("chưa xác nhận"); expect(screen.queryByText(success)).toBeNull();
  fireEvent.click(view.container.querySelector("nav button:first-child")!);
  expect(view.container.querySelector("select")!.value).toBe("");
  expect(view.container.querySelector('textarea + button')!.hasAttribute("disabled")).toBe(true);
});

it.each(["preflight", "put", "reload"])("company change fences a late %s response", async phase => {
  const pending = deferred(); let saving = false; let written = false; let intercepted = false;
  backend((url, init) => {
    if (init?.method === "PUT") written = true;
    if (saving && url.includes(companyA) && !intercepted && (phase === "put" ? init?.method === "PUT" : url.startsWith("/api/local/data-sources?") && (phase === "preflight" || written))) { intercepted = true; return pending.promise; }
  });
  const view = await open(); change("PRIVATE-A-EDIT"); saving = true; submit(); await waitFor(() => expect(intercepted).toBe(true));
  view.rerender(<LocalAiWorkspace companyId={companyB} companyName="Company B" />); await screen.findByRole("option", { name: "PUBLIC-B" });
  await act(async () => pending.resolve(Response.json(phase === "put" ? { ...original, logicalName: "PRIVATE-A-EDIT" } : [{ ...original, logicalName: "PRIVATE-A-EDIT" }])));
  expect(view.container.textContent).not.toContain("PRIVATE-A"); expect(view.container.querySelector('input[name="logicalName"]')).toBeNull(); expect(screen.queryByText(success)).toBeNull();
});
it("logout clears pending editor; B login cannot receive a late A update", async () => {
  const pending = deferred(); let intercepted = false;
  backend((_, init) => { if (init?.method === "PUT") { intercepted = true; return pending.promise; } });
  const view = await open(); change("PRIVATE-A-EDIT"); submit(); await waitFor(() => expect(intercepted).toBe(true)); fireEvent.click(view.container.querySelector("header button")!);
  await screen.findByLabelText("Tên đăng nhập"); fireEvent.change(view.container.querySelector('input[name="password"]')!, { target: { value: "fixture-only" } }); fireEvent.submit(view.container.querySelector('input[name="password"]')!.closest("form")!); await screen.findByRole("option", { name: "PUBLIC-B" });
  await act(async () => pending.resolve(Response.json({ ...original, logicalName: "PRIVATE-A-EDIT" }))); expect(view.container.textContent).not.toContain("PRIVATE-A"); expect(screen.queryByText(success)).toBeNull();
});

it.each(["preflight", "put", "reload"])("a %s 401 clears editor/draft rather than leaving a stale admin view", async phase => {
  let saving = false; let written = false; let intercepted = false;
  backend((url, init) => {
    if (init?.method === "PUT") written = true;
    if (saving && !intercepted && (phase === "put" ? init?.method === "PUT" : url.startsWith("/api/local/data-sources?") && (phase === "preflight" || written))) { intercepted = true; return Response.json({}, { status: 401 }); }
  });
  const view = await open(); change("PRIVATE-A-EDIT"); saving = true; submit(); await screen.findByLabelText("Tên đăng nhập");
  expect(view.container.textContent).not.toContain("PRIVATE-A"); expect(view.container.querySelector('input[name="logicalName"]')).toBeNull(); expect(screen.queryByText(success)).toBeNull();
});

it("same-session focus preserves the editor draft and field focus", async () => {
  backend(); await open(); change("FOCUS-DRAFT"); const input = screen.getByLabelText("Tên nguồn") as HTMLInputElement; input.focus();
  fireEvent(window, new Event("focus")); await act(async () => { for (let i = 0; i < 8; i++) await Promise.resolve(); });
  expect(screen.getByLabelText("Tên nguồn")).toBe(input); expect(input.value).toBe("FOCUS-DRAFT"); expect(document.activeElement).toBe(input);
});

it("network mutation failure keeps draft and retries the same existing source", async () => {
  let offline = true; const { state } = backend((_, init) => init?.method === "PUT" && offline ? Promise.reject(new Error("Synthetic offline")) : undefined);
  await open(); change(); submit(); await screen.findByRole("alert"); expect(screen.queryByText(success)).toBeNull(); expect(state.stored.logicalName).toBe("ERP-A");
  offline = false; submit(); await screen.findByText(success); expect(state.stored.logicalName).toBe("SAVED-A");
});
