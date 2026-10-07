// @vitest-environment node
import { afterEach, expect, it, vi } from "vitest";
import { reportBrowserProof } from "./browser-proof-diagnostic";
import type { BrowserOidcSettings } from "./browser-oidc";
const local = { publicOrigin: "http://127.0.0.1:3000", localHttp: true } as BrowserOidcSettings;
afterEach(() => { vi.restoreAllMocks(); vi.unstubAllEnvs(); });
it("stays silent by default, for missing settings and for HTTPS production", () => {
  const warn = vi.spyOn(console, "warn").mockImplementation(() => {});
  reportBrowserProof(local, "exchange", new Error("PRIVATE"));
  vi.stubEnv("AIOFFICE_BROWSER_CI_PROOF", "true");
  reportBrowserProof(undefined, "configuration");
  reportBrowserProof({ ...local, publicOrigin: "https://office.invalid", localHttp: false }, "exchange");
  expect(warn).not.toHaveBeenCalled();
});
it("logs only fixed stages and allowlisted codes without message, token, cause or caller code", () => {
  vi.stubEnv("AIOFFICE_BROWSER_CI_PROOF", "true");
  const warn = vi.spyOn(console, "warn").mockImplementation(() => {});
  reportBrowserProof(local, "signed-exchange", { code: "OAUTH_JWT_CLAIM_COMPARISON_FAILED", message: "PRIVATE-TOKEN", cause: { password: "PRIVATE" } });
  reportBrowserProof(local, "exchange", { code: "PRIVATE-PROVIDER-CODE", message: "PRIVATE" });
  expect(warn.mock.calls).toEqual([["[aioffice-browser-proof] signed-exchange OAUTH_JWT_CLAIM_COMPARISON_FAILED"], ["[aioffice-browser-proof] exchange"]]);
});
it("never invokes a code getter or lets hostile error inspection change refusal", () => {
  vi.stubEnv("AIOFFICE_BROWSER_CI_PROOF", "true");
  const warn = vi.spyOn(console, "warn").mockImplementation(() => {});
  const getter = vi.fn(() => { throw new Error("PRIVATE"); });
  reportBrowserProof(local, "exchange", Object.defineProperty({}, "code", { get: getter }));
  reportBrowserProof(local, "exchange", new Proxy({}, { getOwnPropertyDescriptor: getter }));
  expect(getter).toHaveBeenCalledTimes(1);
  expect(warn.mock.calls).toEqual([["[aioffice-browser-proof] exchange"], ["[aioffice-browser-proof] exchange"]]);
});
