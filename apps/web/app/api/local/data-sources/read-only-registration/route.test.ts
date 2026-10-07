// @vitest-environment node
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { POST } from "./route";
import { GET } from "../registration-options/route";
const session = vi.hoisted(() => ({ token: "issued-token" as string | null }));
vi.mock("next/headers", () => ({ cookies: async () => ({ get: () => session.token ? { value: session.token } : undefined }) }));
const company = "22222222-2222-2222-2222-222222222222";
const body = { bindingId: "33333333-3333-3333-3333-333333333333", bindingVersion: "9223372036854775807",
  operationId: "44444444-4444-4444-4444-444444444444", logicalName: "ERP", environment: "Production", purpose: "Reporting", maxConcurrency: 2 };
const fetcher = vi.fn();
const request = (value: unknown = body, origin = "http://localhost:3000") => new Request(`http://localhost:3000/api/local/data-sources/read-only-registration?companyId=${company}`,
  { method: "POST", headers: { Origin: origin, "Content-Type": "application/json" }, body: JSON.stringify(value) });
beforeEach(() => {
  session.token = "issued-token"; fetcher.mockReset(); vi.stubGlobal("fetch", fetcher);
  vi.stubEnv("AIOFFICE_LOCAL_UI_ENABLED", "true"); vi.stubEnv("AIOFFICE_LOCAL_CORE_API_URL", "http://core:8080");
});
afterEach(() => { vi.unstubAllGlobals(); vi.unstubAllEnvs(); });
it("forwards strict opaque binding and operation IDs with lossless version and no-store session scope", async () => {
  fetcher.mockResolvedValue(Response.json({ id: "created" }, { headers: { "Set-Cookie": "discard" } }));
  const response = await POST(request());
  expect(response.status).toBe(200); expect(response.headers.get("cache-control")).toBe("no-store");
  expect(response.headers.get("set-cookie")).toBeNull();
  const [url, init] = fetcher.mock.calls[0];
  expect(url).toBe("http://core:8080/api/data-sources/read-only-registration");
  expect(JSON.parse(init.body)).toEqual(body);
  expect(init.headers.get("Authorization")).toBe("Bearer issued-token");
  expect(init.headers.get("X-AIOffice-Company-Id")).toBe(company);
});
it.each(["tenantId", "companyId", "userId", "connectionSecretReference", "connectionString", "allowWrite", "kind"])("rejects extra %s before Core", async field => {
  expect((await POST(request({ ...body, [field]: "forbidden" }))).status).toBe(400);
  expect(fetcher).not.toHaveBeenCalled();
});
it.each(["01", "0", "-1", "9223372036854775808", 1])("rejects unsafe version %s", async bindingVersion => {
  expect((await POST(request({ ...body, bindingVersion }))).status).toBe(400); expect(fetcher).not.toHaveBeenCalled();
});
it("enforces same origin and byte bounds before forwarding", async () => {
  expect((await POST(request(body, "https://foreign.invalid"))).status).toBe(403);
  expect((await POST(request({ ...body, purpose: "x".repeat(9000) }))).status).toBe(413);
  expect(fetcher).not.toHaveBeenCalled();
});
it("preserves denial/conflict/unavailability without cookies and fails closed without a session", async () => {
  for (const status of [403, 409, 503]) {
    fetcher.mockResolvedValueOnce(Response.json({}, { status }));
    const result = await POST(request()); expect(result.status).toBe(status); expect(result.headers.get("cache-control")).toBe("no-store");
  }
  session.token = null; expect((await POST(request())).status).toBe(401);
});
it("forwards bounded options pagination and rejects excessive pages", async () => {
  fetcher.mockResolvedValueOnce(Response.json({ items: [], offset: 0, limit: 100, hasMore: false }));
  expect((await GET(new Request(`http://localhost:3000/api/local/data-sources/registration-options?companyId=${company}&limit=100`))).status).toBe(200);
  expect(fetcher.mock.calls[0][0]).toBe("http://core:8080/api/data-sources/registration-options?offset=0&limit=100");
  expect((await GET(new Request(`http://localhost:3000/api/local/data-sources/registration-options?companyId=${company}&limit=101`))).status).toBe(400);
});
