// @vitest-environment node
import { afterEach, beforeEach, expect, it, vi } from "vitest";
const mocks = vi.hoisted(() => ({ runtime: vi.fn(), cookies: vi.fn(), read: vi.fn(), fetch: vi.fn() }));
vi.mock("./browser-auth-runtime", () => ({ getBrowserAuthRuntime: mocks.runtime }));
vi.mock("next/headers", () => ({ cookies: mocks.cookies }));
import { GET as sources } from "../app/api/local/group-sources/route";
import { GET as messages } from "../app/api/local/group-sources/[sourceId]/messages/route";
import { GET as message } from "../app/api/local/group-sources/[sourceId]/messages/[messageId]/route";

const company = "22222222-2222-4222-8222-222222222222", sourceId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", messageId = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
const scope = { companyId: company, tenantId: "11111111-1111-4111-8111-111111111111", sourceBindingId: sourceId };
const session = { companyId: company, accessToken: "PRIVATE-TOKEN", subject: "PRIVATE-SUBJECT", expiresAt: Math.floor(Date.now() / 1000) + 300 };
const sourcePage = { companyId: company, items: [{ source: scope, displayName: "Khách 😀 � ", provider: "owned", version: 1 }], hasMore: false };
const head = { messageId, revision: 2, lastChangedSequence: 5, kind: 3, occurredAtUtc: "2026-10-10T00:00:00Z", isHistoricalBackfill: false };
const messagePage = { source: scope, items: [head], nextBeforeSequence: null, hasCoverageGap: true };
const privateMessage = { source: scope, messageId, externalMessageId: "m😀 � ", revision: 2, committedSequence: 3, kind: 3,
  senderId: "s😀 � ", replyToMessageId: "r ", occurredAtUtc: head.occurredAtUtc, text: "\uFEFF tồn kho 😀 �\n ", isHistoricalBackfill: false, hasCoverageGap: true };
type Mode = "sources" | "messages" | "message";
const req = (query = `companyId=${company}`, signal?: AbortSignal) => new Request(`http://internal:3000/api/local/group-sources?${query}`,
  { signal, headers: { Authorization: "Bearer FORGED", "X-AIOffice-Company-Id": "FORGED" } });
const invoke = (mode: Mode, request = req()) => mode === "sources" ? sources(request) : mode === "messages"
  ? messages(request, { params: Promise.resolve({ sourceId }) }) : message(request, { params: Promise.resolve({ sourceId, messageId }) });
