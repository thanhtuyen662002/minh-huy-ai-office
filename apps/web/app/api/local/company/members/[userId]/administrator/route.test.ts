// @vitest-environment node
import { afterEach, beforeEach, expect, it, vi } from "vitest";
const session = vi.hoisted(() => ({ token: "issued-cookie-token" as string | null }));
vi.mock("next/headers", () => ({ cookies: async () => ({ get: () => session.token ? { value: session.token } : undefined }) }));
import { POST } from "./route";
const company = "22222222-2222-2222-2222-222222222222", user = "33333333-3333-3333-3333-333333333333";
const origin = "http://localhost:3000", fetcher = vi.fn();
const input = { operationId: "44444444-4444-4444-4444-444444444444", expectedVersion: "7", isAdministrator: true };
const result = { companyId: company, userId: user, operationId: input.operationId, membershipVersion: "8", isAdministrator: true };
const invoke = (req: Request, userId = user) => POST(req, { params: Promise.resolve({ userId }) });
const request = (body = JSON.stringify(input), suffix = "", requestOrigin: string | null = origin) => new Request(`${origin}/api/local/company/members/${user}/administrator?companyId=${company}${suffix}`, {
  method: "POST", body, headers: { "Content-Type": "application/json", ...(requestOrigin === null ? {} : { Origin: requestOrigin }), Authorization: "Bearer FORGED", "X-AIOffice-Company-Id": "FORGED" },
});
beforeEach(() => { session.token = "issued-cookie-token"; fetcher.mockReset(); vi.stubGlobal("fetch", fetcher); vi.stubEnv("AIOFFICE_LOCAL_UI_ENABLED", "true"); vi.stubEnv("AIOFFICE_BROWSER_OIDC_ENABLED", "false"); vi.stubEnv("AIOFFICE_LOCAL_CORE_API_URL", "http://core.fixture.invalid"); });
afterEach(() => { vi.unstubAllGlobals(); vi.unstubAllEnvs(); vi.useRealTimers(); });

