// @vitest-environment node
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createServer } from "node:http";
import { BROWSER_CORE_RESPONSE_BYTES, fetchCoreApi } from "./local-ai-bff";
import { readBrowserOidcSettings } from "./browser-oidc";
import { browserBindingCookieName, browserSessionCookieName } from "./browser-session-store";
import { GET as context } from "../app/api/local/session/route";
import { POST as password } from "../app/api/local/session/login/route";
import { POST as logout } from "../app/api/local/session/logout/route";
import { GET as sources, POST as mutateSource } from "../app/api/local/data-sources/route";
import { GET as members } from "../app/api/local/company/members/route";
import { GET as choices } from "../app/api/local/data-sources/registration-options/route";
import { POST as task } from "../app/api/local/tasks/route";
import { POST as connection } from "../app/api/local/data-sources/[dataSourceId]/connection-test/route";

const mocks = vi.hoisted(() => ({ runtime: vi.fn(), cookies: vi.fn() }));
vi.mock("./browser-auth-runtime", () => ({ getBrowserAuthRuntime: mocks.runtime }));
vi.mock("next/headers", () => ({ cookies: mocks.cookies }));
const origin = "https://office.example.test", core = "https://captured-core.example.test";
const company = "22222222-2222-2222-2222-222222222222", source = "44444444-4444-4444-4444-444444444444";
const values: Record<string, string> = {
  AIOFFICE_BROWSER_OIDC_ENABLED: "true", AIOFFICE_BROWSER_OIDC_PUBLIC_ORIGIN: origin,
  AIOFFICE_BROWSER_OIDC_ISSUER: "https://identity.example.test/realm", AIOFFICE_BROWSER_OIDC_CLIENT_ID: "browser-office",
  AIOFFICE_BROWSER_OIDC_AUTHORIZATION_ENDPOINT: "https://identity.example.test/auth",
  AIOFFICE_BROWSER_OIDC_TOKEN_ENDPOINT: "https://identity.example.test/token", AIOFFICE_BROWSER_OIDC_JWKS_URI: "https://identity.example.test/certs",
  AIOFFICE_BROWSER_OIDC_TRANSACTION_KEY: Buffer.alloc(32, 9).toString("base64url"),
};
const settings = readBrowserOidcSettings((name) => values[name])!;
const binding = `${Buffer.alloc(32, 1).toString("base64url")}.${Buffer.alloc(32, 2).toString("base64url")}`;
const nextBinding = `${binding.split(".")[0]}.${Buffer.alloc(32, 3).toString("base64url")}`;
const sid = Buffer.alloc(32, 4).toString("base64url"), token = "private.access.token";
const jar = new Map<string, string>();
const read = vi.fn(), revoke = vi.fn(), fetcher = vi.fn();
const networkFetch = globalThis.fetch;
const session = () => ({ accessToken: token, companyId: company, subject: "private-subject", expiresAt: Math.floor(Date.now() / 1000) + 300 });
function request(path = "/api/local/session", method = "GET", body?: unknown, extra: Record<string, string> = {}) {
  return new Request(`http://web:3000${path}?companyId=${company}`, {
    method, headers: { Host: "office.example.test", Origin: origin, "Content-Type": "application/json", ...extra },
    body: method === "GET" ? undefined : JSON.stringify(body ?? {}),
  });
}
beforeEach(() => {
  vi.clearAllMocks(); vi.stubEnv("AIOFFICE_BROWSER_OIDC_ENABLED", "true"); vi.stubEnv("AIOFFICE_LOCAL_UI_ENABLED", "false");
  vi.stubEnv("AIOFFICE_LOCAL_CORE_API_URL", "http://untrusted-legacy.invalid");
  vi.stubEnv("AIOFFICE_BROWSER_CORE_API_ORIGIN", "https://untrusted-later.invalid");
  jar.clear(); jar.set(browserBindingCookieName(settings), binding); jar.set(browserSessionCookieName(settings), sid);
  jar.set("aioffice_local_access_token", "untrusted-legacy-token");
  mocks.cookies.mockImplementation(async () => ({ get: (name: string) => jar.has(name) ? { value: jar.get(name) } : undefined }));
  mocks.runtime.mockReturnValue({ settings, coreApiOrigin: core, sessions: { read, revoke } });
  read.mockImplementation(async () => session()); revoke.mockResolvedValue(nextBinding);
  fetcher.mockImplementation(async () => Response.json({ companyId: company, roles: ["admin"] }, { headers: { "Set-Cookie": "untrusted=1" } }));
  vi.stubGlobal("fetch", fetcher);
});
afterEach(() => { vi.unstubAllEnvs(); vi.unstubAllGlobals(); });
describe("opaque browser BFF", () => {
  it.each([context, sources, members, choices])("enables an authenticated business route while password login is disabled", async (route) => {
    const response = await route(request()); expect(response.status).toBe(200);
    expect(response.headers.get("cache-control")).toBe("no-store"); expect(response.headers.get("set-cookie")).toBeNull();
    expect(JSON.stringify(await response.json())).not.toContain(token);
    const [url, init] = fetcher.mock.calls[0]; expect(url).toMatch(new RegExp(`^${core}/api/`));
    expect(init.headers.get("Authorization")).toBe(`Bearer ${token}`);
    expect(init.headers.get("X-AIOffice-Company-Id")).toBe(company);
    expect(init.cache).toBe("no-store"); expect(init.redirect).toBe("error"); expect(init.signal).toBeInstanceOf(AbortSignal);
    expect(read.mock.calls).toEqual([[binding, sid], [binding, sid]]);
  });
  it("never enables password grant implicitly in browser mode", async () => {
    const response = await password(request("/api/local/session/login", "POST", { username: "owner", password: "private-password", companyId: company }));
    expect(response.status).toBe(503); expect(response.headers.get("set-cookie")).toBeNull(); expect(fetcher).not.toHaveBeenCalled();
  });
  it.each([browserBindingCookieName(settings), browserSessionCookieName(settings)])("requires %s without legacy cookie fallback", async (name) => {
    vi.stubEnv("AIOFFICE_LOCAL_UI_ENABLED", "true"); jar.delete(name);
    expect((await context(request())).status).toBe(401); expect(fetcher).not.toHaveBeenCalled(); expect(read).not.toHaveBeenCalled();
  });
  it.each(["missing", "expired", "revoked", "restored", "bad-cipher"]) (
    "fails closed when shared coordinator denies %s authority", async () => {
      read.mockResolvedValue(null); expect((await context(request())).status).toBe(401); expect(fetcher).not.toHaveBeenCalled();
    });
  it("does not fall back to legacy authority on disabled/invalid enabled runtime or Redis outage", async () => {
    vi.stubEnv("AIOFFICE_LOCAL_UI_ENABLED", "true"); mocks.runtime.mockReturnValue(null);
    expect((await context(request())).status).toBe(401);
    mocks.runtime.mockImplementation(() => { throw new Error("private-runtime-config"); }); expect((await context(request())).status).toBe(401);
    mocks.runtime.mockReturnValue({ settings, coreApiOrigin: core, sessions: { read, revoke } });
    read.mockRejectedValue(new Error("private-redis-details")); const response = await context(request());
    expect(response.status).toBe(401); expect(await response.text()).not.toContain("private-redis-details"); expect(fetcher).not.toHaveBeenCalled();
  });
  it("binds selected company and permits only GUID spelling equivalence", async () => {
    expect(await fetchCoreApi("/api/auth/context", "99999999-9999-9999-9999-999999999999")).toBeNull();
    expect(fetcher).not.toHaveBeenCalled();
    const mixed = "ABCDEFAB-2222-2222-2222-222222222222"; read.mockResolvedValue({ ...session(), companyId: mixed.toLowerCase() });
    expect((await fetchCoreApi("/api/auth/context", mixed))?.status).toBe(200);
  });
  it("discards reply authority revoked or expired during Core processing", async () => {
    read.mockResolvedValueOnce(session()).mockResolvedValueOnce(null);
    expect((await context(request())).status).toBe(401); expect(fetcher).toHaveBeenCalledOnce();
  });
  it("discards replies on coordinator outage/config-change closure after Core processing", async () => {
    read.mockResolvedValueOnce(session()).mockRejectedValueOnce(new Error("private-closed-transport"));
    const response = await context(request()); expect(response.status).toBe(401);
    expect(response.headers.get("set-cookie")).toBeNull(); expect(await response.text()).not.toContain("private-closed-transport");
  });
  it.each([401, 403, 503])("retains fresh Core denial %i without substituting ID/session roles", async (status) => {
    fetcher.mockResolvedValue(Response.json({ error: "denied" }, { status }));
    const response = await members(request()); expect(response.status).toBe(status); expect(response.headers.get("cache-control")).toBe("no-store");
  });
  it("forces private authenticated headers/no-store/no-redirect over caller options and retains cancellation", async () => {
    const controller = new AbortController();
    await fetchCoreApi("/api/auth/context", company, { headers: { Authorization: "Bearer forged", "X-AIOffice-Company-Id": "forged" },
      cache: "force-cache", redirect: "follow", signal: controller.signal });
    const init = fetcher.mock.calls[0][1]; expect(init.headers.get("Authorization")).toBe(`Bearer ${token}`);
    expect(init.headers.get("X-AIOffice-Company-Id")).toBe(company); expect(init.redirect).toBe("error"); expect(init.cache).toBe("no-store");
    controller.abort(); expect(init.signal.aborted).toBe(true);
  });
  it("uses explicit pilot compatibility only when browser mode is disabled", async () => {
    vi.stubEnv("AIOFFICE_BROWSER_OIDC_ENABLED", "false"); vi.stubEnv("AIOFFICE_LOCAL_UI_ENABLED", "true");
    expect((await context(request())).status).toBe(200);
    expect(fetcher.mock.calls[0][0]).toBe("http://untrusted-legacy.invalid/api/auth/context");
    expect(fetcher.mock.calls[0][1].headers.get("Authorization")).toBe("Bearer untrusted-legacy-token");
    expect(mocks.runtime).not.toHaveBeenCalled(); expect(read).not.toHaveBeenCalled();
  });
  it.each(["positive", "logout", "expiry", "outage"])("fences %s after real HTTP headers while the private body is delayed", async (change) => {
    let releaseBody!: () => void, gotHeaders!: () => void;
    const headersReady = new Promise<void>((resolve) => { gotHeaders = resolve; });
    let current = true;
    read.mockImplementation(async () => {
      if (!current && change === "outage") throw new Error("private-store-unavailable");
      return current ? session() : null;
    });
    const server = createServer((_request, response) => {
      response.setHeader("Content-Type", "application/json"); response.flushHeaders();
      releaseBody = () => response.end(JSON.stringify({ privateEvidence: "private-streamed-context" }));
      gotHeaders();
    });
    await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
    const address = server.address() as import("node:net").AddressInfo;
    fetcher.mockImplementation(async (_url, init) => networkFetch(`http://127.0.0.1:${address.port}/context`, init));
    try {
      const pending = context(request()); await headersReady;
      expect(read).toHaveBeenCalledOnce();
      current = change === "positive"; releaseBody();
      const response = await pending; expect(response.status).toBe(current ? 200 : 401);
      const body = await response.text();
      if (current) expect(body).toContain("private-streamed-context");
      else expect(body).not.toContain("private-streamed-context");
      expect(response.headers.get("cache-control")).toBe("no-store"); expect(response.headers.get("set-cookie")).toBeNull();
      expect(read).toHaveBeenCalledTimes(2);
    } finally {
      server.closeAllConnections(); await new Promise<void>((resolve) => server.close(() => resolve()));
    }
  });
  it("bounds actual Core body bytes despite dishonest Content-Length", async () => {
    const cancel = vi.fn();
    fetcher.mockResolvedValue(new Response(new ReadableStream({
      start(controller) { controller.enqueue(new Uint8Array(BROWSER_CORE_RESPONSE_BYTES + 1)); }, cancel,
    }), { headers: { "Content-Type": "application/json", "Content-Length": "1" } }));
    expect((await context(request())).status).toBe(401); expect(cancel).toHaveBeenCalledOnce(); expect(read).toHaveBeenCalledOnce();
  });
  it("cancels a stalled Core body on caller abort without returning private data", async () => {
    const cancel = vi.fn(), controller = new AbortController();
    fetcher.mockResolvedValue(new Response(new ReadableStream({ cancel }), { headers: { "Content-Type": "application/json" } }));
    const pending = fetchCoreApi("/api/auth/context", company, { signal: controller.signal });
    await new Promise<void>((resolve) => setImmediate(resolve)); controller.abort();
    expect(await pending).toBeNull(); expect(cancel).toHaveBeenCalledOnce(); expect(read).toHaveBeenCalledOnce();
  });
  it("does not extend the Core deadline while reading a stalled body", async () => {
    const cancel = vi.fn(), deadline = new AbortController();
    const timeout = vi.spyOn(AbortSignal, "timeout").mockReturnValue(deadline.signal);
    fetcher.mockResolvedValue(new Response(new ReadableStream({ cancel })));
    try {
      const pending = context(request()); await new Promise<void>((resolve) => setImmediate(resolve)); deadline.abort();
      expect((await pending).status).toBe(401); expect(cancel).toHaveBeenCalledOnce(); expect(timeout).toHaveBeenCalledWith(10_000);
    } finally { timeout.mockRestore(); }
  });
  it("copies a bodyless Core response without inventing content or upstream cookies", async () => {
    fetcher.mockResolvedValue(new Response(null, { status: 204, headers: { "Set-Cookie": "untrusted=1" } }));
    const response = await sources(request()); expect(response.status).toBe(204); expect(await response.text()).toBe("");
    expect(response.headers.get("set-cookie")).toBeNull();
  });
});
describe("browser mutation Origin", () => {
  const routes = [
    (req: Request) => task(req),
    (req: Request) => connection(req, { params: Promise.resolve({ dataSourceId: source }) }),
    (req: Request) => mutateSource(req),
  ];
  it.each(routes)("rejects foreign Origin before body consumption/session/upstream", async (route) => {
    const req = request("/api/local/tasks", "POST", { private: "bad-input" }, { Origin: "https://foreign.invalid" });
    const response = await route(req); expect(response.status).toBe(403); expect(req.bodyUsed).toBe(false);
    expect(response.headers.get("cache-control")).toBe("no-store"); expect(response.headers.get("set-cookie")).toBeNull();
    expect(mocks.cookies).not.toHaveBeenCalled(); expect(fetcher).not.toHaveBeenCalled();
  });
  it.each(routes)("refuses raw Host alias despite configured Origin/forwarded Host", async (route) => {
    const response = await route(request("/api/local/tasks", "POST", {}, { Host: "office.example.test/a/..", "X-Forwarded-Host": "office.example.test" }));
    expect(response.status).toBe(403); expect(fetcher).not.toHaveBeenCalled();
  });
  it("submits a valid task through trusted HTTPS browser authority after TLS termination", async () => {
    fetcher.mockResolvedValue(Response.json({ taskId: "created" }, { status: 202 }));
    const response = await task(request("/api/local/tasks", "POST", { dataSourceId: source, question: "Inventory lookup" }));
    expect(response.status).toBe(202); expect(fetcher.mock.calls[0][0]).toBe(`${core}/api/tasks`);
    expect(fetcher.mock.calls[0][1].headers.get("Idempotency-Key")).toMatch(/^web-/);
  });
  it("tests a source through opaque browser authority after the Origin fence", async () => {
    expect((await connection(request("/api/local/data-sources", "POST"), { params: Promise.resolve({ dataSourceId: source }) })).status).toBe(200);
    expect(fetcher.mock.calls[0][0]).toBe(`${core}/api/data-sources/${source}/connection-test`);
  });
});
describe("atomic browser logout", () => {
  it("rotates shared generation, revokes pending/active authority and clears SID/transaction/legacy cookies", async () => {
    const response = await logout(request("/api/local/session/logout", "POST")); expect(response.status).toBe(200);
    expect(revoke).toHaveBeenCalledWith(binding); expect(await response.json()).toEqual({ ok: true });
    const cookies = response.headers.getSetCookie(); expect(cookies).toHaveLength(4);
    expect(cookies[0]).toContain(nextBinding); expect(cookies[0]).toContain("Max-Age=86400");
    for (const cookie of cookies) for (const flag of ["HttpOnly", "Secure", "SameSite=lax", "Path=/"]) expect(cookie).toContain(flag);
    for (const cookie of cookies.slice(1)) expect(cookie).toContain("Max-Age=0");
    expect(response.headers.get("cache-control")).toBe("no-store"); expect(JSON.stringify(cookies)).not.toContain(token);
  });
  it.each(["https://foreign.invalid", "null", `${origin}/`])("cannot revoke or clear cookies from Origin %s", async (value) => {
    const response = await logout(request("/api/local/session/logout", "POST", {}, { Origin: value })); expect(response.status).toBe(403);
    expect(response.headers.get("set-cookie")).toBeNull(); expect(mocks.cookies).not.toHaveBeenCalled(); expect(revoke).not.toHaveBeenCalled();
  });
  it("cannot report logout success without a known registered binding", async () => {
    jar.delete(browserBindingCookieName(settings)); const response = await logout(request("/api/local/session/logout", "POST"));
    expect(response.status).toBe(409); expect(response.headers.get("set-cookie")).toBeNull(); expect(revoke).not.toHaveBeenCalled();
  });
  it("cannot overwrite a newer generation when stale logout CAS loses", async () => {
    revoke.mockResolvedValue(null); const response = await logout(request("/api/local/session/logout", "POST"));
    expect(response.status).toBe(409); expect(response.headers.get("set-cookie")).toBeNull();
  });
  it("does not claim logout or clear cookies when Redis revocation is unavailable", async () => {
    revoke.mockRejectedValue(new Error(`private-store-error:${token}`));
    const response = await logout(request("/api/local/session/logout", "POST")); expect(response.status).toBe(503);
    expect(response.headers.get("set-cookie")).toBeNull(); expect(await response.text()).not.toContain(token);
  });
});
