// @vitest-environment node
import { afterEach, beforeEach, expect, it, vi } from "vitest";
const session = vi.hoisted(() => ({ token: "issued-cookie-token" as string | null }));
vi.mock("next/headers", () => ({ cookies: async () => ({ get: () => session.token ? { value: session.token } : undefined }) }));
import { POST } from "./route";
const company = "22222222-2222-2222-2222-222222222222", user = "33333333-3333-3333-3333-333333333333";
const origin = "http://localhost:3000", fetcher = vi.fn();
const input = { operationId: "44444444-4444-4444-4444-444444444444", expectedVersion: "7", isActive: false };
const invoke = (request: Request, userId = user) => POST(request, { params: Promise.resolve({ userId }) });
const request = (body = JSON.stringify(input), suffix = "", requestOrigin: string | null = origin) => new Request(`${origin}/api/local/company/members/${user}/access?companyId=${company}${suffix}`, {
  method: "POST", body, headers: { "Content-Type": "application/json", ...(requestOrigin === null ? {} : { Origin: requestOrigin }), Authorization: "Bearer FORGED", "X-AIOffice-Company-Id": "FORGED" },
});
beforeEach(() => { session.token = "issued-cookie-token"; fetcher.mockReset(); vi.stubGlobal("fetch", fetcher); vi.stubEnv("AIOFFICE_LOCAL_UI_ENABLED", "true"); vi.stubEnv("AIOFFICE_BROWSER_OIDC_ENABLED", "false"); vi.stubEnv("AIOFFICE_LOCAL_CORE_API_URL", "http://core.fixture.invalid"); });
afterEach(() => { vi.unstubAllGlobals(); vi.unstubAllEnvs(); vi.useRealTimers(); });
it("relays only issued authority, selected company and the bounded operation; strips upstream cookies", async () => {
  fetcher.mockResolvedValue(Response.json({ ok: true }, { headers: { "Set-Cookie": "PRIVATE=discard" } }));
  const response = await invoke(request()); expect(response.status).toBe(200); expect(response.headers.get("Cache-Control")).toBe("no-store"); expect(response.headers.get("Set-Cookie")).toBeNull();
  expect(fetcher).toHaveBeenCalledOnce(); const [url, init] = fetcher.mock.calls[0];
  expect(url).toBe(`http://core.fixture.invalid/api/company/members/${user}/access`); expect(init.method).toBe("POST"); expect(init.cache).toBe("no-store");
  expect(init.headers.get("Authorization")).toBe("Bearer issued-cookie-token"); expect(init.headers.get("X-AIOffice-Company-Id")).toBe(company); expect(JSON.parse(init.body)).toEqual(input);
});
it.each([null, "null", "https://foreign.invalid", origin + "/", "http://localhost:3001"])("rejects foreign/missing origin %j before consuming a body or proxying", async requestOrigin => {
  const response = await invoke(request("{", "", requestOrigin)); expect(response.status).toBe(403); expect(fetcher).not.toHaveBeenCalled(); expect(response.headers.get("Cache-Control")).toBe("no-store");
});
it.each(["&companyId=" + company, "&tenantId=forged", "&role=admin"])("rejects ambiguous/injected query %s", async suffix => { expect((await invoke(request(undefined, suffix))).status).toBe(400); expect(fetcher).not.toHaveBeenCalled(); });
it.each(["../foreign", "00000000-0000-0000-0000-000000000000", "bad"])("rejects invalid target %s before constructing the upstream path", async target => { expect((await invoke(request(), target)).status).toBe(400); expect(fetcher).not.toHaveBeenCalled(); });
it.each(["{", "null", "[]", JSON.stringify({ ...input, companyId: company }), JSON.stringify({ ...input, roles: ["admin"] }), JSON.stringify({ ...input, expectedVersion: "01" }), JSON.stringify({ ...input, isActive: "false" })])("rejects malformed input %s", async body => { expect((await invoke(request(body))).status).toBe(400); expect(fetcher).not.toHaveBeenCalled(); });
it("bounds the actual body bytes without trusting a small Content-Length", async () => {
  const req = request(JSON.stringify({ ...input, extra: "á".repeat(1100) })); req.headers.set("Content-Length", "1");
  const response = await invoke(req); expect(response.status).toBe(413); expect(fetcher).not.toHaveBeenCalled();
});
it("bounds a stalled body and releases its reader without proxying", async () => {
  vi.useFakeTimers(); const stream = new ReadableStream<Uint8Array>({ start() {} });
  const req = new Request(`${origin}/api/local/company/members/${user}/access?companyId=${company}`, { method: "POST", body: stream, headers: { Origin: origin, "Content-Type": "application/json" }, duplex: "half" } as RequestInit);
  const pending = invoke(req); await vi.advanceTimersByTimeAsync(10_001); expect((await pending).status).toBe(408); expect(stream.locked).toBe(false); expect(fetcher).not.toHaveBeenCalled();
});
it.each([401, 403, 404, 409, 503])("preserves fresh upstream denial %i without caching or cookies", async status => {
  fetcher.mockResolvedValue(Response.json({ code: "stale-version" }, { status, headers: { "Set-Cookie": "PRIVATE=discard" } })); const response = await invoke(request()); expect(response.status).toBe(status); expect(response.headers.get("Cache-Control")).toBe("no-store"); expect(response.headers.get("Set-Cookie")).toBeNull();
});
it("requires the issued session and respects disabled UI", async () => {
  session.token = null; expect((await invoke(request())).status).toBe(401); vi.stubEnv("AIOFFICE_LOCAL_UI_ENABLED", "false"); expect((await invoke(request())).status).toBe(503); expect(fetcher).not.toHaveBeenCalled();
});
it("masks transport details", async () => { fetcher.mockRejectedValue(new Error("PRIVATE transport")); const response = await invoke(request()); expect(response.status).toBe(502); expect(await response.text()).not.toContain("PRIVATE"); });
