// @vitest-environment node
import { afterEach, expect, it, vi } from "vitest";
import { getBrowserAuthRuntime } from "./browser-auth-runtime";

const env: Record<string, string> = {
  AIOFFICE_BROWSER_OIDC_ENABLED: "true", AIOFFICE_BROWSER_OIDC_PUBLIC_ORIGIN: "https://office.example.test",
  AIOFFICE_BROWSER_OIDC_ISSUER: "https://identity.example.test/realm", AIOFFICE_BROWSER_OIDC_CLIENT_ID: "browser-office",
  AIOFFICE_BROWSER_OIDC_AUTHORIZATION_ENDPOINT: "https://identity.example.test/auth",
  AIOFFICE_BROWSER_OIDC_TOKEN_ENDPOINT: "https://identity.example.test/token", AIOFFICE_BROWSER_OIDC_JWKS_URI: "https://identity.example.test/certs",
  AIOFFICE_BROWSER_OIDC_TRANSACTION_KEY: Buffer.alloc(32, 5).toString("base64url"),
  AIOFFICE_BROWSER_CORE_API_ORIGIN: "https://core.example.test", AIOFFICE_BROWSER_SESSION_REDIS_URL: "rediss://redis.example.test:6380",
};
function configure() { for (const [name, value] of Object.entries(env)) vi.stubEnv(name, value); }
afterEach(() => {
  vi.stubEnv("AIOFFICE_BROWSER_OIDC_ENABLED", "false"); getBrowserAuthRuntime(); vi.unstubAllEnvs();
});
it("has no enabled runtime or connection before explicit operator configuration", () => {
  vi.stubEnv("AIOFFICE_BROWSER_OIDC_ENABLED", "false");
  expect(getBrowserAuthRuntime()).toBeNull();
});
it("reuses only transport/configuration and never connects on construction", () => {
  configure();
  const runtime = getBrowserAuthRuntime()!;
  expect(runtime.settings.publicOrigin).toBe(env.AIOFFICE_BROWSER_OIDC_PUBLIC_ORIGIN);
  expect(runtime.coreApiOrigin).toBe(env.AIOFFICE_BROWSER_CORE_API_ORIGIN);
  expect(Object.isFrozen(runtime)).toBe(true);
  expect(getBrowserAuthRuntime()).toBe(runtime);
});
it.each(["AIOFFICE_BROWSER_CORE_API_ORIGIN", "AIOFFICE_BROWSER_SESSION_REDIS_URL", "AIOFFICE_BROWSER_OIDC_TRANSACTION_KEY"])("fails closed and discards cached transport when required %s becomes invalid", (name) => {
  configure(); const previous = getBrowserAuthRuntime()!;
  vi.stubEnv(name, "private-invalid-config");
  expect(() => getBrowserAuthRuntime()).toThrow(/^Browser sign-in is unavailable\.$/);
  vi.stubEnv(name, env[name]);
  expect(getBrowserAuthRuntime()).not.toBe(previous);
});
it.each(["AIOFFICE_BROWSER_CORE_API_ORIGIN", "AIOFFICE_BROWSER_SESSION_REDIS_URL", "AIOFFICE_BROWSER_OIDC_PUBLIC_ORIGIN", "AIOFFICE_BROWSER_OIDC_ISSUER"])("replaces the snapshot when the operator changes %s", (name) => {
  configure(); const previous = getBrowserAuthRuntime()!;
  const newValue = name === "AIOFFICE_BROWSER_SESSION_REDIS_URL" ? "rediss://replacement.example.test:6380"
    : name === "AIOFFICE_BROWSER_OIDC_ISSUER" ? "https://replacement.example.test/realm" : "https://replacement.example.test";
  vi.stubEnv(name, newValue);
  expect(getBrowserAuthRuntime()).not.toBe(previous);
});
it("closes cached runtime on disable and recreates it on explicit re-enable", () => {
  configure(); const previous = getBrowserAuthRuntime()!;
  vi.stubEnv("AIOFFICE_BROWSER_OIDC_ENABLED", "false");
  expect(getBrowserAuthRuntime()).toBeNull();
  vi.stubEnv("AIOFFICE_BROWSER_OIDC_ENABLED", "true");
  expect(getBrowserAuthRuntime()).not.toBe(previous);
});
