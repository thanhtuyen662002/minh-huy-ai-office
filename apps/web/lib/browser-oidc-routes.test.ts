// @vitest-environment node
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { POST as prepare } from "../app/api/local/session/oidc/binding/route";
import { POST as start } from "../app/api/local/session/oidc/start/route";
import { GET as callback } from "../app/api/local/session/oidc/callback/route";
import { createOidcTransaction, OIDC_CALLBACK_PATH, oidcTransactionCookieName, openOidcTransaction,
  readBrowserOidcSettings, sealOidcTransaction } from "./browser-oidc";
import { browserBindingCookieName, browserSessionCookieName } from "./browser-session-store";
import { readBrowserCompany } from "./browser-auth-http";

const mocks = vi.hoisted(() => ({ runtime: vi.fn(), cookies: vi.fn(), exchange: vi.fn(), authorize: vi.fn() }));
vi.mock("next/headers", () => ({ cookies: mocks.cookies }));
vi.mock("./browser-auth-runtime", () => ({ getBrowserAuthRuntime: mocks.runtime }));
vi.mock("./browser-oidc-client", async (original) => ({
  ...await original<typeof import("./browser-oidc-client")>(), exchangeBrowserAuthorizationCode: mocks.exchange,
}));
vi.mock("./browser-oidc-authority", () => ({ authorizeBrowserSession: mocks.authorize }));

const companyId = "22222222-2222-2222-2222-222222222222";
const origin = "https://office.example.test";
const environment: Record<string, string> = {
  AIOFFICE_BROWSER_OIDC_ENABLED: "true", AIOFFICE_BROWSER_OIDC_PUBLIC_ORIGIN: origin,
  AIOFFICE_BROWSER_OIDC_ISSUER: "https://identity.example.test/realm",
  AIOFFICE_BROWSER_OIDC_AUTHORIZATION_ENDPOINT: "https://identity.example.test/auth",
  AIOFFICE_BROWSER_OIDC_TOKEN_ENDPOINT: "https://identity.example.test/token",
  AIOFFICE_BROWSER_OIDC_JWKS_URI: "https://identity.example.test/certs",
  AIOFFICE_BROWSER_OIDC_CLIENT_ID: "browser-office",
  AIOFFICE_BROWSER_OIDC_TRANSACTION_KEY: Buffer.alloc(32, 7).toString("base64url"),
};
let settings = readBrowserOidcSettings((name) => environment[name])!;
const binding = `${Buffer.alloc(32, 1).toString("base64url")}.${Buffer.alloc(32, 2).toString("base64url")}`;
const nextBinding = `${binding.split(".")[0]}.${Buffer.alloc(32, 3).toString("base64url")}`;
const sid = Buffer.alloc(32, 4).toString("base64url");
const secretToken = "private.access.token", secretSubject = "private-subject", privateDetails = "private-provider-error";
const jar = new Map<string, string>();
const events: string[] = [];
const sessions = { register: vi.fn(), isCurrent: vi.fn(), begin: vi.fn(), claim: vi.fn(), complete: vi.fn() };
let transaction = createOidcTransaction(companyId);

