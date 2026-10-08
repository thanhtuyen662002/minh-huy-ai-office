// @vitest-environment node
import { beforeEach, expect, it, vi } from "vitest";
import { GET } from "./route";

const fixture = vi.hoisted(() => ({ fetch: vi.fn(), enabled: vi.fn(() => true) }));
vi.mock("../../../../lib/local-ai-bff", async importOriginal => ({
  ...await importOriginal<typeof import("../../../../lib/local-ai-bff")>(),
  fetchCoreApi: fixture.fetch, isOfficeAiUiEnabled: fixture.enabled,
}));
const company = "22222222-2222-2222-2222-222222222222";
const choice = { companyId: company, companyName: "Company" };
const request = (query = `companyId=${company}`) => new Request(`http://localhost:3000/api/local/companies?${query}`);
beforeEach(() => { fixture.fetch.mockReset(); fixture.enabled.mockReturnValue(true); });
async function privateFailure(response: Response, status: number) {
  expect(response.status).toBe(status); expect(response.headers.get("cache-control")).toBe("no-store");
  expect(response.headers.get("set-cookie")).toBeNull(); expect(await response.text()).not.toContain("PRIVATE");
}
it("relays only verified two-field choices through the issued-session Core helper", async () => {
  fixture.fetch.mockResolvedValue(Response.json({ items: [choice] }));
  const response = await GET(request()); expect(await response.json()).toEqual({ items: [choice] });
  expect(response.headers.get("cache-control")).toBe("no-store"); expect(response.headers.get("set-cookie")).toBeNull();
  expect(fixture.fetch).toHaveBeenCalledWith("/api/auth/companies", company);
});
it.each(["", "companyId=foreign", `companyId=${company}&companyId=${company}`])("rejects invalid or duplicate selection %s", async query => {
  await privateFailure(await GET(request(query)), 400); expect(fixture.fetch).not.toHaveBeenCalled();
});
it("requires enabled UI and issued session", async () => {
  fixture.enabled.mockReturnValue(false); await privateFailure(await GET(request()), 503); expect(fixture.fetch).not.toHaveBeenCalled();
  fixture.enabled.mockReturnValue(true); fixture.fetch.mockResolvedValue(null); await privateFailure(await GET(request()), 401);
});
it.each([401, 403, 500])("bounds upstream denial %i", async status => {
  fixture.fetch.mockResolvedValue(Response.json({ error: "PRIVATE diagnostics" }, { status, headers: { "Set-Cookie": "PRIVATE" } }));
  await privateFailure(await GET(request()), status === 500 ? 503 : status);
});
it.each([{ items: [choice], roles: ["admin"] }, { items: [{ ...choice, companyName: "PRIVATE\n" }] }, { items: [choice, choice] }])(
  "rejects malformed upstream choices without returning private data", async body => {
    fixture.fetch.mockResolvedValue(Response.json(body)); await privateFailure(await GET(request()), 503);
  });
it("bounds transport, malformed JSON and non-JSON replies", async () => {
  fixture.fetch.mockRejectedValue(new Error("PRIVATE")); await privateFailure(await GET(request()), 503);
  fixture.fetch.mockResolvedValue(new Response("PRIVATE")); await privateFailure(await GET(request()), 503);
  fixture.fetch.mockResolvedValue(new Response("{PRIVATE", { headers: { "Content-Type": "application/json" } })); await privateFailure(await GET(request()), 503);
});
