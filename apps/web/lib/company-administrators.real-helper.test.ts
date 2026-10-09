// @vitest-environment node
import { createServer } from "node:http";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
const mocks = vi.hoisted(() => ({ runtime: vi.fn(), cookies: vi.fn(), read: vi.fn(), fetch: vi.fn() }));
vi.mock("./browser-auth-runtime", () => ({ getBrowserAuthRuntime: mocks.runtime }));
vi.mock("next/headers", () => ({ cookies: mocks.cookies }));
import { POST } from "../app/api/local/company/members/[userId]/administrator/route";
const networkFetch = globalThis.fetch;
const company = "22222222-2222-2222-2222-222222222222", user = "33333333-3333-3333-3333-333333333333";
const input = { operationId: "44444444-4444-4444-4444-444444444444", expectedVersion: "7", isAdministrator: true };
const receipt = { companyId: company, userId: user, operationId: input.operationId, membershipVersion: "8", isAdministrator: true };
const session = { companyId: company, accessToken: "PRIVATE-TOKEN", subject: "PRIVATE-SUBJECT", expiresAt: Math.floor(Date.now() / 1000) + 300 };
const settings = { localHttp: false, publicOrigin: "https://office.example.test" };
const request = (selected = company, origin = settings.publicOrigin) => new Request(
  `http://internal-web:3000/api/local/company/members/${user}/administrator?companyId=${selected}`, {
    method: "POST", headers: { Host: "office.example.test", Origin: origin, "Content-Type": "application/json",
      Authorization: "Bearer FORGED", "X-AIOffice-Company-Id": "FORGED" }, body: JSON.stringify(input),
  });
const invoke = (req = request()) => POST(req, { params: Promise.resolve({ userId: user }) });
beforeEach(() => {
  vi.clearAllMocks(); vi.stubEnv("AIOFFICE_BROWSER_OIDC_ENABLED", "true"); vi.stubEnv("AIOFFICE_LOCAL_UI_ENABLED", "false");
  mocks.runtime.mockReturnValue({ settings, coreApiOrigin: "http://core.fixture.invalid", sessions: { read: mocks.read } });
  mocks.cookies.mockResolvedValue({ get: (name: string) => ({ value: name.includes("binding") ? "PRIVATE-BINDING" : "PRIVATE-SID" }) });
  mocks.read.mockResolvedValue(session);
  mocks.fetch.mockResolvedValue(Response.json(receipt, { headers: { "Set-Cookie": "PRIVATE=discard", "X-PRIVATE": "secret" } }));
  vi.stubGlobal("fetch", mocks.fetch);
});
afterEach(() => { vi.unstubAllGlobals(); vi.unstubAllEnvs(); });

it("uses the issued session through the real browser helper behind public HTTPS and internal HTTP", async () => {
  const response = await invoke(); expect(response.status).toBe(200); expect(await response.json()).toEqual(receipt);
  expect(response.headers.get("Set-Cookie")).toBeNull(); expect(response.headers.get("X-PRIVATE")).toBeNull();
  expect(response.headers.get("Cache-Control")).toBe("no-store");
  const [url, init] = mocks.fetch.mock.calls[0]; expect(url).toContain(`/api/company/members/${user}/administrator`);
  expect(init.headers.get("Authorization")).toBe("Bearer PRIVATE-TOKEN"); expect(init.headers.get("X-AIOffice-Company-Id")).toBe(company);
  expect(init.cache).toBe("no-store"); expect(init.redirect).toBe("error"); expect(mocks.read).toHaveBeenCalledTimes(2);
});
it.each([null, { ...session, companyId: "66666666-6666-6666-6666-666666666666" }])(
  "discards a successful receipt when final issued-session scope is lost: %j", async current => {
    mocks.read.mockResolvedValueOnce(session).mockResolvedValueOnce(current);
    const response = await invoke(); expect(response.status).toBe(401); expect(await response.text()).not.toContain(input.operationId);
    expect(mocks.fetch).toHaveBeenCalledTimes(1);
  });
