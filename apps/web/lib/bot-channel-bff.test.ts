// @vitest-environment node
import { afterEach, beforeEach, expect, it, vi } from "vitest";
const mocks = vi.hoisted(() => ({ cookies: vi.fn(), runtime: vi.fn(), read: vi.fn(), fetch: vi.fn() }));
vi.mock("next/headers", () => ({ cookies: mocks.cookies }));
vi.mock("./browser-auth-runtime", () => ({ getBrowserAuthRuntime: mocks.runtime }));
import { botChannelBff } from "./bot-channel-bff";
import { botCompany as company, botView, botPng } from "./bot-channel-test-fixture";
const session = { companyId: company, accessToken: "PRIVATE-TOKEN" };
const request = (path = "", method = "GET", body?: unknown, origin = "http://web.test", query = `companyId=${company}`) => new Request(`http://web.test/api/local/bot-channels${path}?${query}`,
  { method, headers: { "Content-Type": "application/json", Origin: origin }, ...(body === undefined ? {} : { body: JSON.stringify(body) }) });
beforeEach(() => {
  vi.clearAllMocks(); vi.stubEnv("AIOFFICE_BROWSER_OIDC_ENABLED", "false"); vi.stubEnv("AIOFFICE_LOCAL_UI_ENABLED", "true");
  mocks.cookies.mockResolvedValue({ get: () => ({ value: "PRIVATE-TOKEN" }) }); mocks.read.mockResolvedValue(session);
  mocks.runtime.mockReturnValue({ coreApiOrigin: "http://core.test", settings: {}, sessions: { read: mocks.read } }); vi.stubGlobal("fetch", mocks.fetch);
});
afterEach(() => { vi.unstubAllGlobals(); vi.unstubAllEnvs(); });
it("uses issued company authority and projects only the write-only admin contract", async () => {
  mocks.fetch.mockResolvedValue(Response.json({ ...botView, apiKey: "PRIVATE-KEY", ai: { ...botView.ai, apiKey: "PRIVATE-KEY", lastFour: "1234" }, channels: botView.channels.map(c => ({ ...c, secret: "PRIVATE-KEY" })) }));
  const response = await botChannelBff(request()); expect(response.status).toBe(200); expect(await response.json()).toEqual(botView);
  expect(response.headers.get("cache-control")).toBe("no-store");
  const [url, init] = mocks.fetch.mock.calls[0]; expect(url).toBe(`http://127.0.0.1:8080/api/companies/${company}/bot-channels`);
  expect(init.headers.get("X-AIOffice-Company-Id")).toBe(company); expect(init.headers.get("Authorization")).toBe("Bearer PRIVATE-TOKEN"); expect(init.redirect).toBe("error");
});
it("accepts a new key only in a same-origin write and never returns it", async () => {
  mocks.fetch.mockResolvedValue(Response.json({ ...botView.ai, apiKey: "PRIVATE-KEY" }));
  const input = { expectedVersion: "1", baseUrl: botView.ai.baseUrl, model: "model-b", apiKey: "PRIVATE-KEY" };
  const response = await botChannelBff(request("/ai", "PUT", input), ["ai"]); expect(response.status).toBe(200); expect(await response.json()).toEqual(botView.ai);
  expect(JSON.parse(mocks.fetch.mock.calls[0][1].body)).toEqual(input);
});
it("accepts bounded escaped Unicode heartbeat lists on both channel cards", async () => {
  const view = { ...botView, channels: botView.channels.map(c => ({ ...c, status: { ...c.status!, groups: Array.from({ length: 500 }, (_, i) => ({ externalId: "界".repeat(197) + String(i).padStart(3, "0"), displayName: "名".repeat(200) })) } })) };
  const escaped = JSON.stringify(view).replace(/[\u0080-\uffff]/g, c => `\\u${c.charCodeAt(0).toString(16).padStart(4, "0")}`);
  expect(new TextEncoder().encode(escaped).byteLength).toBeGreaterThan(2 * 1024 * 1024);
  mocks.fetch.mockResolvedValue(new Response(escaped, { headers: { "Content-Type": "application/json" } }));
  const response = await botChannelBff(request()); expect(response.status).toBe(200); expect(await response.json()).toEqual(view);
});
it.each([{}, { expectedVersion: "1", baseUrl: "file:///secret", model: "m" }, { expectedVersion: "1", baseUrl: "https://x/?key=SECRET", model: "m" },
  { expectedVersion: "1", baseUrl: "https://x", model: "m", apiKey: "PRIVATE", clearApiKey: true },
  { expectedVersion: "1", baseUrl: "https://x", model: "m", companyId: company }])("rejects invalid AI mutation %j", async input => {
  expect((await botChannelBff(request("/ai", "PUT", input), ["ai"])).status).toBe(400); expect(mocks.fetch).not.toHaveBeenCalled();
});
it("rejects cross-origin writes before reading credentials or calling Core", async () => {
  expect((await botChannelBff(request("/ai", "PUT", {}, "http://evil.test"), ["ai"])).status).toBe(403); expect(mocks.cookies).not.toHaveBeenCalled(); expect(mocks.fetch).not.toHaveBeenCalled();
});
it.each(["", `companyId=${company}&companyId=${company}`, `companyId=${company}&tenantId=foreign`])("refuses invalid company selectors %s", async query => {
  expect((await botChannelBff(request("", "GET", undefined, undefined, query))).status).toBe(400); expect(mocks.fetch).not.toHaveBeenCalled();
});
it.each([403, 404, 409, 503])("returns bounded %i errors without forwarding upstream bodies or headers", async status => {
  mocks.fetch.mockResolvedValue(new Response("PRIVATE-KEY Exception secretref://env/X", { status, headers: { "Set-Cookie": "PRIVATE" } }));
  const response = await botChannelBff(request()); expect(response.status).toBe(status); expect(await response.text()).not.toContain("PRIVATE"); expect(response.headers.get("set-cookie")).toBeNull();
});
it("fences admin body release after logout in browser mode", async () => {
  vi.stubEnv("AIOFFICE_BROWSER_OIDC_ENABLED", "true"); mocks.read.mockReset().mockResolvedValueOnce(session).mockResolvedValueOnce(session).mockResolvedValueOnce(null);
  mocks.fetch.mockResolvedValue(Response.json(botView)); const response = await botChannelBff(request()); expect(response.status).toBe(401); expect(await response.text()).not.toContain("model-a");
});
it("never returns expired QR or unsafe image content types", async () => {
  for (const qr of [{ contentType: "image/svg+xml", base64: botPng, expiresAt: new Date(Date.now() + 60000).toISOString() },
    { contentType: "image/png", base64: botPng, expiresAt: new Date(Date.now() - 1).toISOString() }]) {
    mocks.fetch.mockResolvedValue(Response.json(qr)); expect((await botChannelBff(request("/zalo/qr"), ["zalo", "qr"])).status).toBe(502);
  }
});
it("returns fixed connection test results only", async () => {
  mocks.fetch.mockResolvedValue(Response.json({ success: false, code: "upstream_rejected", rawBody: "PRIVATE-KEY" }));
  const response = await botChannelBff(request("/ai/test", "POST"), ["ai", "test"]); expect(await response.json()).toEqual({ success: false, code: "upstream_rejected" });
});
it("feature unavailable when the web workspace is disabled", async () => {
  vi.stubEnv("AIOFFICE_LOCAL_UI_ENABLED", "false"); expect((await botChannelBff(request())).status).toBe(503); expect(mocks.fetch).not.toHaveBeenCalled();
});
