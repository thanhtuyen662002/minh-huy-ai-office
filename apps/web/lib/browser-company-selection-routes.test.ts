// @vitest-environment node
import { beforeEach, expect, it, vi } from "vitest";
import { GET as callback } from "../app/api/local/session/oidc/callback/route";
import { GET as read, POST as choose, DELETE as cancel } from "../app/api/local/session/oidc/company/route";
import { POST as start } from "../app/api/local/session/oidc/start/route";
import { createCompanyDiscoveryTransaction, oidcTransactionCookieName, openOidcTransaction, readBrowserOidcSettings, sealOidcTransaction } from "./browser-oidc";
import { browserBindingCookieName, browserSessionCookieName } from "./browser-session-store";

const mocks = vi.hoisted(() => ({ runtime: vi.fn(), cookies: vi.fn(), exchange: vi.fn(), discover: vi.fn(), authorize: vi.fn(), legacy: vi.fn() }));
vi.mock("next/headers", () => ({ cookies: mocks.cookies }));
vi.mock("./browser-auth-runtime", () => ({ getBrowserAuthRuntime: mocks.runtime }));
vi.mock("./browser-oidc-client", async (original) => ({ ...await original<typeof import("./browser-oidc-client")>(), exchangeBrowserAuthorizationCode: mocks.exchange }));
vi.mock("./browser-company-preselection", () => ({ discoverBrowserCompanies: mocks.discover, authorizeDiscoveredCompany: mocks.authorize }));
vi.mock("./browser-oidc-authority", () => ({ authorizeBrowserSession: mocks.legacy }));

const origin = "https://office.example.test", company = "22222222-2222-2222-2222-222222222222", other = "33333333-3333-3333-3333-333333333333";
const env: Record<string, string> = {
  AIOFFICE_BROWSER_OIDC_ENABLED: "true", AIOFFICE_BROWSER_OIDC_PUBLIC_ORIGIN: origin,
  AIOFFICE_BROWSER_OIDC_ISSUER: "https://identity.example.test/realm", AIOFFICE_BROWSER_OIDC_CLIENT_ID: "browser-office",
  AIOFFICE_BROWSER_OIDC_AUTHORIZATION_ENDPOINT: "https://identity.example.test/auth",
  AIOFFICE_BROWSER_OIDC_TOKEN_ENDPOINT: "https://identity.example.test/token", AIOFFICE_BROWSER_OIDC_JWKS_URI: "https://identity.example.test/certs",
  AIOFFICE_BROWSER_OIDC_TRANSACTION_KEY: Buffer.alloc(32, 8).toString("base64url"),
};
const settings = readBrowserOidcSettings((key) => env[key])!;
const binding = `${Buffer.alloc(32, 1).toString("base64url")}.${Buffer.alloc(32, 2).toString("base64url")}`;
const sid = Buffer.alloc(32, 3).toString("base64url"), choices = [{ companyId: company, companyName: "Company A" }, { companyId: other, companyName: "Company B" }];
const jar = new Map<string, string>(), events: string[] = [];
let transaction = createCompanyDiscoveryTransaction();
const tokens = () => ({ accessToken: "private-token", subject: "private-subject", expiresAt: Math.floor(Date.now() / 1000) + 600, expiresIn: 600 });
const selection = () => ({ accessToken: tokens().accessToken, subject: tokens().subject, expiresAt: tokens().expiresAt, choices });
const sessions = { begin: vi.fn(), claim: vi.fn(), stageSelection: vi.fn(), readSelection: vi.fn(), claimSelection: vi.fn(), complete: vi.fn(), cancelSelection: vi.fn() };
function request(method: string, body?: unknown, headers: Record<string, string> = {}) {
  return new Request(origin + "/api/local/session/oidc/company", { method,
    headers: { Host: "office.example.test", Origin: origin, "Content-Type": "application/json", ...headers },
    ...(body === undefined ? {} : { body: JSON.stringify(body) }) });
}
function callbackRequest() { return new Request(`${settings.redirectUri}?code=private-code&state=${transaction.state}`, { headers: { Host: "office.example.test" } }); }
beforeEach(() => {
  vi.clearAllMocks(); jar.clear(); events.length = 0; transaction = createCompanyDiscoveryTransaction();
  jar.set(browserBindingCookieName(settings), binding);
  jar.set(oidcTransactionCookieName(settings), sealOidcTransaction(transaction, settings));
  jar.set(browserSessionCookieName(settings), "old-working-sid");
  mocks.cookies.mockResolvedValue({ get: (key: string) => jar.has(key) ? { value: jar.get(key) } : undefined });
  mocks.runtime.mockReturnValue({ settings, coreApiOrigin: "https://core.example.test", sessions });
  sessions.begin.mockResolvedValue(binding); sessions.claim.mockImplementation(async () => { events.push("callback-claim"); return "callback-claim"; });
  mocks.exchange.mockImplementation(async () => { events.push("signed-exchange"); return tokens(); });
  mocks.discover.mockImplementation(async () => { events.push("fresh-directory"); return choices; });
  mocks.authorize.mockImplementation(async (_settings, _tx, value, target) => {
    events.push("fresh-context"); return { session: { accessToken: value.accessToken, subject: value.subject, expiresAt: value.expiresAt, companyId: target } };
  });
  sessions.stageSelection.mockResolvedValue(true); sessions.readSelection.mockResolvedValue(selection());
  sessions.claimSelection.mockImplementation(async () => { events.push("selection-claim"); return { claim: "selection-claim", selection: selection() }; });
  sessions.complete.mockImplementation(async () => { events.push("publish"); return sid; }); sessions.cancelSelection.mockResolvedValue(true);
});
function noCookies(response: Response) { expect(response.headers.get("set-cookie")).toBeNull(); expect(response.headers.get("cache-control")).toBe("no-store"); }