const body = (mode: Mode) => mode === "sources" ? sourcePage : mode === "messages" ? messagePage : privateMessage;
beforeEach(() => {
  vi.clearAllMocks(); vi.stubEnv("AIOFFICE_BROWSER_OIDC_ENABLED", "true"); vi.stubEnv("AIOFFICE_LOCAL_UI_ENABLED", "false");
  mocks.runtime.mockReturnValue({ coreApiOrigin: "http://core.fixture.invalid", settings: {}, sessions: { read: mocks.read } });
  mocks.cookies.mockResolvedValue({ get: (name: string) => ({ value: name.includes("binding") ? "PRIVATE-BINDING" : "PRIVATE-SID" }) });
  mocks.read.mockResolvedValue(session); vi.stubGlobal("fetch", mocks.fetch);
});
afterEach(() => { vi.unstubAllGlobals(); vi.unstubAllEnvs(); vi.useRealTimers(); });
it.each(["sources", "messages", "message"] as const)("serves strict %s via issued SID, strips upstream secrets and fences final release", async mode => {
  const value = body(mode); mocks.fetch.mockResolvedValue(Response.json(value, { headers: { "Set-Cookie": "PRIVATE", "X-PRIVATE": "PRIVATE" } }));
  const response = await invoke(mode); expect(response.status).toBe(200); expect(await response.json()).toEqual(value);
  expect(response.headers.get("cache-control")).toBe("no-store"); expect(response.headers.get("set-cookie")).toBeNull();
  const [url, init] = mocks.fetch.mock.calls[0];
  expect(url).toBe(`http://core.fixture.invalid/api/group-sources${mode === "sources" ? "?offset=0&limit=25" : `/${sourceId}/messages${mode === "messages" ? "?limit=25" : `/${messageId}`}`}`);
  expect(init.headers.get("Authorization")).toBe("Bearer PRIVATE-TOKEN"); expect(init.headers.get("X-AIOffice-Company-Id")).toBe(company);
  expect(mocks.read).toHaveBeenCalledTimes(3);
});
it.each(["limit=26", "limit=0", "limit=01", "offset=10001", "offset=01", "limit=1&limit=2", "tenantId=forged", "ownerId=forged", `companyId=${company}`])("denies invalid/ambiguous selector %s before fetching authority", async suffix => {
  expect((await invoke("sources", req(`companyId=${company}&${suffix}`))).status).toBe(400);
  expect(mocks.read).not.toHaveBeenCalled(); expect(mocks.fetch).not.toHaveBeenCalled();
});
it.each(["beforeSequence=0", "beforeSequence=01", "beforeSequence=9007199254740992", "beforeSequence=1&beforeSequence=2", "offset=1"])("refuses invalid logical cursor %s", async suffix => {
  expect((await invoke("messages", req(`companyId=${company}&${suffix}`))).status).toBe(400); expect(mocks.fetch).not.toHaveBeenCalled();
});
it("rejects noncanonical route IDs and default disabled requests before authority", async () => {
  expect((await messages(req(), { params: Promise.resolve({ sourceId: sourceId.toUpperCase() }) })).status).toBe(400);
  expect((await message(req(), { params: Promise.resolve({ sourceId, messageId: "00000000-0000-0000-0000-000000000000" }) })).status).toBe(400);
  vi.stubEnv("AIOFFICE_BROWSER_OIDC_ENABLED", "false"); expect((await sources(req())).status).toBe(503);
  expect(mocks.fetch).not.toHaveBeenCalled();
});
it.each([400, 401, 403, 404, 500, 503])( "scrubs upstream denial %i without its private diagnostics", async status => {
  mocks.fetch.mockResolvedValue(new Response("PRIVATE-STACK-KEY", { status }));
  const response = await invoke("message"); expect(response.status).toBe(status === 500 ? 502 : status);
  expect(await response.text()).not.toContain("PRIVATE"); expect(response.headers.get("cache-control")).toBe("no-store");
});
it.each(["sources", "messages", "message"] as const)("drops %s after final SID loss or company change", async mode => {
  for (const lost of [null, { ...session, companyId: sourceId }]) {
    mocks.read.mockReset().mockResolvedValueOnce(session).mockResolvedValueOnce(session).mockResolvedValueOnce(lost);
    mocks.fetch.mockResolvedValue(Response.json(body(mode))); const response = await invoke(mode);
    expect(response.status).toBe(401); expect(await response.text()).not.toContain("tồn kho");
  }
});
it("drops streamed source text after logout while buffering and cancels stalled caller body", async () => {
  let controller!: ReadableStreamDefaultController<Uint8Array>;
  mocks.fetch.mockResolvedValue(new Response(new ReadableStream({ start(value) { controller = value; } }), { headers: { "Content-Type": "application/json" } }));
  const pending = invoke("message"); await vi.waitFor(() => expect(mocks.fetch).toHaveBeenCalledTimes(1));
  mocks.read.mockResolvedValue(null); controller.enqueue(new TextEncoder().encode(JSON.stringify(privateMessage))); controller.close();
  const denied = await pending; expect(denied.status).toBe(401); expect(await denied.text()).not.toContain("tồn kho");
  mocks.read.mockResolvedValue(session); const abort = new AbortController(), cancel = vi.fn();
  mocks.fetch.mockResolvedValue(new Response(new ReadableStream({ cancel }), { headers: { "Content-Type": "application/json" } }));
  const stalled = invoke("message", req(undefined, abort.signal)); await vi.waitFor(() => expect(mocks.fetch).toHaveBeenCalledTimes(2)); abort.abort();
  expect((await stalled).status).toBe(503); expect(cancel).toHaveBeenCalled();
});
it.each([
  { ...privateMessage, source: { ...scope, companyId: sourceId } }, { ...privateMessage, source: { ...scope, sourceBindingId: messageId } },
  { ...privateMessage, messageId: sourceId }, { ...privateMessage, text: "\ud800" }, { ...privateMessage, committedSequence: 9007199254740992 },
  { ...privateMessage, text: "x".repeat(8001) }, { ...privateMessage, kind: 4 }, { ...privateMessage, keyId: "PRIVATE" },
  { ...privateMessage, occurredAtUtc: "2026-02-30T00:00:00Z" }, { ...privateMessage, replyToMessageId: "\udfff" },
])( "refuses malformed private projection without releasing source text", async value => {
  mocks.fetch.mockResolvedValue(Response.json(value)); const response = await invoke("message");
  expect(response.status).toBe(502); expect(await response.text()).not.toContain("tồn kho");
});
it("recall has no text and original opaque IDs retain trailing spaces", async () => {
  const recall = { ...privateMessage, kind: 4, text: null }; mocks.fetch.mockResolvedValue(Response.json(recall));
  expect(await (await invoke("message")).json()).toEqual(recall);
});
it("refuses duplicate JSON keys, malformed UTF8 and byte overflow before releasing a projection", async () => {
  const encoded = JSON.stringify(privateMessage);
  for (const value of [encoded.replace('"text":', '"text":"PRIVATE","te\\u0078t":'), new Uint8Array([0xff]), " ".repeat(128 * 1024 + 1)]) {
    mocks.fetch.mockResolvedValue(new Response(value, { headers: { "Content-Type": "application/json" } }));
    expect((await invoke("message")).status).toBe(502);
  }
});
it("refuses foreign/duplicate sources and inconsistent message cursor/order instead of repairing pages", async () => {
  const cases: [Mode, unknown, Request?][] = [
    ["sources", { ...sourcePage, items: [...sourcePage.items, sourcePage.items[0]] }],
    ["sources", { ...sourcePage, items: [...sourcePage.items, { ...sourcePage.items[0], source: { ...scope, tenantId: messageId, sourceBindingId: messageId } }] }],
    ["sources", { ...sourcePage, hasMore: true }],
    ["messages", { ...messagePage, nextBeforeSequence: 4 }, req(`companyId=${company}&limit=1`)],
    ["messages", { ...messagePage, items: [head, { ...head, messageId: sourceId, lastChangedSequence: 6 }] }],
    ["messages", messagePage, req(`companyId=${company}&beforeSequence=5`)],
  ];
  for (const [mode, value, request] of cases) {
    mocks.fetch.mockResolvedValue(Response.json(value)); expect((await invoke(mode, request)).status).toBe(502);
  }
  mocks.fetch.mockResolvedValue(Response.json({ ...messagePage, nextBeforeSequence: 5 }));
  expect((await invoke("messages", req(`companyId=${company}&limit=1&beforeSequence=6`))).status).toBe(200);
});
