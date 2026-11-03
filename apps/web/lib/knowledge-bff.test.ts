// @vitest-environment node
import { afterEach, beforeEach, expect, it, vi } from "vitest";
const mocks = vi.hoisted(() => ({ cookies: vi.fn(), runtime: vi.fn(), read: vi.fn(), fetch: vi.fn() }));
vi.mock("next/headers", () => ({ cookies: mocks.cookies }));
vi.mock("./browser-auth-runtime", () => ({ getBrowserAuthRuntime: mocks.runtime }));
import { GET as list, POST as create } from "../app/api/local/knowledge-pages/route";
import { DELETE as remove, GET as read, PUT as update } from "../app/api/local/knowledge-pages/[id]/route";
import { POST as extract } from "../app/api/local/knowledge-pages/extract/route";
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

const upload = (name = "Hướng dẫn.pdf", body: BodyInit = new Uint8Array([37, 80, 68, 70]), origin = "http://web.test", type = "application/octet-stream") =>
  new Request(`http://web.test/api/local/knowledge-pages/extract?companyId=${company}&fileName=${encodeURIComponent(name)}`,
    { method: "POST", headers: { "Content-Type": type, Origin: origin }, body });
it("forwards an uploaded file to Core.Api and returns only the parsed text parts", async () => {
  mocks.fetch.mockResolvedValue(Response.json({ title: "Hướng dẫn", parts: ["# Khoá sổ", "phần 2"], truncated: false, extra: 1 }, { status: 200 }));
  mocks.fetch.mockResolvedValueOnce(Response.json({ title: "Hướng dẫn", parts: ["# Khoá sổ", "phần 2"], truncated: false }));
  const response = await extract(upload());
  expect(await response.json()).toEqual({ title: "Hướng dẫn", parts: ["# Khoá sổ", "phần 2"], truncated: false });
  const [url, init] = mocks.fetch.mock.calls[0];
  expect(url).toBe(`http://127.0.0.1:8080/api/companies/${company}/knowledge-pages/extract?fileName=H%C6%B0%E1%BB%9Bng%20d%E1%BA%ABn.pdf`);
  expect(init.method).toBe("POST");
  expect(new Headers(init.headers).get("content-type")).toBe("application/octet-stream");
  expect([...init.body as Uint8Array]).toEqual([37, 80, 68, 70]);
  expect((await extract(upload())).status).toBe(502);
});
it("passes a known unreadable reason through and refuses bad uploads before calling Core.Api", async () => {
  mocks.fetch.mockResolvedValueOnce(Response.json({ error: "x", reason: "legacy" }, { status: 422 }));
  const legacy = await extract(upload("cu.doc"));
  expect(legacy.status).toBe(422); expect((await legacy.json()).reason).toBe("legacy");
  mocks.fetch.mockResolvedValueOnce(Response.json({ error: "x", reason: "<script>" }, { status: 422 }));
  expect((await (await extract(upload())).json()).reason).toBe("damaged");
  mocks.fetch.mockClear();
  expect((await extract(upload("a.pdf", undefined, "http://evil.test"))).status).toBe(403);
  expect((await extract(upload("a.pdf", undefined, undefined, "application/json"))).status).toBe(400);
  expect((await extract(upload("bad\nname.pdf"))).status).toBe(400);
  expect((await extract(upload("x".repeat(256)))).status).toBe(400);
  const empty = await extract(upload("a.txt", new Uint8Array(0)));
  expect(empty.status).toBe(422); expect((await empty.json()).reason).toBe("empty");
  const big = await extract(upload("a.txt", new Uint8Array(20 * 1024 * 1024 + 1)));
  expect(big.status).toBe(422); expect((await big.json()).reason).toBe("tooLarge");
  expect(mocks.fetch).not.toHaveBeenCalled();
});
it("accepts long pages up to the new limit", async () => {
  const body = "Dòng tiếng Việt có dấu\n".repeat(20_000);
  mocks.fetch.mockImplementation(async () => Response.json({ ...page, body }));
  const response = await create(write("POST", { title: "Dài", body, customerName: null }));
  expect(response.status).toBe(200);
  expect((await response.json()).body).toBe(body);
  expect(JSON.parse(mocks.fetch.mock.calls[0][1].body).body).toBe(body);
});
