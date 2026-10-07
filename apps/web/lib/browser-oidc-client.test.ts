// @vitest-environment node
import { createHash, generateKeyPairSync, sign } from "node:crypto";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createOidcTransaction, readBrowserOidcSettings } from "./browser-oidc";
import { browserAuthorizationUrl, exchangeBrowserAuthorizationCode } from "./browser-oidc-client";

const signingKey = generateKeyPairSync("rsa", { modulusLength: 2048 });
const foreignKey = generateKeyPairSync("rsa", { modulusLength: 2048 });
const issuer = "https://identity.example.test/realm";
const client = "browser-office";
const subject = "case-sensitive-provider-subject";
const fetchMock = vi.fn();
const env: Record<string, string> = {
  AIOFFICE_BROWSER_OIDC_ENABLED: "true", AIOFFICE_BROWSER_OIDC_PUBLIC_ORIGIN: "https://office.example.test",
  AIOFFICE_BROWSER_OIDC_ISSUER: issuer, AIOFFICE_BROWSER_OIDC_CLIENT_ID: client,
  AIOFFICE_BROWSER_OIDC_AUTHORIZATION_ENDPOINT: issuer + "/auth",
  AIOFFICE_BROWSER_OIDC_TOKEN_ENDPOINT: issuer + "/token", AIOFFICE_BROWSER_OIDC_JWKS_URI: issuer + "/certs",
  AIOFFICE_BROWSER_OIDC_TRANSACTION_KEY: Buffer.alloc(32, 1).toString("base64url"),
};
const settings = readBrowserOidcSettings((name) => env[name])!;
const transaction = createOidcTransaction("22222222-2222-2222-2222-222222222222");
const callback = () => new URL(`${settings.redirectUri}?state=${transaction.state}&code=owned-code&iss=${encodeURIComponent(issuer)}`);

function jwt(claims: Record<string, unknown>, foreign = false) {
  const data = [Buffer.from(JSON.stringify({ alg: "RS256", kid: "owned-key" })).toString("base64url"),
    Buffer.from(JSON.stringify(claims)).toString("base64url")].join(".");
  return data + "." + sign("RSA-SHA256", Buffer.from(data), foreign ? foreignKey.privateKey : signingKey.privateKey).toString("base64url");
}
function tokenResponse(changes: Record<string, unknown> = {}, foreign = false, responseChanges: Record<string, unknown> = {}) {
  const now = Math.floor(Date.now() / 1000);
  return { token_type: "Bearer", expires_in: 300,
    access_token: jwt({ iss: issuer, aud: "office-api", sub: subject, iat: now, exp: now + 300 }),
    id_token: jwt({ iss: issuer, aud: client, sub: subject, nonce: transaction.nonce, iat: now, exp: now + 300, ...changes }, foreign),
    ...responseChanges };
}
function provider(response: Record<string, unknown>) {
  fetchMock.mockImplementation(async (url: string | URL, options: RequestInit) => {
    expect(options.cache).toBe("no-store");
    expect(options.redirect).toBe("error");
    if (String(url) === settings.jwksUri) return Response.json({ keys: [{ ...signingKey.publicKey.export({ format: "jwk" }), kid: "owned-key", alg: "RS256", use: "sig" }] });
    expect(String(url)).toBe(settings.tokenEndpoint);
    const form = new URLSearchParams(String(options.body));
    expect(form.get("grant_type")).toBe("authorization_code");
    expect(form.get("redirect_uri")).toBe(settings.redirectUri);
    expect(form.get("code_verifier")).toBe(transaction.verifier);
    return Response.json(response);
  });
}
beforeEach(() => { fetchMock.mockReset(); vi.stubGlobal("fetch", fetchMock); });
afterEach(() => vi.unstubAllGlobals());

