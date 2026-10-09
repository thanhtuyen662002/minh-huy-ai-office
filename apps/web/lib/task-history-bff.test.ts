// @vitest-environment node
import { afterEach, beforeEach, expect, it, vi } from "vitest";
const mocks = vi.hoisted(() => ({ runtime: vi.fn(), cookies: vi.fn(), read: vi.fn(), fetch: vi.fn() }));
vi.mock("./browser-auth-runtime", () => ({ getBrowserAuthRuntime: mocks.runtime }));
vi.mock("next/headers", () => ({ cookies: mocks.cookies }));
import { GET as list } from "../app/api/local/tasks/route";
import { GET as detail } from "../app/api/local/tasks/[taskId]/history/route";
const company = "22222222-2222-2222-2222-222222222222", task = "33333333-3333-3333-3333-333333333333";
const session = { companyId: company, accessToken: "PRIVATE-TOKEN", subject: "PRIVATE-SUBJECT", expiresAt: Math.floor(Date.now() / 1000) + 300 };
const row = { taskId: task, status: 0, createdAtUtc: "2026-10-09T00:00:00Z", updatedAtUtc: "2026-10-09T00:00:00Z", summary: "Owned task", metadataUnavailable: false };
const page = { companyId: company, items: [row], offset: 0, limit: 25, hasMore: false };
const detailPayload = { companyId: company, task: row, result: null, resultUnavailable: false };
const request = (query = `companyId=${company}`) => new Request(`http://internal-web:3000/api/local/tasks?${query}`, { headers: { Authorization: "Bearer FORGED", "X-AIOffice-Company-Id": "FORGED" } });
const invokeDetail = (req = request(), taskId = task) => detail(req, { params: Promise.resolve({ taskId }) });
beforeEach(() => {
  vi.clearAllMocks(); vi.stubEnv("AIOFFICE_BROWSER_OIDC_ENABLED", "true"); vi.stubEnv("AIOFFICE_LOCAL_UI_ENABLED", "false");
  mocks.runtime.mockReturnValue({ settings: { localHttp: false, publicOrigin: "https://office.example.test" }, coreApiOrigin: "http://core.fixture.invalid", sessions: { read: mocks.read } });
  mocks.cookies.mockResolvedValue({ get: (name: string) => ({ value: name.includes("binding") ? "PRIVATE-BINDING" : "PRIVATE-SID" }) });
  mocks.read.mockResolvedValue(session); mocks.fetch.mockResolvedValue(Response.json(page, { headers: { "Set-Cookie": "PRIVATE=discard", "X-PRIVATE": "secret" } }));
  vi.stubGlobal("fetch", mocks.fetch);
});
afterEach(() => { vi.unstubAllGlobals(); vi.unstubAllEnvs(); });
it.each(["list", "detail"])("uses issued SID and fresh final scope for %s through the real helper", async route => {
  if (route === "detail") mocks.fetch.mockResolvedValue(Response.json(detailPayload, { headers: { "Set-Cookie": "PRIVATE=discard", "X-PRIVATE": "secret" } }));
  const response = route === "list" ? await list(request()) : await invokeDetail(); expect(response.status).toBe(200);
  const [url, init] = mocks.fetch.mock.calls[0]; expect(url).toBe(`http://core.fixture.invalid/api/tasks${route === "list" ? "?offset=0&limit=25" : `/${task}/history`}`);
  expect(init.headers.get("Authorization")).toBe("Bearer PRIVATE-TOKEN"); expect(init.headers.get("X-AIOffice-Company-Id")).toBe(company);
  expect(init.method ?? "GET").toBe("GET"); expect(init.cache).toBe("no-store"); expect(init.redirect).toBe("error");
  expect(response.headers.get("Set-Cookie")).toBeNull(); expect(response.headers.get("X-PRIVATE")).toBeNull();
  expect(response.headers.get("Cache-Control")).toBe("no-store"); expect(mocks.read).toHaveBeenCalledTimes(2);
});
it.each(["list", "detail", "detail-task-only"])("preserves the same issued GUID identity for uppercase %s selectors", async route => {
  const companyId = "abcdef01-abcd-abcd-abcd-abcdef012345", taskId = "fedcba98-fedc-fedc-fedc-fedcba987654";
  mocks.read.mockResolvedValue({ ...session, companyId });
  const payload = route === "list" ? { ...page, companyId, items: [{ ...row, taskId }] }
    : { ...detailPayload, companyId, task: { ...row, taskId } };
  mocks.fetch.mockResolvedValue(Response.json(payload));
  const selectedCompany = route === "detail-task-only" ? companyId : companyId.toUpperCase();
  const response = route === "list" ? await list(request(`companyId=${selectedCompany}`))
    : await invokeDetail(request(`companyId=${selectedCompany}`), taskId.toUpperCase());
  expect(response.status).toBe(200); expect(await response.json()).toEqual(payload);
  expect(mocks.fetch.mock.calls[0][0]).toBe(`http://core.fixture.invalid/api/tasks${route === "list" ? "?offset=0&limit=25" : `/${taskId}/history`}`);
  expect(mocks.fetch.mock.calls[0][1].headers.get("X-AIOffice-Company-Id")).toBe(companyId);
  expect(response.headers.get("Cache-Control")).toBe("no-store"); expect(mocks.read).toHaveBeenCalledTimes(2);
});
it.each(["offset=-1", "offset=01", "offset=10001", "limit=0", "limit=101", "offset=0&offset=1", "ownerId=forged", "tenantId=forged", `companyId=${company}`])("rejects ambiguous/invalid list selector %s before authority", async suffix => {
  const response = await list(request(`companyId=${company}&${suffix}`)); expect(response.status).toBe(400);
  expect(response.headers.get("Cache-Control")).toBe("no-store"); expect(mocks.read).not.toHaveBeenCalled(); expect(mocks.fetch).not.toHaveBeenCalled();
});
it.each(["00000000-0000-0000-0000-000000000000", "------------------------------------", "333333333333-3333-3333-3333333333333333"])("rejects noncanonical detail identity %s", async taskId => {
  expect((await invokeDetail(request(), taskId)).status).toBe(400); expect(mocks.fetch).not.toHaveBeenCalled();
});
it("rejects detail owner selectors and lost/mismatched final authority without private bytes", async () => {
  expect((await invokeDetail(request(`companyId=${company}&ownerId=forged`))).status).toBe(400);
  for (const final of [null, { ...session, companyId: "66666666-6666-6666-6666-666666666666" }]) {
    mocks.read.mockReset().mockResolvedValueOnce(session).mockResolvedValueOnce(final);
    mocks.fetch.mockResolvedValue(Response.json({ answer: "PRIVATE_RESULT" }));
    const response = await invokeDetail(); expect(response.status).toBe(401); expect(await response.text()).not.toContain("PRIVATE");
  }
});
it("fences the session after delayed body buffering", async () => {
  let finish!: ReadableStreamDefaultController<Uint8Array>;
  mocks.fetch.mockResolvedValue(new Response(new ReadableStream({ start(controller) { finish = controller; } })));
  const pending = list(request()); await vi.waitFor(() => expect(mocks.fetch).toHaveBeenCalledTimes(1));
  mocks.read.mockResolvedValue(null); finish.enqueue(new TextEncoder().encode('{"private":"PRIVATE_RESULT"}')); finish.close();
  const response = await pending; expect(response.status).toBe(401); expect(await response.text()).not.toContain("PRIVATE");
});
it.each([401, 403, 404, 503])("preserves safe Core status %i without caching", async status => {
  mocks.fetch.mockResolvedValue(Response.json({}, { status })); expect((await list(request())).status).toBe(status);
});
it("fails closed for missing SID, wrong company and coordinator failure", async () => {
  mocks.read.mockResolvedValue(null); expect((await list(request())).status).toBe(401); expect(mocks.fetch).not.toHaveBeenCalled();
  mocks.read.mockResolvedValue(session); expect((await list(request("companyId=66666666-6666-6666-6666-666666666666"))).status).toBe(401);
  mocks.read.mockRejectedValue(new Error("PRIVATE_STORE_DIAGNOSTIC")); const response = await invokeDetail(); expect(response.status).toBe(401); expect(await response.text()).not.toContain("PRIVATE");
});
it("rejects malformed UTF8 with bounded502 and retains valid authority", async () => {
  mocks.fetch.mockResolvedValue(new Response(new Uint8Array([123, 34, 255, 34, 58, 49, 125]), { headers: { "Content-Type": "application/json" } }));
  const response = await list(request()); expect(response.status).toBe(502); expect(await response.text()).not.toContain("PRIVATE"); expect(mocks.read).toHaveBeenCalledTimes(2);
});
it.each([{ ...page, companyId: "66666666-6666-6666-6666-666666666666" }, { ...page, ownerId: "PRIVATE" }, { ...page, offset: 1 }])("rejects successful unscoped/untyped Core JSON %j", async payload => {
  mocks.fetch.mockResolvedValue(Response.json(payload)); const response = await list(request()); expect(response.status).toBe(502); expect(await response.text()).not.toContain("PRIVATE");
});
it("never relays private Core error diagnostics", async () => {
  mocks.fetch.mockResolvedValue(Response.json({ error: "PRIVATE_PROVIDER_DIAGNOSTIC" }, { status: 503 }));
  const response = await invokeDetail(); expect(response.status).toBe(503); expect(await response.text()).not.toContain("PRIVATE");
});
