// @vitest-environment node
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { PUT } from "./route";
const session = vi.hoisted(() => ({ token: "synthetic-issued-session" as string | null }));
vi.mock("next/headers", () => ({ cookies: async () => ({ get: () => session.token ? { value: session.token } : undefined }) }));
const company = "22222222-2222-2222-2222-222222222222";
const source = "33333333-3333-3333-3333-333333333333";
const origin = "http://localhost:3000";
const metadata = { logicalName: "renamed", purpose: "synthetic", maxConcurrency: 3, isEnabled: false };
const upstream = vi.fn();
const invoke = (requestOrigin = origin, id = source) => PUT(new Request(`${origin}/api/local/data-sources/${id}/metadata?companyId=${company}`, {
  method: "PUT", headers: { Origin: requestOrigin, "Content-Type": "application/json" }, body: JSON.stringify(metadata),
}), { params: Promise.resolve({ dataSourceId: id }) });
beforeEach(() => {
  session.token = "synthetic-issued-session"; upstream.mockReset(); vi.stubGlobal("fetch", upstream);
  vi.stubEnv("AIOFFICE_LOCAL_UI_ENABLED", "true"); vi.stubEnv("AIOFFICE_LOCAL_CORE_API_URL", "http://127.0.0.1:8080");
});
afterEach(() => { vi.unstubAllGlobals(); vi.unstubAllEnvs(); });
it("forwards only the supplied metadata to the narrow Core endpoint with cookie authority and company scope", async () => {
  upstream.mockResolvedValue(Response.json({ id: source, ...metadata }, { headers: { "Set-Cookie": "synthetic-upstream-cookie=discard" } }));
  const response = await invoke(); expect(response.status).toBe(200); expect(response.headers.get("set-cookie")).toBeNull(); expect(response.headers.get("cache-control")).toBe("no-store");
  const [url, options] = upstream.mock.calls[0]; expect(url).toBe(`http://127.0.0.1:8080/api/data-sources/${source}/metadata`);
  expect(options.method).toBe("PUT"); expect(options.headers.get("X-AIOffice-Company-Id")).toBe(company); expect(options.headers.get("Authorization")).toBe("Bearer synthetic-issued-session"); expect(JSON.parse(options.body)).toEqual(metadata);
});
it.each([400, 403, 404, 409])("preserves authoritative Core HTTP %s without inventing successful updates", async status => {
  upstream.mockResolvedValue(Response.json({ error: "synthetic denial" }, { status })); const response = await invoke(); expect(response.status).toBe(status); expect(response.headers.get("cache-control")).toBe("no-store");
});
it("rejects a foreign origin before any upstream request", async () => { expect((await invoke("https://foreign.invalid")).status).toBe(403); expect(upstream).not.toHaveBeenCalled(); });
it("rejects a malformed id before any upstream request", async () => { expect((await invoke(origin, "not-a-guid")).status).toBe(400); expect(upstream).not.toHaveBeenCalled(); });
it("fails closed without an issued cookie", async () => { session.token = null; expect((await invoke()).status).toBe(401); expect(upstream).not.toHaveBeenCalled(); });
it("reports unavailable Core without returning cookie authority", async () => { upstream.mockRejectedValue(new Error("synthetic offline")); const response = await invoke(); expect(response.status).toBe(502); expect(await response.text()).not.toContain("synthetic-issued-session"); });
