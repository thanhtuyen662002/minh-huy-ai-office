// @vitest-environment node
import { afterEach, beforeEach, expect, it, vi } from "vitest";
const session = vi.hoisted(() => ({ token: "issued-cookie-token" as string | null }));
vi.mock("next/headers", () => ({ cookies: async () => ({ get: () => session.token ? { value: session.token } : undefined }) }));
import { POST } from "./route";
const origin = "http://localhost:3000", company = "22222222-2222-2222-2222-222222222222", id = "33333333-3333-3333-3333-333333333333";
const result = { companyId: company, id, email: "member@example.com", roles: ["viewer"], createdAtUtc: "2026-10-26T00:00:00+00:00",
  expiresAtUtc: "2026-11-02T00:00:00+00:00", acceptedAtUtc: null, revokedAtUtc: "2026-10-26T01:00:00+00:00", version: "2" };
const fetcher = vi.fn();
const request = (suffix = "", requestOrigin: string | null = origin) => new Request(`${origin}/api/local/company/invitations/${id}/revoke?companyId=${company}${suffix}`,
  { method: "POST", headers: requestOrigin ? { Origin: requestOrigin } : {} });
const invoke = (value: Request, invitationId = id) => POST(value, { params: Promise.resolve({ id: invitationId }) });
beforeEach(() => { session.token = "issued-cookie-token"; fetcher.mockReset(); vi.stubGlobal("fetch", fetcher);
  vi.stubEnv("AIOFFICE_LOCAL_UI_ENABLED", "true"); vi.stubEnv("AIOFFICE_BROWSER_OIDC_ENABLED", "false");
  vi.stubEnv("AIOFFICE_LOCAL_CORE_API_URL", "http://core.fixture.invalid"); });
afterEach(() => { vi.unstubAllGlobals(); vi.unstubAllEnvs(); });

it("revokes only the scoped invitation with issued authority", async () => {
  fetcher.mockResolvedValue(Response.json(result)); const response = await invoke(request());
  expect(response.status).toBe(200); expect(await response.json()).toEqual(result);
  const [url, init] = fetcher.mock.calls[0]; expect(url).toBe(`http://core.fixture.invalid/api/company/invitations/${id}/revoke`);
  expect(init.headers.get("Authorization")).toBe("Bearer issued-cookie-token"); expect(init.headers.get("X-AIOffice-Company-Id")).toBe(company);
});
it.each([null, "https://foreign.invalid"])("rejects bad origin %j", async requestOrigin => {
  expect((await invoke(request("", requestOrigin))).status).toBe(403); expect(fetcher).not.toHaveBeenCalled();
});
it.each(["bad", "00000000-0000-0000-0000-000000000000", "../foreign"])("rejects target %s", async target => {
  expect((await invoke(request(), target)).status).toBe(400); expect(fetcher).not.toHaveBeenCalled();
});
it("rejects injected selectors and mismatched results", async () => {
  expect((await invoke(request("&tenantId=forged"))).status).toBe(400); expect(fetcher).not.toHaveBeenCalled();
  fetcher.mockResolvedValue(Response.json({ ...result, id: "44444444-4444-4444-4444-444444444444" }));
  expect((await invoke(request())).status).toBe(503);
});
it("accepts the empty body stream Next.js gives a bodyless POST and rejects a real body", async () => {
  fetcher.mockResolvedValue(Response.json(result));
  const empty = new Request(`${origin}/api/local/company/invitations/${id}/revoke?companyId=${company}`,
    { method: "POST", headers: { Origin: origin }, body: new ReadableStream({ start(controller) { controller.close(); } }), duplex: "half" } as RequestInit);
  expect(empty.body).not.toBeNull();
  expect((await invoke(empty)).status).toBe(200);
  fetcher.mockClear();
  const withBody = new Request(`${origin}/api/local/company/invitations/${id}/revoke?companyId=${company}`,
    { method: "POST", headers: { Origin: origin }, body: "{}" });
  expect((await invoke(withBody)).status).toBe(400); expect(fetcher).not.toHaveBeenCalled();
});