it("builds only the pinned code/PKCE authorization request without exposing verifier or company", async () => {
  const url = await browserAuthorizationUrl(settings, transaction);
  expect(url.origin + url.pathname).toBe(settings.authorizationEndpoint);
  expect(url.searchParams.get("redirect_uri")).toBe(settings.redirectUri);
  expect(url.searchParams.get("response_type")).toBe("code");
  expect(url.searchParams.get("scope")).toBe("openid");
  expect(url.searchParams.get("state")).toBe(transaction.state);
  expect(url.searchParams.get("nonce")).toBe(transaction.nonce);
  expect(url.searchParams.get("code_challenge_method")).toBe("S256");
  expect(url.searchParams.get("code_challenge")).toBe(createHash("sha256").update(transaction.verifier).digest("base64url"));
  expect(url.href).not.toContain(transaction.verifier);
  expect(url.href).not.toContain(transaction.companyId);
  expect(fetchMock).not.toHaveBeenCalled();
});
it("verifies a real RSA-signed ID token before returning private token data", async () => {
  const tokens = tokenResponse(); provider(tokens);
  const result = await exchangeBrowserAuthorizationCode(settings, callback(), transaction);
  expect(result).toEqual({ accessToken: tokens.access_token, expiresIn: 300, subject });
  expect(Object.isFrozen(result)).toBe(true);
  expect(fetchMock).toHaveBeenCalledTimes(2);
});
it.each([
  { iss: "https://foreign.invalid" }, { aud: "foreign-client" }, { nonce: "foreign-nonce" },
  { exp: 1 }, { sub: "other-subject" }, { sub: "" }, { azp: "foreign-client" }, { aud: [client, "other"], azp: "other" },
  { aud: [client, "untrusted-client"], azp: client },
])("refuses signed tokens with wrong issuer/audience/nonce/lifetime/subject", async (changes) => {
  provider(tokenResponse(changes));
  await expect(exchangeBrowserAuthorizationCode(settings, callback(), transaction)).rejects.toThrow("Browser sign-in could not be verified.");
});
it("refuses a forged RSA signature even with valid issuer/audience/nonce/expiry", async () => {
  provider(tokenResponse({}, true));
  await expect(exchangeBrowserAuthorizationCode(settings, callback(), transaction)).rejects.toThrow("Browser sign-in could not be verified.");
});
it.each([
  { id_token: undefined }, { access_token: "opaque-token" }, { access_token: "x".repeat(16_385) },
  { expires_in: 0 }, { expires_in: 1.5 }, { expires_in: undefined }, { token_type: "DPoP" },
])("refuses invalid token response shape or expiry", async (changes) => {
  provider(tokenResponse({}, false, changes));
  await expect(exchangeBrowserAuthorizationCode(settings, callback(), transaction)).rejects.toThrow("Browser sign-in could not be verified.");
});
it.each(["foreign-state", "", transaction.state + "&state=" + transaction.state])("refuses wrong/missing/duplicate state before provider calls", async (state) => {
  const url = callback(); url.search = "code=owned-code&state=" + state;
  await expect(exchangeBrowserAuthorizationCode(settings, url, transaction)).rejects.toThrow("Browser sign-in could not be verified.");
  expect(fetchMock).not.toHaveBeenCalled();
});
it.each(["https://foreign.invalid/callback", "https://office.example.test/another-callback"])("refuses unpinned callback before provider calls", async (value) => {
  const url = new URL(value); url.search = callback().search;
  await expect(exchangeBrowserAuthorizationCode(settings, url, transaction)).rejects.toThrow("Browser sign-in could not be verified.");
  expect(fetchMock).not.toHaveBeenCalled();
});
it("never reflects transport/provider details", async () => {
  fetchMock.mockRejectedValue(new Error("private-provider-body-and-secret"));
  await expect(exchangeBrowserAuthorizationCode(settings, callback(), transaction)).rejects.toThrow(/^Browser sign-in could not be verified\.$/);
});
it("bounds actual provider response bytes despite an understated length hint", async () => {
  fetchMock.mockResolvedValue(new Response("x".repeat(65_537), { headers: { "Content-Length": "1" } }));
  await expect(exchangeBrowserAuthorizationCode(settings, callback(), transaction)).rejects.toThrow(/^Browser sign-in could not be verified\.$/);
  expect(fetchMock).toHaveBeenCalledTimes(1);
});
it("cannot extend a session beyond the signed ID-token lifetime", async () => {
  provider(tokenResponse({ exp: Math.floor(Date.now() / 1000) + 90 }, false, { expires_in: 7200 }));
  const result = await exchangeBrowserAuthorizationCode(settings, callback(), transaction);
  expect(result.expiresIn).toBeGreaterThan(0);
  expect(result.expiresIn).toBeLessThanOrEqual(90);
});
it("accepts an explicit matching authorized party with the required Bearer token", async () => {
  provider(tokenResponse({ azp: client }));
  await expect(exchangeBrowserAuthorizationCode(settings, callback(), transaction)).resolves.toMatchObject({ subject });
});
it("accepts the configured client as the sole audience in singleton array form", async () => {
  provider(tokenResponse({ aud: [client], azp: client }));
  await expect(exchangeBrowserAuthorizationCode(settings, callback(), transaction)).resolves.toMatchObject({ subject });
});
it.each(["Bearer", "DPoP"])("rejects sender-constrained %s access without a proof contract", async (tokenType) => {
  const now = Math.floor(Date.now() / 1000);
  provider(tokenResponse({}, false, { token_type: tokenType,
    access_token: jwt({ iss: issuer, aud: "office-api", sub: subject, iat: now, exp: now + 300, cnf: { jkt: "foreign-proof-key" } }) }));
  await expect(exchangeBrowserAuthorizationCode(settings, callback(), transaction)).rejects.toThrow("Browser sign-in could not be verified.");
});