it("ordinary discovery start creates v2 without accepting company/tenant/role authority", async () => {
  const response = await start(request("POST", { discoverCompanies: true }));
  expect(response.status).toBe(200);
  const tx = sessions.begin.mock.calls[0][1]; expect(tx.version).toBe(2); expect(tx.companyId).toBeNull();
  expect(openOidcTransaction(sealOidcTransaction(tx, settings), settings)).toEqual(tx);
});
it.each([{ discoverCompanies: false }, { discoverCompanies: true, companyId: company }, { discoverCompanies: true, tenantId: company }, { discoverCompanies: "true" }])(
  "refuses mixed authority and malformed discovery input before beginning", async (body) => {
    expect((await start(request("POST", body))).status).toBe(400); expect(sessions.begin).not.toHaveBeenCalled();
  });
it("auto-selects the only nondefault eligible company after signed exchange and current Core context", async () => {
  mocks.discover.mockResolvedValue([choices[1]]);
  const response = await callback(callbackRequest()); expect(response.status).toBe(303);
  expect(mocks.authorize.mock.calls[0][3]).toBe(other);
  expect(events).toEqual(["callback-claim", "signed-exchange", "fresh-context", "publish"]);
  expect(sessions.complete.mock.calls[0][1]).toEqual(transaction);
  expect(response.headers.get("set-cookie")).toContain(sid);
  expect(response.headers.get("set-cookie")).not.toContain(browserBindingCookieName(settings));
  expect(mocks.legacy).not.toHaveBeenCalled();
});
it("multiple choices issue no SID and retain only private server preselection", async () => {
  const response = await callback(callbackRequest()); expect(response.status).toBe(303);
  expect(response.headers.get("location")).toBe(origin + "/sign-in/company"); noCookies(response);
  expect(sessions.stageSelection).toHaveBeenCalledWith(binding, transaction, "callback-claim", selection());
  expect(mocks.authorize).not.toHaveBeenCalled(); expect(sessions.complete).not.toHaveBeenCalled();
});
it("no eligible company gives bounded actionable denial and preserves an old SID", async () => {
  mocks.discover.mockResolvedValue([]); const response = await callback(callbackRequest());
  expect(response.status).toBe(403); noCookies(response); expect(await response.text()).toContain("quản trị viên");
  expect(sessions.complete).not.toHaveBeenCalled(); expect(sessions.stageSelection).not.toHaveBeenCalled();
});
it("directory ambiguity and unknown Redis staging replies cannot issue or clear cookies", async () => {
  mocks.discover.mockRejectedValueOnce(new Error("private-details")); noCookies(await callback(callbackRequest()));
  sessions.stageSelection.mockResolvedValue(false); noCookies(await callback(callbackRequest())); expect(sessions.complete).not.toHaveBeenCalled();
});
it("chooser only exposes names and identifiers, without roles, subject, token or old private workspace", async () => {
  const response = await read(request("GET")); expect(response.status).toBe(200);
  expect(await response.json()).toEqual({ items: choices }); noCookies(response);
  expect(mocks.authorize).not.toHaveBeenCalled(); expect(sessions.complete).not.toHaveBeenCalled();
});
it("selection is claimed before fresh directory and context checks and one publication", async () => {
  const response = await choose(request("POST", { companyId: other })); expect(response.status).toBe(200);
  expect(events).toEqual(["selection-claim", "fresh-directory", "fresh-context", "publish"]);
  expect(await response.json()).toEqual({ redirectTo: "/" });
  expect(response.headers.get("set-cookie")).toContain(sid);
  expect(response.headers.get("set-cookie")).not.toContain(browserBindingCookieName(settings));
});
it("retained-list tampering, revocation and fresh target-context denial cannot issue", async () => {
  const foreign = "44444444-4444-4444-4444-444444444444";
  noCookies(await choose(request("POST", { companyId: foreign }))); expect(mocks.discover).not.toHaveBeenCalled();
  mocks.discover.mockResolvedValue([choices[0]]); expect((await choose(request("POST", { companyId: other }))).status).toBe(403);
  mocks.discover.mockResolvedValue(choices); mocks.authorize.mockRejectedValue(new Error("private-details"));
  noCookies(await choose(request("POST", { companyId: other }))); expect(sessions.complete).not.toHaveBeenCalled();
});
it("replay, canceled generation, expired selection or unknown claim response cannot reissue", async () => {
  sessions.claimSelection.mockResolvedValue(null); noCookies(await choose(request("POST", { companyId: company })));
  sessions.claimSelection.mockRejectedValue(new Error("private-redis")); noCookies(await choose(request("POST", { companyId: company })));
  expect(mocks.authorize).not.toHaveBeenCalled(); expect(sessions.complete).not.toHaveBeenCalled();
});
it("logout/new-login completion refusal preserves all browser cookies", async () => {
  sessions.complete.mockResolvedValue(null); noCookies(await choose(request("POST", { companyId: company })));
  sessions.complete.mockRejectedValue(new Error("unknown-write-reply")); noCookies(await choose(request("POST", { companyId: company })));
});
it("cancel invalidates the pending selection without SID/binding mutation", async () => {
  const response = await cancel(request("DELETE")); expect(response.status).toBe(200); noCookies(response);
  expect(sessions.cancelSelection).toHaveBeenCalledWith(binding, transaction); expect(sessions.complete).not.toHaveBeenCalled();
});
it.each([choose, cancel])("rejects foreign Origin before claim/cancel or cookie mutation", async (route) => {
  const response = await route(request(route === choose ? "POST" : "DELETE", undefined, { Origin: "https://foreign.example.test" }));
  expect(response.status).toBe(401); noCookies(response);
  expect(sessions.claimSelection).not.toHaveBeenCalled(); expect(sessions.cancelSelection).not.toHaveBeenCalled();
});
it("rejects missing transaction, foreign raw Host and browser query selectors", async () => {
  noCookies(await read(request("GET", undefined, { Host: "foreign.example.test" })));
  noCookies(await read(new Request(origin + "/api/local/session/oidc/company?companyId=" + company, { headers: { Host: "office.example.test" } })));
  jar.delete(oidcTransactionCookieName(settings)); noCookies(await read(request("GET")));
  expect(sessions.readSelection).not.toHaveBeenCalled();
});