function runtime() { return { settings, coreApiOrigin: "https://captured-core.example.test", sessions }; }
function post(path = "start", body: BodyInit | undefined = JSON.stringify({ companyId }), headers: Record<string, string> = {}) {
  return new Request(`${settings.publicOrigin}/api/local/session/oidc/${path}`, {
    method: "POST", headers: { Origin: settings.publicOrigin, Host: new URL(settings.publicOrigin).host,
      "Content-Type": "application/json", ...headers }, body,
  });
}
function incoming(query = `code=private-code&state=${transaction.state}`, host = new URL(settings.publicOrigin).host,
  requestOrigin = settings.publicOrigin) {
  return new Request(`${requestOrigin}${OIDC_CALLBACK_PATH}?${query}`, { headers: { Host: host } });
}
function pending() {
  jar.set(browserBindingCookieName(settings), binding);
  jar.set(oidcTransactionCookieName(settings), sealOidcTransaction(transaction, settings));
}
async function refusal(response: Response, status: number) {
  expect(response.status).toBe(status);
  expect(response.headers.get("set-cookie")).toBeNull();
  expect(response.headers.get("location")).toBeNull();
  expect(response.headers.get("cache-control")).toBe("no-store");
  expect(response.headers.get("referrer-policy")).toBe("no-referrer");
  expect(await response.json()).toEqual({ error: "Browser sign-in could not be verified." });
}
function useLocalSettings() {
  settings = readBrowserOidcSettings((name) => ({ ...environment,
    AIOFFICE_BROWSER_OIDC_LOCAL_HTTP: "true", AIOFFICE_BROWSER_OIDC_PUBLIC_ORIGIN: "http://127.0.0.1:3000",
    AIOFFICE_BROWSER_OIDC_ISSUER: "http://127.0.0.1:8081/realm",
    AIOFFICE_BROWSER_OIDC_AUTHORIZATION_ENDPOINT: "http://127.0.0.1:8081/auth",
    AIOFFICE_BROWSER_OIDC_TOKEN_ENDPOINT: "http://identity:8080/token",
    AIOFFICE_BROWSER_OIDC_JWKS_URI: "http://identity:8080/certs",
  })[name])!;
}
beforeEach(() => {
  vi.clearAllMocks(); jar.clear(); events.length = 0;
  settings = readBrowserOidcSettings((name) => environment[name])!;
  transaction = createOidcTransaction(companyId);
  mocks.runtime.mockImplementation(runtime);
  mocks.cookies.mockImplementation(async () => ({ get: (name: string) => jar.has(name) ? { value: jar.get(name) } : undefined }));
  sessions.register.mockResolvedValue(binding); sessions.isCurrent.mockResolvedValue(true);
  sessions.begin.mockResolvedValue(nextBinding);
  sessions.claim.mockImplementation(async () => { events.push("claim"); return "private-claim"; });
  mocks.exchange.mockImplementation(async () => {
    events.push("exchange"); return { accessToken: secretToken, subject: secretSubject, expiresIn: 300,
      expiresAt: Math.floor(Date.now() / 1000) + 300 };
  });
  mocks.authorize.mockImplementation(async (_settings, tx, tokens) => {
    events.push("core"); return { context: { companyId: tx.companyId, roles: ["admin"] },
      session: { accessToken: tokens.accessToken, subject: tokens.subject, companyId: tx.companyId, expiresAt: tokens.expiresAt } };
  });
  sessions.complete.mockImplementation(async () => { events.push("publish"); return sid; });
});
afterEach(() => { vi.useRealTimers(); vi.unstubAllEnvs(); });