it("uses issued authority and exact operation while discarding upstream cookies", async () => {
  fetcher.mockResolvedValue(Response.json(result, { headers: { "Set-Cookie": "PRIVATE=discard" } }));
  const response = await invoke(request()); expect(response.status).toBe(200); expect(await response.json()).toEqual(result);
  expect(response.headers.get("Cache-Control")).toBe("no-store"); expect(response.headers.get("Set-Cookie")).toBeNull();
  const [url, init] = fetcher.mock.calls[0]; expect(url).toBe(`http://core.fixture.invalid/api/company/members/${user}/administrator`);
  expect(init.method).toBe("POST"); expect(init.cache).toBe("no-store"); expect(init.headers.get("Authorization")).toBe("Bearer issued-cookie-token");
  expect(init.headers.get("X-AIOffice-Company-Id")).toBe(company); expect(JSON.parse(init.body)).toEqual(input); expect(init.signal).toBeInstanceOf(AbortSignal);
});
it.each([null, "null", "https://foreign.invalid", origin + "/", "http://localhost:3001"])("refuses bad origin %j before body/authority", async requestOrigin => {
  const response = await invoke(request("{", "", requestOrigin)); expect(response.status).toBe(403); expect(fetcher).not.toHaveBeenCalled();
});
it.each(["&companyId=" + company, "&tenantId=forged", "&role=admin"])("refuses injected query %s", async suffix => {
  expect((await invoke(request(undefined, suffix))).status).toBe(400); expect(fetcher).not.toHaveBeenCalled();
});
it.each(["../foreign", "00000000-0000-0000-0000-000000000000", "bad"])("refuses invalid target %s", async target => {
  expect((await invoke(request(), target)).status).toBe(400); expect(fetcher).not.toHaveBeenCalled();
});
it.each(["{", "null", "[]", JSON.stringify({ ...input, companyId: company }), JSON.stringify({ ...input, roles: ["admin"] }), JSON.stringify({ ...input, expectedVersion: "01" }), JSON.stringify({ ...input, isAdministrator: "true" }),
  JSON.stringify(input).replace('"isAdministrator":true', '"isAdministrator":false,"isAdministrator":true'),
  JSON.stringify(input).replace('"isAdministrator":true', '"isAdministrator":false,"is\\u0041dministrator":true')])("refuses malformed/duplicate request %s", async body => {
  expect((await invoke(request(body))).status).toBe(400); expect(fetcher).not.toHaveBeenCalled();
});
it("bounds actual request bytes with a forged short length", async () => {
  const req = request(JSON.stringify({ ...input, extra: "á".repeat(1100) })); req.headers.set("Content-Length", "1");
  expect((await invoke(req)).status).toBe(413); expect(fetcher).not.toHaveBeenCalled();
});
it.each([400, 401, 403, 404, 500, 503])("bounds denial %i and drops private diagnostics", async status => {
  fetcher.mockResolvedValue(Response.json({ error: "PRIVATE SQL token" }, { status, headers: { "Set-Cookie": "PRIVATE=discard" } }));
  const response = await invoke(request()); expect(response.status).toBe(status >= 500 ? 503 : status);
  expect(await response.text()).not.toContain("PRIVATE"); expect(response.headers.get("Set-Cookie")).toBeNull(); expect(response.headers.get("Cache-Control")).toBe("no-store");
});
it.each(["stale-version", "PRIVATE detail", "constructor", "__proto__"])("409 exposes only a known bounded code %s", async code => {
  fetcher.mockResolvedValue(Response.json({ code, error: "PRIVATE diagnostic", token: "PRIVATE credential" }, { status: 409 }));
  const response = await invoke(request()); expect(response.status).toBe(409); const body = await response.json();
  expect(body.code).toBe(code === "stale-version" ? code : undefined); expect(JSON.stringify(body)).not.toContain("PRIVATE");
});
it.each([{ ...result, companyId: "55555555-5555-5555-5555-555555555555" }, { ...result, userId: "55555555-5555-5555-5555-555555555555" },
  { ...result, operationId: "55555555-5555-5555-5555-555555555555" }, { ...result, isAdministrator: false }, { ...result, membershipVersion: "01" }, { ...result, token: "PRIVATE" }])("refuses mismatched or extra successful authority %j", async body => {
  fetcher.mockResolvedValue(Response.json(body)); const response = await invoke(request()); expect(response.status).toBe(503); expect(await response.text()).not.toContain("PRIVATE");
});
it("refuses invalid UTF8, escaped duplicate result and overlarge response without repair", async () => {
  for (const response of [new Response(new Uint8Array([123, 34, 255, 34, 58, 49, 125]), { headers: { "Content-Type": "application/json" } }),
    new Response(JSON.stringify(result).replace('"isAdministrator":true', '"isAdministrator":false,"isAdministrator":true'), { headers: { "Content-Type": "application/json" } }),
    Response.json({ ...result, extra: "á".repeat(2200) })]) {
    fetcher.mockResolvedValueOnce(response); expect((await invoke(request())).status).toBe(503);
  }
});
it("ends a stalled upstream body and releases its reader", async () => {
  vi.useFakeTimers(); const stream = new ReadableStream<Uint8Array>({ start() {} });
  fetcher.mockResolvedValue(new Response(stream, { headers: { "Content-Type": "application/json" } }));
  const pending = invoke(request()); await vi.advanceTimersByTimeAsync(10_001);
  expect((await pending).status).toBe(503); expect(stream.locked).toBe(false);
});
it("requires issued session and honors disabled UI", async () => {
  session.token = null; expect((await invoke(request())).status).toBe(401); vi.stubEnv("AIOFFICE_LOCAL_UI_ENABLED", "false"); expect((await invoke(request())).status).toBe(503); expect(fetcher).not.toHaveBeenCalled();
});
it("does not expose transport secrets", async () => {
  fetcher.mockRejectedValue(new Error("PRIVATE transport")); const response = await invoke(request()); expect(response.status).toBe(503); expect(await response.text()).not.toContain("PRIVATE");
});