it("refuses a foreign selected company before Core transport", async () => {
  expect((await invoke(request("66666666-6666-6666-6666-666666666666"))).status).toBe(401); expect(mocks.fetch).not.toHaveBeenCalled();
});
it("checks trusted Origin before issued-session reads", async () => {
  expect((await invoke(request(company, "https://foreign.invalid"))).status).toBe(403); expect(mocks.read).not.toHaveBeenCalled();
  expect(mocks.fetch).not.toHaveBeenCalled();
});
it("rejects malformed Core UTF8 after the real helper buffers the body", async () => {
  mocks.fetch.mockResolvedValue(new Response(new Uint8Array([123, 34, 255, 34, 58, 49, 125]), { headers: { "Content-Type": "application/json" } }));
  expect((await invoke()).status).toBe(503); expect(mocks.read).toHaveBeenCalledTimes(2);
});
it("fences delayed private receipt bytes after buffering", async () => {
  let finish!: ReadableStreamDefaultController<Uint8Array>;
  const stream = new ReadableStream<Uint8Array>({ start(controller) { finish = controller; } });
  mocks.fetch.mockResolvedValue(new Response(stream, { headers: { "Content-Type": "application/json" } }));
  const pending = invoke(); await vi.waitFor(() => expect(mocks.fetch).toHaveBeenCalledTimes(1));
  expect(mocks.read).toHaveBeenCalledTimes(1); mocks.read.mockResolvedValue(null);
  finish.enqueue(new TextEncoder().encode(JSON.stringify(receipt))); finish.close();
  expect((await pending).status).toBe(401); expect(mocks.read).toHaveBeenCalledTimes(2); expect(stream.locked).toBe(false);
});
it("exposes only a bounded conflict through the real browser helper", async () => {
  mocks.fetch.mockResolvedValue(Response.json({ code: "__proto__", error: "PRIVATE token SQL" }, { status: 409, headers: { "Set-Cookie": "PRIVATE=discard" } }));
  const response = await invoke(); expect(response.status).toBe(409); expect(response.headers.get("Set-Cookie")).toBeNull();
  const body = await response.json(); expect(Object.hasOwn(body, "code")).toBe(false); expect(JSON.stringify(body)).not.toContain("PRIVATE");
});

it.each(["headers", "body"].flatMap(boundary => ["valid", "revoked", "outage", "company-changed"].map(authority => ({ boundary, authority }))))(
  "real committed HTTP reply loss at $boundary preserves retry only with $authority authority", async ({ boundary, authority }) => {
    let committed = 0, attempts = 0;
    const received: unknown[] = [];
    const server = createServer((req, res) => {
      let body = ""; req.on("data", chunk => { body += chunk; });
      req.on("end", () => {
        received.push(JSON.parse(body)); attempts++;
        if (attempts === 1) committed++;
        res.setHeader("Content-Type", "application/json"); res.setHeader("Set-Cookie", "PRIVATE=discard");
        if (attempts > 1) { res.end(JSON.stringify(receipt)); return; }
        if (boundary === "headers") { res.destroy(); return; }
        res.setHeader("Content-Length", "4096"); res.flushHeaders(); res.write('{"PRIVATE":"partial');
        // Send actual headers/body before destroying the incomplete response.
        setImmediate(() => res.destroy());
      });
    });
    await new Promise<void>(resolve => server.listen(0, "127.0.0.1", resolve));
    const address = server.address(); if (!address || typeof address === "string") throw new Error("Fixture address missing.");
    mocks.runtime.mockReturnValue({ settings, coreApiOrigin: `http://127.0.0.1:${address.port}`, sessions: { read: mocks.read } });
    mocks.fetch.mockImplementation(networkFetch);
    mocks.read.mockResolvedValueOnce(session);
    if (authority === "revoked") mocks.read.mockResolvedValueOnce(null);
    if (authority === "outage") mocks.read.mockRejectedValueOnce(new Error("PRIVATE coordinator details"));
    if (authority === "company-changed") mocks.read.mockResolvedValueOnce({ ...session, companyId: "66666666-6666-6666-6666-666666666666" });
    try {
      const response = await invoke(); expect(committed).toBe(1); expect(received).toEqual([input]);
      expect(response.status).toBe(authority === "valid" ? 503 : 401); expect(mocks.read).toHaveBeenCalledTimes(2);
      expect(response.headers.get("Set-Cookie")).toBeNull(); expect(response.headers.get("Cache-Control")).toBe("no-store");
      const body = await response.text(); expect(body).not.toContain("PRIVATE"); expect(body).not.toContain(input.operationId);
      if (authority === "valid") {
        const replay = await invoke(); expect(replay.status).toBe(200); expect(await replay.json()).toEqual(receipt);
        expect(received).toEqual([input, input]); expect(committed).toBe(1); expect(mocks.read).toHaveBeenCalledTimes(4);
      }
    } finally { server.closeAllConnections(); await new Promise<void>(resolve => server.close(() => resolve())); }
  });