describe("browser preparation and start", () => {
  it.each([prepare, start])("has no cookies or store access when disabled", async (route) => {
    mocks.runtime.mockReturnValue(null); await refusal(await route(post()), 503);
    expect(mocks.cookies).not.toHaveBeenCalled(); expect(sessions.register).not.toHaveBeenCalled();
  });
  it.each(["null", "https://foreign.example.test", `${origin}/`, `${origin}, https://foreign.example.test`])(
    "refuses Origin %s before body, cookies or store", async (value) => {
      const request = post("start", "{", { Origin: value });
      await refusal(await start(request), 403); expect(request.bodyUsed).toBe(false);
      await refusal(await prepare(post("binding", undefined, { Origin: value })), 403);
      expect(mocks.cookies).not.toHaveBeenCalled(); expect(sessions.register).not.toHaveBeenCalled();
      expect(sessions.begin).not.toHaveBeenCalled();
    });
  it("refuses a missing Origin and raw Host alias despite forwarded authority", async () => {
    const missing = post(); missing.headers.delete("Origin"); await refusal(await start(missing), 403);
    await refusal(await prepare(post("binding", undefined, { Host: "office.example.test/a/..", "X-Forwarded-Host": "office.example.test" })), 403);
    expect(mocks.cookies).not.toHaveBeenCalled();
  });
  it("registers a fresh binding privately without granting a session", async () => {
    const response = await prepare(post("binding"));
    expect(await response.json()).toEqual({ ok: true });
    expect(sessions.register).toHaveBeenCalledOnce(); expect(sessions.complete).not.toHaveBeenCalled();
    const cookie = response.headers.get("set-cookie")!;
    expect(cookie).toContain(`${browserBindingCookieName(settings)}=${binding}`);
    for (const flag of ["HttpOnly", "Secure", "SameSite=lax", "Path=/", "Max-Age=86400"]) expect(cookie).toContain(flag);
    expect(cookie).not.toContain(browserSessionCookieName(settings));
  });
  it("preserves a current registered binding without rewriting cookies", async () => {
    pending(); const response = await prepare(post("binding"));
    expect(await response.json()).toEqual({ ok: true }); expect(response.headers.get("set-cookie")).toBeNull();
    expect(sessions.isCurrent).toHaveBeenCalledWith(binding); expect(sessions.register).not.toHaveBeenCalled();
  });
  it("replaces a stale binding only with a newly registered identity", async () => {
    pending(); sessions.isCurrent.mockResolvedValue(false); sessions.register.mockResolvedValue(nextBinding);
    const response = await prepare(post("binding"));
    expect(response.headers.get("set-cookie")).toContain(nextBinding);
    expect(sessions.register).toHaveBeenCalledWith(); expect(await response.json()).toEqual({ ok: true });
  });
  it("fails generically on store outage without fallback binding", async () => {
    pending(); sessions.isCurrent.mockRejectedValue(new Error(privateDetails));
    await refusal(await prepare(post("binding")), 503); expect(sessions.register).not.toHaveBeenCalled();
  });
  it.each(["{", "null", "[]", "{}", JSON.stringify({ companyId, roles: ["admin"] }),
    JSON.stringify({ companyId: "00000000-0000-0000-0000-000000000000" }), JSON.stringify({ companyId: "bad-company" })])(
    "refuses invalid company payload %s before any store/cookie access", async (body) => {
      await refusal(await start(post("start", body)), 400);
      expect(mocks.cookies).not.toHaveBeenCalled(); expect(sessions.begin).not.toHaveBeenCalled();
    });
  it("requires JSON media and bounds actual input bytes despite false Content-Length", async () => {
    await refusal(await start(post("start", JSON.stringify({ companyId }), { "Content-Type": "text/plain" })), 400);
    await refusal(await start(post("start", JSON.stringify({ companyId }) + " ".repeat(2048), { "Content-Length": "1" })), 400);
    await refusal(await start(post("start", new Uint8Array([0xff, 0xfe]))), 400);
    expect(sessions.begin).not.toHaveBeenCalled();
  });
  it("bounds a stalled request body and cancels its reader", async () => {
    vi.useFakeTimers(); const cancel = vi.fn();
    const body = new ReadableStream<Uint8Array>({ start() {}, cancel });
    const request = new Request(`${origin}/start`, { method: "POST", headers: { "Content-Type": "application/json" },
      body, duplex: "half" } as RequestInit);
    const result = readBrowserCompany(request); await vi.advanceTimersByTimeAsync(10_000);
    expect(await result).toBeNull(); expect(cancel).toHaveBeenCalledOnce();
  });
  it("requires settled preparation and refuses a stale compare-and-swap", async () => {
    await refusal(await start(post()), 409); expect(sessions.begin).not.toHaveBeenCalled();
    pending(); sessions.begin.mockResolvedValue(null); await refusal(await start(post()), 409);
    expect(sessions.begin).toHaveBeenCalledOnce(); expect(mocks.exchange).not.toHaveBeenCalled();
  });
  it("returns only the pinned authorization URL and encrypted transaction after advancing the binding", async () => {
    pending(); const response = await start(post());
    expect(response.status).toBe(200); expect(response.headers.get("cache-control")).toBe("no-store");
    const body = await response.json(); expect(Object.keys(body)).toEqual(["authorizationUrl"]);
    const url = new URL(body.authorizationUrl);
    expect(url.origin + url.pathname).toBe(settings.authorizationEndpoint);
    expect(url.searchParams.get("redirect_uri")).toBe(settings.redirectUri);
    expect(url.searchParams.get("code_challenge_method")).toBe("S256");
    const tx = sessions.begin.mock.calls[0][1];
    expect(url.searchParams.get("state")).toBe(tx.state); expect(url.searchParams.get("nonce")).toBe(tx.nonce);
    expect(JSON.stringify(body)).not.toContain(tx.verifier); expect(JSON.stringify(body)).not.toContain(companyId);
    const cookies = response.headers.getSetCookie(); expect(cookies).toHaveLength(2);
    expect(cookies[0]).toContain(nextBinding);
    const sealed = cookies[1].split(";")[0].slice(oidcTransactionCookieName(settings).length + 1);
    expect(openOidcTransaction(sealed, settings)).toEqual(tx);
    for (const cookie of cookies) for (const flag of ["HttpOnly", "Secure", "SameSite=lax", "Path=/"]) expect(cookie).toContain(flag);
    expect(mocks.exchange).not.toHaveBeenCalled(); expect(mocks.authorize).not.toHaveBeenCalled();
  });
});

