import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { renewBrowserSessionSilently } from "./browser-silent-renew";
import type { PublicBrowserLogin } from "./browser-login-navigation";

const company = "22222222-2222-2222-2222-222222222222";
const settings: PublicBrowserLogin = { authorizationEndpoint: "https://identity.example.invalid/authorize",
  clientId: "office", redirectUri: "https://office.example.invalid/api/local/session/oidc/callback", localHttp: false };
function authorization(prompt?: string) {
  const url = new URL(settings.authorizationEndpoint);
  for (const [key, value] of Object.entries({ client_id: settings.clientId, redirect_uri: settings.redirectUri,
    response_type: "code", scope: "openid", code_challenge_method: "S256", state: "A".repeat(43),
    nonce: "B".repeat(43), code_challenge: "C".repeat(43), ...(prompt ? { prompt } : {}) })) url.searchParams.set(key, value);
  return url.href;
}
const fetcher = vi.fn();
beforeEach(() => {
  window.localStorage.clear();
  fetcher.mockReset().mockImplementation(async (url: string) => url === "/api/local/session/oidc/binding"
    ? Response.json({ ok: true }) : Response.json({ authorizationUrl: authorization("none") }));
  vi.stubGlobal("fetch", fetcher);
});
afterEach(() => { vi.unstubAllGlobals(); document.body.innerHTML = ""; });

it("asks for a silent start and loads it only in a hidden frame", async () => {
  const pending = renewBrowserSessionSilently(company, settings, 50);
  await vi.waitFor(() => expect(document.querySelector("iframe")).not.toBeNull());
  const frame = document.querySelector("iframe")!;
  expect(frame.hidden).toBe(true); expect(frame.getAttribute("src")).toBe(authorization("none"));
  expect(fetcher.mock.calls.map(([url]) => url)).toEqual(["/api/local/session/oidc/binding", "/api/local/session/oidc/start?prompt=none"]);
  expect(JSON.parse(fetcher.mock.calls[1][1].body)).toEqual({ companyId: company });
  expect(await pending).toBe("failed");
  expect(document.querySelector("iframe")).toBeNull();
});

it("refuses a start that would show the provider page", async () => {
  fetcher.mockImplementation(async (url: string) => url === "/api/local/session/oidc/binding"
    ? Response.json({ ok: true }) : Response.json({ authorizationUrl: authorization() }));
  expect(await renewBrowserSessionSilently(company, settings)).toBe("failed");
  expect(document.querySelector("iframe")).toBeNull();
});

it("lets only one tab renew at a time", async () => {
  window.localStorage.setItem("aioffice-silent-renew-at", String(Date.now()));
  expect(await renewBrowserSessionSilently(company, settings)).toBe("busy");
  expect(fetcher).not.toHaveBeenCalled();
});
