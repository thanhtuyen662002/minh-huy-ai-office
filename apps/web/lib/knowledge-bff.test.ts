// @vitest-environment node
import { afterEach, beforeEach, expect, it, vi } from "vitest";
const mocks = vi.hoisted(() => ({ cookies: vi.fn(), runtime: vi.fn(), read: vi.fn(), fetch: vi.fn() }));
vi.mock("next/headers", () => ({ cookies: mocks.cookies }));
vi.mock("./browser-auth-runtime", () => ({ getBrowserAuthRuntime: mocks.runtime }));
import { GET as list, POST as create } from "../app/api/local/knowledge-pages/route";
import { DELETE as remove, GET as read, PUT as update } from "../app/api/local/knowledge-pages/[id]/route";
import { requestCompany as company, requestUser as user } from "./customer-request-test-fixture";

const id = "66666666-6666-4666-8666-666666666666", version = "AAAAAAAAB9A=";
const context = { params: Promise.resolve({ id }) };
const page = { id, customerName: "Cty Hoà Phát Đạt", title: "Khoá sổ", body: "- [ ] Kiểm tra", createdBy: user, updatedBy: user,
  createdAt: "2026-10-29T01:00:00+00:00", updatedAt: "2026-10-29T02:00:00+00:00", rowVersion: version };
const request = (query = `companyId=${company}`, init?: RequestInit) => new Request(`http://web.test/api/local/knowledge-pages?${query}`, init);
const write = (method: string, value: unknown, origin = "http://web.test", query?: string) =>
  request(query, { method, headers: { "Content-Type": "application/json", Origin: origin }, body: value === undefined ? undefined : JSON.stringify(value) });
beforeEach(() => {
  vi.clearAllMocks(); vi.stubEnv("AIOFFICE_BROWSER_OIDC_ENABLED", "false"); vi.stubEnv("AIOFFICE_LOCAL_UI_ENABLED", "true");
  mocks.cookies.mockResolvedValue({ get: () => ({ value: "PRIVATE-TOKEN" }) });
  mocks.read.mockResolvedValue({ companyId: company, accessToken: "PRIVATE-TOKEN" });
  mocks.runtime.mockReturnValue({ coreApiOrigin: "http://core.test", settings: {}, sessions: { read: mocks.read } });
  vi.stubGlobal("fetch", mocks.fetch);
});
afterEach(() => { vi.unstubAllGlobals(); vi.unstubAllEnvs(); });

it("lists pages for the selected company and drops unknown fields", async () => {
  const summary = { id, customerName: null, title: "Hướng dẫn", updatedBy: user, updatedAt: page.updatedAt };
  mocks.fetch.mockResolvedValue(Response.json({ items: [{ ...summary, secret: "PRIVATE" }], hasMore: false }));
  const response = await list(request());
  expect(await response.json()).toEqual({ items: [summary], hasMore: false });
  expect(mocks.fetch.mock.calls[0][0]).toBe(`http://127.0.0.1:8080/api/companies/${company}/knowledge-pages`);
  expect(response.headers.get("cache-control")).toBe("no-store");
});
it("creates, reads, updates and deletes one page", async () => {
  mocks.fetch.mockImplementation(async () => Response.json(page));
  const input = { title: "Khoá sổ", body: "- [ ] Kiểm tra", customerName: "Cty Hoà Phát Đạt" };
  expect((await create(write("POST", input))).status).toBe(200);
  expect(mocks.fetch.mock.calls[0][1].method).toBe("POST"); expect(JSON.parse(mocks.fetch.mock.calls[0][1].body)).toEqual(input);
  expect(await (await read(request(), context)).json()).toEqual(page);
  expect((await update(write("PUT", { ...input, rowVersion: version }), context)).status).toBe(200);
  expect(mocks.fetch.mock.calls[2][0]).toBe(`http://127.0.0.1:8080/api/companies/${company}/knowledge-pages/${id}`);
  mocks.fetch.mockResolvedValue(new Response(null, { status: 204 }));
  const gone = await remove(write("DELETE", undefined, undefined, `companyId=${company}&rowVersion=${encodeURIComponent(version)}`), context);
  expect(gone.status).toBe(204);
  expect(mocks.fetch.mock.calls[3][0]).toBe(`http://127.0.0.1:8080/api/companies/${company}/knowledge-pages/${id}?rowVersion=${encodeURIComponent(version)}`);
});
it("refuses cross-site writes, bad input and stray query keys before calling Core.Api", async () => {
  expect((await create(write("POST", { title: "A", body: "", customerName: null }, "http://evil.test"))).status).toBe(403);
  expect((await create(write("POST", { title: " ", body: "", customerName: null }))).status).toBe(400);
  expect((await create(write("POST", { title: "A", body: "", customerName: null, rowVersion: version }))).status).toBe(400);
  expect((await update(write("PUT", { title: "A", body: "", customerName: null }), context)).status).toBe(400);
  expect((await list(request(`companyId=${company}&x=1`))).status).toBe(400);
  expect((await remove(write("DELETE", undefined, undefined, `companyId=${company}`), context)).status).toBe(400);
  expect(mocks.fetch).not.toHaveBeenCalled();
});
it("maps Core.Api conflicts and rejects malformed answers", async () => {
  mocks.fetch.mockResolvedValue(Response.json({}, { status: 409 }));
  expect((await update(write("PUT", { title: "A", body: "", customerName: null, rowVersion: version }), context)).status).toBe(409);
  mocks.fetch.mockResolvedValue(Response.json({ ...page, id: "77777777-7777-4777-8777-777777777777" }));
  expect((await read(request(), context)).status).toBe(502);
});