describe("browser callback authority and publication", () => {
  it.each([undefined, "tampered", "x".repeat(2049)])("refuses missing/corrupt/oversized transaction %s before claim/exchange", async (value) => {
    pending(); if (value === undefined) jar.delete(oidcTransactionCookieName(settings));
    else jar.set(oidcTransactionCookieName(settings), value);
    await refusal(await callback(incoming()), 401); expect(sessions.claim).not.toHaveBeenCalled();
    expect(mocks.exchange).not.toHaveBeenCalled();
  });
  it("refuses missing binding and expired/future transactions before claim", async () => {
    // Keep the future +11s case outside the real +10s allowance even when
    // a busy runner crosses a wall-clock second before decoding the cookie.
    vi.useFakeTimers({ toFake: ["Date"] }); vi.setSystemTime(new Date("2026-01-01T00:00:00Z"));
    pending(); jar.delete(browserBindingCookieName(settings)); await refusal(await callback(incoming()), 401);
    for (const offset of [-300, 11]) {
      transaction = createOidcTransaction(companyId, Math.floor(Date.now() / 1000) + offset); pending();
      await refusal(await callback(incoming()), 401);
    }
    expect(sessions.claim).not.toHaveBeenCalled();
  });
  it.each(["foreign.example.test", "office.example.test/a/..", "office.example.test:443", ""]) (
    "refuses raw Host %s before cookies and ignores forwarded authority", async (host) => {
      pending(); const request = incoming(undefined, host); request.headers.set("X-Forwarded-Host", "office.example.test");
      await refusal(await callback(request), 403); expect(mocks.cookies).not.toHaveBeenCalled();
      expect(sessions.claim).not.toHaveBeenCalled();
    });
  it.each(["code=private-code", "state=wrong&code=private-code", "code=private-code&state={state}&state={state}",
    "state={state}", "code=&state={state}", "code=a&code=b&state={state}", "code=a&state={state}&error=provider-secret",
    "code=a&state={state}&iss=https%3A%2F%2Fforeign.example.test", "code=a&state={state}&iss=a&iss=b"])(
    "refuses malformed provider query before claiming or exchanging: %s", async (query) => {
      pending(); await refusal(await callback(incoming(query.replaceAll("{state}", transaction.state))), 401);
      expect(sessions.claim).not.toHaveBeenCalled(); expect(mocks.exchange).not.toHaveBeenCalled();
    });
  it("refuses replay, superseded generation or logout before the provider exchange", async () => {
    pending(); sessions.claim.mockResolvedValue(null); await refusal(await callback(incoming()), 401);
    expect(mocks.exchange).not.toHaveBeenCalled(); expect(mocks.authorize).not.toHaveBeenCalled();
    expect(sessions.complete).not.toHaveBeenCalled();
  });
  it.each(["exchange", "core", "publish"]) ("preserves existing cookies on private %s failure", async (step) => {
    pending(); (step === "exchange" ? mocks.exchange : step === "core" ? mocks.authorize : sessions.complete)
      .mockRejectedValue(new Error(`${privateDetails}:${secretToken}`));
    await refusal(await callback(incoming()), 401);
    if (step === "exchange") expect(mocks.authorize).not.toHaveBeenCalled();
    if (step !== "publish") expect(sessions.complete).not.toHaveBeenCalled();
  });
  it("refuses completion if logout/supersession won while provider/Core work was pending", async () => {
    pending(); sessions.complete.mockResolvedValue(null); await refusal(await callback(incoming()), 401);
    expect(events).toEqual(["claim", "exchange", "core"]);
  });
  it("issues only opaque SID after one-use claim, signed exchange, fresh Core and atomic completion", async () => {
    pending(); const response = await callback(incoming(`code=private-code&state=${transaction.state}&iss=${encodeURIComponent(settings.issuer)}`));
    expect(events).toEqual(["claim", "exchange", "core", "publish"]);
    expect(response.status).toBe(303); expect(response.headers.get("location")).toBe(`${origin}/`);
    expect(response.headers.get("cache-control")).toBe("no-store");
    expect(response.headers.get("referrer-policy")).toBe("no-referrer");
    const cookie = response.headers.get("set-cookie")!;
    expect(cookie).toContain(`${browserSessionCookieName(settings)}=${sid}`);
    expect(cookie).toContain(`${oidcTransactionCookieName(settings)}=;`); expect(cookie).toContain("Max-Age=0");
    for (const flag of ["HttpOnly", "Secure", "SameSite=lax", "Path=/"]) expect(cookie).toContain(flag);
    expect(cookie).not.toContain(browserBindingCookieName(settings));
    expect(cookie).not.toContain(secretToken); expect(cookie).not.toContain(secretSubject); expect(cookie).not.toContain(companyId);
    expect(await response.text()).not.toContain(secretToken);
    expect(sessions.complete).toHaveBeenCalledWith(binding, transaction, "private-claim", expect.objectContaining({ accessToken: secretToken, companyId }));
  });
  it("pins provider callback/Core origin despite internal request URL and later environment changes", async () => {
    pending(); vi.stubEnv("AIOFFICE_BROWSER_CORE_API_ORIGIN", "https://untrusted-later.example.test");
    const response = await callback(incoming(undefined, "office.example.test", "http://web:3000"));
    expect(response.status).toBe(303);
    expect(mocks.exchange.mock.calls[0][1].origin).toBe(origin);
    const readEnv = mocks.authorize.mock.calls[0][4];
    expect(readEnv("AIOFFICE_BROWSER_CORE_API_ORIGIN")).toBe(runtime().coreApiOrigin);
    expect(readEnv("unrelated")).toBeUndefined(); expect(response.headers.get("location")).toBe(`${origin}/`);
  });
  it("does not issue an expired SID even if a stale completion reports success", async () => {
    pending(); sessions.complete.mockImplementation(async () => {
      vi.useFakeTimers(); vi.setSystemTime(Date.now() + 301_000); return sid;
    });
    await refusal(await callback(incoming()), 401);
  });
  it("uses explicit loopback cookies without weakening deployed Secure cookies", async () => {
    useLocalSettings(); pending(); const response = await callback(incoming());
    expect(response.status).toBe(303); const cookie = response.headers.get("set-cookie")!;
    expect(cookie).toContain(`aioffice_browser_session=${sid}`); expect(cookie).not.toContain("Secure");
    expect(cookie).toContain("HttpOnly"); expect(cookie).toContain("SameSite=lax"); expect(cookie).toContain("Path=/");
  });
});
