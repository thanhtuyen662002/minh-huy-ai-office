// @vitest-environment node
import { afterEach, beforeEach, expect, it, vi } from "vitest";
const mocks = vi.hoisted(() => ({ cookies: vi.fn(), runtime: vi.fn(), read: vi.fn(), fetch: vi.fn() }));
vi.mock("next/headers", () => ({ cookies: mocks.cookies }));
vi.mock("./browser-auth-runtime", () => ({ getBrowserAuthRuntime: mocks.runtime }));
import { DELETE as unlink, GET as read, POST as create } from "../app/api/local/telegram-link/route";
import { requestCompany as company } from "./customer-request-test-fixture";

const code = "AbCdEfGhIjKlMnOpQrSt_-";
const request = (init?: RequestInit, query = `companyId=${company}`) => new Request(`http://web.test/api/local/telegram-link?${query}`, init);
beforeEach(() => {
  vi.clearAllMocks(); vi.stubEnv("AIOFFICE_BROWSER_OIDC_ENABLED", "false"); vi.stubEnv("AIOFFICE_LOCAL_UI_ENABLED", "true");
  mocks.cookies.mockResolvedValue({ get: () => ({ value: "PRIVATE-TOKEN" }) });
  mocks.read.mockResolvedValue({ companyId: company, accessToken: "PRIVATE-TOKEN" });
  mocks.runtime.mockReturnValue({ coreApiOrigin: "http://core.test", settings: {}, sessions: { read: mocks.read } });
  vi.stubGlobal("fetch", mocks.fetch);
});
afterEach(() => { vi.unstubAllGlobals(); vi.unstubAllEnvs(); });

it("reads the member's link and drops unknown fields", async () => {
  const link = { linked: true, telegramName: "Minh", linkedAt: "2026-10-30T04:00:00+00:00", botUsername: "Minh_Huy_Ky_Thuat_Bot" };
  mocks.fetch.mockResolvedValue(Response.json({ ...link, telegramUserId: 42 }));
  const response = await read(request());
  expect(await response.json()).toEqual(link);
  expect(String(mocks.fetch.mock.calls[0][0])).toContain(`/api/companies/${company}/telegram-link`);
});

it("creates a code only for a same-origin POST and checks the deep link", async () => {
  expect((await create(request({ method: "POST", headers: { Origin: "http://evil.test" } }))).status).toBe(403);
  const value = { code, url: `https://t.me/Minh_Huy_Ky_Thuat_Bot?start=${code}`, expiresAt: "2026-10-30T04:15:00+00:00" };
  mocks.fetch.mockResolvedValueOnce(Response.json(value));
  expect(await (await create(request({ method: "POST", headers: { Origin: "http://web.test" } }))).json()).toEqual(value);
  mocks.fetch.mockResolvedValueOnce(Response.json({ ...value, url: "https://evil.test/?start=" + code }));
  expect((await create(request({ method: "POST", headers: { Origin: "http://web.test" } }))).status).toBe(502);
});

it("unlinks, passes 404 when the feature is off and rejects extra query keys", async () => {
  mocks.fetch.mockResolvedValueOnce(new Response(null, { status: 204 }));
  expect((await unlink(request({ method: "DELETE", headers: { Origin: "http://web.test" } }))).status).toBe(204);
  mocks.fetch.mockResolvedValueOnce(new Response(null, { status: 404 }));
  expect((await read(request())).status).toBe(404);
  expect((await read(request(undefined, `companyId=${company}&x=1`))).status).toBe(400);
});
