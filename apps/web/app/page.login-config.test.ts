// @vitest-environment node
import { afterEach, expect, it, vi } from "vitest";
import Home from "./page";
afterEach(() => vi.unstubAllEnvs());
it("sends only validated public navigation settings to browser JS", () => {
  for (const [name, value] of Object.entries({ ENABLED: "true", PUBLIC_ORIGIN: "https://office.example.invalid",
    ISSUER: "https://id.example.invalid/realm", AUTHORIZATION_ENDPOINT: "https://id.example.invalid/auth",
    TOKEN_ENDPOINT: "https://private.example.invalid/token", JWKS_URI: "https://private.example.invalid/keys",
    CLIENT_ID: "office", CLIENT_SECRET: "PRIVATE-PROVIDER-SECRET", TRANSACTION_KEY: "A".repeat(43) })) {
    vi.stubEnv("AIOFFICE_BROWSER_OIDC_" + name, value);
  }
  const props = Home().props;
  expect(props.loginMode).toBe("browser");
  expect(props.browserLogin).toEqual({ authorizationEndpoint: "https://id.example.invalid/auth", clientId: "office",
    redirectUri: "https://office.example.invalid/api/local/session/oidc/callback", localHttp: false });
  expect(JSON.stringify(props)).not.toMatch(/PRIVATE-PROVIDER|private\.example|transactionKey|issuer|jwksUri|tokenEndpoint/);
});
it("never falls back to a password form when enabled configuration is invalid", () => {
  vi.stubEnv("AIOFFICE_BROWSER_OIDC_ENABLED", "true"); vi.stubEnv("AIOFFICE_BROWSER_OIDC_PUBLIC_ORIGIN", "invalid");
  expect(Home().props.loginMode).toBe("browser"); expect(Home().props.browserLogin).toBeUndefined();
});
it("retains explicitly selected local UI mode when browser mode is disabled", () => {
  vi.stubEnv("AIOFFICE_BROWSER_OIDC_ENABLED", "false");
  expect(Home().props.loginMode).toBe("local"); expect(Home().props.browserLogin).toBeUndefined();
});
