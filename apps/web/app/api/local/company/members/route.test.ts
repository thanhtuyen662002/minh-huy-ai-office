import { afterEach, beforeEach, expect, it, vi } from "vitest";
const state = vi.hoisted(() => ({ token: "opaque-fixture-token" }));
vi.mock("next/headers", () => ({ cookies: async () => ({ get: () => state.token ? { value: state.token } : undefined }) }));
import { GET } from "./route";
const company = "22222222-2222-2222-2222-222222222222";
beforeEach(() => { vi.stubEnv("AIOFFICE_LOCAL_UI_ENABLED", "true"); vi.stubEnv("AIOFFICE_LOCAL_CORE_API_URL", "http://core.fixture.invalid"); state.token = "opaque-fixture-token"; });
afterEach(() => { vi.unstubAllGlobals(); vi.unstubAllEnvs(); });
const request = (query = "") => new Request(`http://web.fixture.invalid/api/local/company/members?companyId=${company}${query}`);
it("relays only authoritative cookie and selected company and strips upstream cookies", async () => {
  const fetcher = vi.fn(async () => Response.json({ items: [] }, { headers: { "Set-Cookie": "untrusted=1" } })); vi.stubGlobal("fetch", fetcher);
  const response = await GET(request()); const [url, init] = fetcher.mock.calls[0] as unknown as [string, RequestInit];
  expect(url).toBe("http://core.fixture.invalid/api/company/members?offset=0&limit=25");
  const headers = new Headers(init.headers); expect(headers.get("Authorization")).toBe("Bearer opaque-fixture-token"); expect(headers.get("X-AIOffice-Company-Id")).toBe(company);
  expect(response.headers.get("Cache-Control")).toBe("no-store"); expect(response.headers.get("Set-Cookie")).toBeNull();
});
it.each(["&offset=-1", "&offset=1001", "&limit=0", "&limit=101", "&offset=01", "&offset=0&offset=1", "&tenantId=forged"])("rejects invalid selection %s before proxying", async query => {
  const fetcher = vi.fn(); vi.stubGlobal("fetch", fetcher); const response = await GET(request(query)); expect(response.status).toBe(400); expect(response.headers.get("Cache-Control")).toBe("no-store"); expect(fetcher).not.toHaveBeenCalled();
});
it.each([401, 403, 503])("preserves server denial %i without caching", async status => {
  vi.stubGlobal("fetch", vi.fn(async () => Response.json({}, { status }))); const response = await GET(request()); expect(response.status).toBe(status); expect(response.headers.get("Cache-Control")).toBe("no-store");
});
it("requires a session cookie", async () => { state.token = ""; const fetcher = vi.fn(); vi.stubGlobal("fetch", fetcher); expect((await GET(request())).status).toBe(401); expect(fetcher).not.toHaveBeenCalled(); });
it("fails closed when local UI is disabled", async () => { vi.stubEnv("AIOFFICE_LOCAL_UI_ENABLED", "false"); expect((await GET(request())).status).toBe(503); });
