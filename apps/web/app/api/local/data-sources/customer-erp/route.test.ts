// @vitest-environment node
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { GET, POST } from "./route";
import { POST as testConnection } from "../connection-test/route";
import { POST as createReader } from "../reader-login/route";
import { PUT } from "../[dataSourceId]/credentials/route";
const session = vi.hoisted(() => ({ token: "synthetic-issued" as string | null }));
vi.mock("next/headers", () => ({ cookies: async () => ({ get: () => session.token ? { value: session.token } : undefined }) }));
const company = "22222222-2222-2222-2222-222222222222", id = "33333333-3333-3333-3333-333333333333";
const fields = { host: "sql.example.test", port: 1433, database: "Demo", userName: "reader", password: "synthetic-only", encrypt: true, trustServerCertificate: false };
const body = { ...fields, operationId: "44444444-4444-4444-4444-444444444444", logicalName: "Customer A" };
const fetcher = vi.fn();
const request = (value: unknown = body, origin = "http://localhost:3000") => new Request(`http://localhost:3000/api/local/data-sources/customer-erp?companyId=${company}`,
  { method: "POST", headers: { Origin: origin, "Content-Type": "application/json" }, body: JSON.stringify(value) });
beforeEach(() => {
  session.token = "synthetic-issued"; fetcher.mockReset(); vi.stubGlobal("fetch", fetcher);
  vi.stubEnv("AIOFFICE_LOCAL_UI_ENABLED", "true"); vi.stubEnv("AIOFFICE_LOCAL_CORE_API_URL", "http://core:8080");
});
afterEach(() => { vi.unstubAllGlobals(); vi.unstubAllEnvs(); });
it("forwards structured fields only with server session company authority and no-store", async () => {
  fetcher.mockResolvedValue(Response.json({ id }, { headers: { "Set-Cookie": "discard" } }));
  const response = await POST(request());
  expect(response.headers.get("cache-control")).toBe("no-store"); expect(response.headers.get("set-cookie")).toBeNull();
  const [url, init] = fetcher.mock.calls[0];
  expect(url).toBe("http://core:8080/api/data-sources/customer-erp"); expect(JSON.parse(init.body)).toEqual(body);
  expect(init.headers.get("Authorization")).toBe("Bearer synthetic-issued"); expect(init.headers.get("X-AIOffice-Company-Id")).toBe(company);
});
it("uses explicit SQL test and rotation paths and methods", async () => {
  fetcher.mockImplementation(async () => Response.json({ code: "ok" }));
  expect((await testConnection(request(fields))).status).toBe(200);
  expect(fetcher.mock.calls[0][0]).toBe("http://core:8080/api/data-sources/connection-test");
  expect((await PUT(request({ ...body, bindingVersion: "1" }), { params: Promise.resolve({ dataSourceId: id }) })).status).toBe(200);
  expect(fetcher.mock.calls[1][0]).toBe(`http://core:8080/api/data-sources/${id}/credentials`);
  expect(fetcher.mock.calls[1][1].method).toBe("PUT");
});
it.each(["tenantId", "companyId", "connectionString", "allowWrite", "connectionSecretReference"])("refuses extra %s without forwarding", async field => {
  expect((await POST(request({ ...body, [field]: "forbidden" }))).status).toBe(400); expect(fetcher).not.toHaveBeenCalled();
});
it("enforces same-origin, session, field and actual byte bounds", async () => {
  expect((await POST(request(body, "https://foreign.invalid"))).status).toBe(403);
  expect((await POST(request({ ...body, password: "x".repeat(17000) }))).status).toBe(413);
  expect((await testConnection(request({ ...fields, host: "host;User ID=sa" }))).status).toBe(400);
  session.token = null; expect((await POST(request())).status).toBe(401);
  expect(fetcher).not.toHaveBeenCalled();
});
it("forwards a scoped list and preserves rate limits and verification errors", async () => {
  fetcher.mockResolvedValueOnce(Response.json([]));
  expect((await GET(request())).status).toBe(200);
  for (const status of [403, 409, 422, 429, 503]) {
    fetcher.mockResolvedValueOnce(Response.json({ code: "not_read_only" }, { status }));
    const result = await POST(request()); expect(result.status).toBe(status); expect(result.headers.get("cache-control")).toBe("no-store");
  }
});
it("forwards a reader-login request with admin fields once and copies the new reader back", async () => {
  fetcher.mockResolvedValueOnce(Response.json({ code: "created", userName: "aioffice_reader_0a1b2c3d", password: "synthetic-reader" }));
  const response = await createReader(request({ ...fields, userName: "sa" }));
  expect(response.status).toBe(200); expect(response.headers.get("cache-control")).toBe("no-store");
  expect(await response.json()).toEqual({ code: "created", userName: "aioffice_reader_0a1b2c3d", password: "synthetic-reader" });
  expect(fetcher.mock.calls[0][0]).toBe("http://core:8080/api/data-sources/reader-login");
  expect((await createReader(request({ ...fields, logicalName: "extra" }))).status).toBe(400);
  expect((await createReader(request(fields, "https://foreign.invalid"))).status).toBe(403);
  expect(fetcher).toHaveBeenCalledTimes(1);
});
