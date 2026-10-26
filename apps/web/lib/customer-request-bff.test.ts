// @vitest-environment node
import { afterEach, beforeEach, expect, it, vi } from "vitest";
const mocks = vi.hoisted(() => ({ cookies: vi.fn(), runtime: vi.fn(), read: vi.fn(), fetch: vi.fn() }));
vi.mock("next/headers", () => ({ cookies: mocks.cookies }));
vi.mock("./browser-auth-runtime", () => ({ getBrowserAuthRuntime: mocks.runtime }));
import { GET as list } from "../app/api/local/customer-requests/route";
import { GET as detail, PATCH as patch } from "../app/api/local/customer-requests/[code]/route";
import { requestCompany as company, requestDetail, requestTicket, assigneeUser } from "./customer-request-test-fixture";

const context = { params: Promise.resolve({ code: requestTicket.code }) };
const session = { companyId: company, accessToken: "PRIVATE-TOKEN" };
const request = (query = `companyId=${company}`, init?: RequestInit) => new Request(`http://web.test/api/local/customer-requests?${query}`, init);
const page = { items: [requestTicket], offset: 0, limit: 25, hasMore: false };
beforeEach(() => {
  vi.clearAllMocks(); vi.stubEnv("AIOFFICE_BROWSER_OIDC_ENABLED", "false"); vi.stubEnv("AIOFFICE_LOCAL_UI_ENABLED", "true");
  mocks.cookies.mockResolvedValue({ get: () => ({ value: "PRIVATE-TOKEN" }) });
  mocks.read.mockResolvedValue(session);
  mocks.runtime.mockReturnValue({ coreApiOrigin: "http://core.test", settings: {}, sessions: { read: mocks.read } });
  vi.stubGlobal("fetch", mocks.fetch);
});
afterEach(() => { vi.unstubAllGlobals(); vi.unstubAllEnvs(); });
const mutation = (value: unknown, origin = "http://web.test") => request(undefined, { method: "PATCH", headers: { "Content-Type": "application/json", Origin: origin }, body: JSON.stringify(value) });

