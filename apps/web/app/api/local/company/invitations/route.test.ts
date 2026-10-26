// @vitest-environment node
import { afterEach, beforeEach, expect, it, vi } from "vitest";
const session = vi.hoisted(() => ({ token: "issued-cookie-token" as string | null }));
vi.mock("next/headers", () => ({ cookies: async () => ({ get: () => session.token ? { value: session.token } : undefined }) }));
import { GET, POST } from "./route";

const origin = "http://localhost:3000", company = "22222222-2222-2222-2222-222222222222";
const input = { email: "member@example.com", roles: ["viewer"] };
const result = { companyId: company, id: "33333333-3333-3333-3333-333333333333", email: input.email, roles: input.roles,
  createdAtUtc: "2026-10-26T00:00:00+00:00", expiresAtUtc: "2026-11-02T00:00:00+00:00", acceptedAtUtc: null, revokedAtUtc: null, version: "1" };
const fetcher = vi.fn();
const mutation = (body = JSON.stringify(input), suffix = "", requestOrigin: string | null = origin) => new Request(
  `${origin}/api/local/company/invitations?companyId=${company}${suffix}`, { method: "POST", body,
    headers: { "Content-Type": "application/json", ...(requestOrigin ? { Origin: requestOrigin } : {}), Authorization: "Bearer FORGED", "X-AIOffice-Company-Id": "FORGED" } });
beforeEach(() => { session.token = "issued-cookie-token"; fetcher.mockReset(); vi.stubGlobal("fetch", fetcher);
  vi.stubEnv("AIOFFICE_LOCAL_UI_ENABLED", "true"); vi.stubEnv("AIOFFICE_BROWSER_OIDC_ENABLED", "false");
  vi.stubEnv("AIOFFICE_LOCAL_CORE_API_URL", "http://core.fixture.invalid"); });
afterEach(() => { vi.unstubAllGlobals(); vi.unstubAllEnvs(); });

it("creates with issued session authority and validates the bounded result", async () => {
  fetcher.mockResolvedValue(Response.json(result, { status: 201, headers: { "Set-Cookie": "PRIVATE=discard" } }));
  const response = await POST(mutation()); expect(response.status).toBe(201); expect(await response.json()).toEqual(result);
  const [url, init] = fetcher.mock.calls[0]; expect(url).toBe("http://core.fixture.invalid/api/company/invitations");
  expect(init.headers.get("Authorization")).toBe("Bearer issued-cookie-token"); expect(init.headers.get("X-AIOffice-Company-Id")).toBe(company);
  expect(JSON.parse(init.body)).toEqual(input); expect(response.headers.get("Set-Cookie")).toBeNull(); expect(response.headers.get("Cache-Control")).toBe("no-store");
});

it("passes the one-time password only on creation with no-store and without logging", async () => {
  const log = vi.spyOn(console, "log"), warn = vi.spyOn(console, "warn"), error = vi.spyOn(console, "error");
  const temporaryPassword = "SyntheticTemporaryPassword123!";
  fetcher.mockResolvedValue(Response.json({ ...result, temporaryPassword }, { status: 201 }));
  const response = await POST(mutation());
  expect(response.status).toBe(201); expect(await response.json()).toEqual({ ...result, temporaryPassword });
  expect(response.headers.get("Cache-Control")).toBe("no-store");
  expect(fetcher.mock.calls[0][1].cache).toBe("no-store");
  expect(log).not.toHaveBeenCalled(); expect(warn).not.toHaveBeenCalled(); expect(error).not.toHaveBeenCalled();
  vi.restoreAllMocks();
});

it.each(["short", null, 123, "x".repeat(129), "invalid\npassword1234"])("refuses malformed temporary credentials", async temporaryPassword => {
  fetcher.mockResolvedValue(Response.json({ ...result, temporaryPassword }, { status: 201 }));
  const response = await POST(mutation()); expect(response.status).toBe(503);
  expect(await response.text()).not.toContain("temporaryPassword");
});

it.each([null, "https://foreign.invalid", origin + "/"])("refuses bad origin %j", async requestOrigin => {
  expect((await POST(mutation(undefined, "", requestOrigin))).status).toBe(403); expect(fetcher).not.toHaveBeenCalled();
});
it.each(["{", "null", "[]", JSON.stringify({ ...input, companyId: company }), JSON.stringify({ ...input, roles: ["ADMIN"] }),
  JSON.stringify(input).replace('"email":"member@example.com"', '"email":"first@example.com","email":"member@example.com"'),
  JSON.stringify(input).replace('"email":"member@example.com"', '"email":"first@example.com","em\\u0061il":"member@example.com"')])
  ("refuses invalid invitation body %s", async body => { expect((await POST(mutation(body))).status).toBe(400); expect(fetcher).not.toHaveBeenCalled(); });
it("returns only bounded conflict codes", async () => {
  fetcher.mockResolvedValue(Response.json({ code: "duplicate-invitation", error: "PRIVATE SQL" }, { status: 409 }));
  let response = await POST(mutation()); expect(await response.json()).toEqual({ error: "Không xác nhận được lời mời thành viên.", code: "duplicate-invitation" });
  fetcher.mockResolvedValue(Response.json({ code: "PRIVATE", error: "PRIVATE SQL" }, { status: 409 }));
  response = await POST(mutation()); expect(JSON.stringify(await response.json())).not.toContain("PRIVATE");
});
it.each([{ ...result, companyId: "55555555-5555-5555-5555-555555555555" }, { ...result, token: "PRIVATE" },
  { ...result, acceptedAtUtc: result.createdAtUtc }, { ...result, version: "01" }])("refuses malformed success %j", async value => {
  fetcher.mockResolvedValue(Response.json(value, { status: 201 })); const response = await POST(mutation());
  expect(response.status).toBe(503); expect(await response.text()).not.toContain("PRIVATE");
});
it("proxies only a bounded invitation page selector", async () => {
  fetcher.mockResolvedValue(Response.json({ companyId: company, items: [] }));
  const response = await GET(new Request(`${origin}/api/local/company/invitations?companyId=${company}&offset=25&limit=25`));
  expect(response.status).toBe(200); expect(fetcher.mock.calls[0][0]).toBe("http://core.fixture.invalid/api/company/invitations?offset=25&limit=25");
  for (const suffix of ["&offset=-1", "&offset=01", "&limit=101", "&tenantId=forged", "&companyId=" + company])
    expect((await GET(new Request(`${origin}/api/local/company/invitations?companyId=${company}${suffix}`))).status).toBe(400);
});
