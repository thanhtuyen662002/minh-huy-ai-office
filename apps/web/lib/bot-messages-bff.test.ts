// @vitest-environment node
import { afterEach, beforeEach, expect, it, vi } from "vitest";
const mocks = vi.hoisted(() => ({ cookies: vi.fn(), runtime: vi.fn(), read: vi.fn(), fetch: vi.fn() }));
vi.mock("next/headers", () => ({ cookies: mocks.cookies }));
vi.mock("./browser-auth-runtime", () => ({ getBrowserAuthRuntime: mocks.runtime }));
import { botMessagesBff } from "./bot-messages-bff";
import { botCompany as company } from "./bot-channel-test-fixture";
const session = { companyId: company, accessToken: "PRIVATE-TOKEN" };
const request = (path: string, query: string) => new Request(`http://web.test/api/local/bot-messages${path}?companyId=${company}${query}`);
const group = { channel: "zalo", externalId: "g 1", displayName: "Nhóm A", customerName: "Khách A", lastMessageAt: null, messageCount: 0 };
const item = { messageId: "m1", senderName: "Anh Minh", text: "Dòng 1\nDòng 2", hasImage: false, sentAt: "2026-10-30T01:00:00Z" };
beforeEach(() => {
  vi.clearAllMocks(); vi.stubEnv("AIOFFICE_BROWSER_OIDC_ENABLED", "false"); vi.stubEnv("AIOFFICE_LOCAL_UI_ENABLED", "true");
  mocks.cookies.mockResolvedValue({ get: () => ({ value: "PRIVATE-TOKEN" }) }); mocks.read.mockResolvedValue(session);
  mocks.runtime.mockReturnValue({ coreApiOrigin: "http://core.test", settings: {}, sessions: { read: mocks.read } }); vi.stubGlobal("fetch", mocks.fetch);
});
afterEach(() => { vi.unstubAllGlobals(); vi.unstubAllEnvs(); });
it("lists groups and projects only the known fields", async () => {
  mocks.fetch.mockResolvedValue(Response.json([{ ...group, secret: "PRIVATE" }]));
  const response = await botMessagesBff(request("", "")); expect(response.status).toBe(200); expect(await response.json()).toEqual([group]);
  expect(response.headers.get("cache-control")).toBe("no-store");
  expect(mocks.fetch.mock.calls[0][0]).toBe(`http://127.0.0.1:8080/api/companies/${company}/bot-messages`);
});
it("reads a page with an encoded group and a canonical before cursor", async () => {
  mocks.fetch.mockResolvedValue(Response.json({ items: [item], hasMore: true }));
  const response = await botMessagesBff(request("/zalo", `&group=${encodeURIComponent("g 1&x")}&limit=50&before=${encodeURIComponent("2026-10-30T08:00:00+07:00")}`), "zalo");
  expect(response.status).toBe(200); expect(await response.json()).toEqual({ items: [item], hasMore: true });
  expect(mocks.fetch.mock.calls[0][0]).toBe(`http://127.0.0.1:8080/api/companies/${company}/bot-messages/zalo?group=g+1%26x&limit=50&before=2026-10-30T01%3A00%3A00.000Z`);
});
it.each([["telegram", "&group=g"], ["zalo", ""], ["zalo", "&group=g&limit=0"], ["zalo", "&group=g&limit=201"], ["zalo", "&group=g&before=soon"], ["zalo", "&group=g&extra=1"]])
("rejects %s %s before calling the API", async (channel, query) => {
  const response = await botMessagesBff(request(`/${channel}`, query), channel);
  expect([400, 404]).toContain(response.status); expect(mocks.fetch).not.toHaveBeenCalled();
});
it("refuses malformed upstream pages and passes through a missing group", async () => {
  mocks.fetch.mockResolvedValueOnce(Response.json({ items: [{ ...item, text: "" }], hasMore: false }));
  expect((await botMessagesBff(request("/zalo", "&group=g"), "zalo")).status).toBe(502);
  mocks.fetch.mockResolvedValueOnce(Response.json({ error: "x" }, { status: 404 }));
  expect((await botMessagesBff(request("/zalo", "&group=g"), "zalo")).status).toBe(404);
});