it("proxies selected-company list filters without adding a company fallback", async () => {
  mocks.fetch.mockResolvedValue(Response.json(page));
  const response = await list(request(`companyId=${company}&status=needs_clarification&customer=Minh+%26+An&offset=0&limit=25`));
  expect(response.status).toBe(200); expect(await response.json()).toEqual(page);
  const [url, init] = mocks.fetch.mock.calls[0];
  expect(url).toBe(`http://127.0.0.1:8080/api/companies/${company}/customer-requests?offset=0&limit=25&status=needs_clarification&customer=Minh+%26+An`);
  expect(init.headers.get("X-AIOffice-Company-Id")).toBe(company); expect(init.headers.get("Authorization")).toBe("Bearer PRIVATE-TOKEN");
  expect(init.cache).toBe("no-store"); expect(response.headers.get("cache-control")).toBe("no-store");
});
it("proxies detail by code and projects only public contract fields", async () => {
  mocks.fetch.mockResolvedValue(Response.json({ ...requestDetail, secretRef: "PRIVATE", request: { ...requestTicket, privateField: "PRIVATE" } },
    { headers: { "Set-Cookie": "PRIVATE", "X-Secret": "PRIVATE" } }));
  const response = await detail(request(), context);
  expect(await response.json()).toEqual(requestDetail);
  expect(response.headers.get("set-cookie")).toBeNull(); expect(response.headers.get("x-secret")).toBeNull();
  expect(mocks.fetch.mock.calls[0][0]).toBe(`http://127.0.0.1:8080/api/companies/${company}/customer-requests/YC-0001`);
  expect(mocks.fetch.mock.calls[0][1].method).toBeUndefined();
});
it.each([{ status: "in_progress" }, { assigneeUserId: assigneeUser }, { assigneeUserId: null }])("preserves patch presence semantics %j with rowVersion", async fields => {
  mocks.fetch.mockResolvedValue(Response.json(requestTicket));
  const input = { rowVersion: requestTicket.rowVersion, ...fields };
  expect((await patch(mutation(input), context)).status).toBe(200);
  expect(mocks.fetch.mock.calls[0][1].method).toBe("PATCH");
  expect(JSON.parse(mocks.fetch.mock.calls[0][1].body)).toEqual(input);
});
it.each([400, 401, 403, 404, 409, 500, 503])("preserves upstream status %i and scrubs private diagnostics", async status => {
  mocks.fetch.mockResolvedValue(new Response("PRIVATE-SQL-TRACE", { status }));
  const response = await patch(mutation({ rowVersion: requestTicket.rowVersion, status: "done" }), context);
  expect(response.status).toBe(status === 500 ? 502 : status); expect(await response.text()).not.toContain("PRIVATE");
  expect(response.headers.get("cache-control")).toBe("no-store");
});
it.each(["", "companyId=invalid", `companyId=${company}&companyId=${company}`, `companyId=${company}&status=invalid`,
  `companyId=${company}&limit=101`, `companyId=${company}&limit=0`, `companyId=${company}&offset=100001`,
  `companyId=${company}&offset=01`, `companyId=${company}&customer=`, `companyId=${company}&tenantId=forged`,
])("rejects invalid selectors %s before forwarding", async query => {
  expect((await list(request(query))).status).toBe(400); expect(mocks.fetch).not.toHaveBeenCalled();
});
it("rejects disabled, unauthenticated and foreign browser company sessions", async () => {
  vi.stubEnv("AIOFFICE_LOCAL_UI_ENABLED", "false"); expect((await list(request())).status).toBe(503);
  vi.stubEnv("AIOFFICE_LOCAL_UI_ENABLED", "true"); mocks.cookies.mockResolvedValue({ get: () => undefined });
  expect((await list(request())).status).toBe(401);
  vi.stubEnv("AIOFFICE_BROWSER_OIDC_ENABLED", "true"); mocks.cookies.mockResolvedValue({ get: () => ({ value: "PRIVATE" }) });
  mocks.read.mockResolvedValue({ ...session, companyId: assigneeUser });
  expect((await list(request())).status).toBe(401); expect(mocks.fetch).not.toHaveBeenCalled();
});
it("rejects cross-origin mutation, invalid path codes and detail query injection", async () => {
  expect((await patch(mutation({ rowVersion: requestTicket.rowVersion, status: "done" }, "http://evil.test"), context)).status).toBe(403);
  expect((await detail(request(), { params: Promise.resolve({ code: "../members" }) })).status).toBe(400);
  expect((await detail(request(), { params: Promise.resolve({ code: ".." }) })).status).toBe(400);
  expect((await detail(request(`companyId=${company}&status=new`), context)).status).toBe(400);
  expect(mocks.fetch).not.toHaveBeenCalled();
});
it.each([{}, { rowVersion: requestTicket.rowVersion }, { rowVersion: "bad", status: "done" },
  { rowVersion: requestTicket.rowVersion, status: null }, { rowVersion: requestTicket.rowVersion, status: "approved" },
  { rowVersion: requestTicket.rowVersion, assigneeUserId: "bad" }, { rowVersion: requestTicket.rowVersion, status: "done", hasAssignee: true },
])("refuses invalid patches %j", async input => {
  expect((await patch(mutation(input), context)).status).toBe(400); expect(mocks.fetch).not.toHaveBeenCalled();
});
it("bounds actual patch bytes and rejects duplicate properties", async () => {
  const init = { method: "PATCH", headers: { Origin: "http://web.test", "Content-Type": "application/json" } };
  expect((await patch(request(undefined, { ...init, body: " ".repeat(2049) }), context)).status).toBe(413);
  expect((await patch(request(undefined, { ...init, body: '{"rowVersion":"AAAAAAAAAAE=","status":"new","status":"done"}' }), context)).status).toBe(400);
  expect(mocks.fetch).not.toHaveBeenCalled();
});
it("fences detail body release after logout or company replacement in browser mode", async () => {
  vi.stubEnv("AIOFFICE_BROWSER_OIDC_ENABLED", "true");
  for (const lost of [null, { ...session, companyId: assigneeUser }]) {
    mocks.read.mockReset().mockResolvedValueOnce(session).mockResolvedValueOnce(session).mockResolvedValueOnce(lost);
    mocks.fetch.mockResolvedValue(Response.json(requestDetail));
    const response = await detail(request(), context); expect(response.status).toBe(401); expect(await response.text()).not.toContain(requestTicket.summary);
  }
});
it("rejects malformed projections, mismatched code, wrong paging, and out-of-order results", async () => {
  for (const bad of [{ ...requestDetail, request: { ...requestTicket, code: "YC-0002" } },
    { ...requestDetail, request: { ...requestTicket, rowVersion: "bad" } }, { ...requestDetail, items: [] },
    { ...requestDetail, evidence: [{ ...requestDetail.evidence[0], senderName: null }] }]) {
    mocks.fetch.mockResolvedValue(Response.json(bad)); expect((await detail(request(), context)).status).toBe(502);
  }
  for (const bad of [{ ...page, offset: 25 }, { ...page, hasMore: true }, { ...page, items: [requestTicket, requestTicket] },
    { ...page, items: [requestTicket, { ...requestTicket, id: assigneeUser, code: "YC-0002", createdAt: "2026-10-27T00:00:00Z" }] }]) {
    mocks.fetch.mockResolvedValue(Response.json(bad)); expect((await list(request())).status).toBe(502);
  }
});
it("bounds a stalled Core request on caller cancellation", async () => {
  mocks.fetch.mockReturnValue(new Promise(() => {}));
  const abort = new AbortController(), pending = list(request(undefined, { signal: abort.signal }));
  await vi.waitFor(() => expect(mocks.fetch).toHaveBeenCalled()); abort.abort();
  expect((await pending).status).toBe(503);
});
