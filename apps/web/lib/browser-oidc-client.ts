import * as oidc from "openid-client";
import type { BrowserOidcSettings, OidcTransaction } from "./browser-oidc";
import { reportBrowserProof } from "./browser-proof-diagnostic";

function configuration(settings: BrowserOidcSettings) {
  const config = new oidc.Configuration({
    issuer: settings.issuer,
    authorization_endpoint: settings.authorizationEndpoint,
    token_endpoint: settings.tokenEndpoint,
    jwks_uri: settings.jwksUri,
  }, settings.clientId, {
    id_token_signed_response_alg: "RS256",
    ...(settings.clientSecret ? { client_secret: settings.clientSecret } : {}),
  }, settings.clientSecret ? oidc.ClientSecretBasic(settings.clientSecret) : oidc.None());
  if (settings.localHttp) oidc.allowInsecureRequests(config);
  // Token-endpoint TLS alone must never replace checking the ID-token JWS.
  oidc.enableNonRepudiationChecks(config);
  config.timeout = 10;
  config[oidc.customFetch] = async (url, options) => {
    const body = options.body instanceof Uint8Array ? new Uint8Array(options.body).buffer : options.body;
    const response = await fetch(url, { ...options, body, cache: "no-store", redirect: "error" });
    const reader = response.body?.getReader();
    if (!reader) return response;
    const chunks: Uint8Array[] = [];
    let length = 0;
    try {
      while (true) {
        const chunk = await reader.read();
        if (chunk.done) break;
        length += chunk.value.byteLength;
        if (length > 65_536) {
          try { await reader.cancel(); } catch { /* Refusal survives failed stream cleanup. */ }
          throw new Error("Identity response is too large.");
        }
        chunks.push(chunk.value);
      }
    } finally { reader.releaseLock(); }
    const bytes = new Uint8Array(length);
    let offset = 0;
    for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.byteLength; }
    return new Response(bytes, { status: response.status, statusText: response.statusText, headers: response.headers });
  };
  return config;
}

export async function browserAuthorizationUrl(settings: BrowserOidcSettings, transaction: OidcTransaction): Promise<URL> {
  return oidc.buildAuthorizationUrl(configuration(settings), {
    response_type: "code", scope: "openid", redirect_uri: settings.redirectUri,
    state: transaction.state, nonce: transaction.nonce,
    code_challenge: await oidc.calculatePKCECodeChallenge(transaction.verifier), code_challenge_method: "S256",
  });
}

export type VerifiedBrowserTokens = Readonly<{ accessToken: string; expiresIn: number; expiresAt: number; subject: string }>;

// The caller still must consume the shared transaction/session generation and
// resolve fresh Core company authority before it can issue a session cookie.
export async function exchangeBrowserAuthorizationCode(
  settings: BrowserOidcSettings, callback: URL, transaction: OidcTransaction,
): Promise<VerifiedBrowserTokens> {
  try {
    if (callback.origin + callback.pathname !== settings.redirectUri || callback.hash || callback.href.length > 8192
      || callback.searchParams.getAll("state").length !== 1 || callback.searchParams.get("state") !== transaction.state
      || callback.searchParams.getAll("code").length !== 1 || !callback.searchParams.get("code")
      || callback.searchParams.has("error")) throw new Error();
    const tokens = await oidc.authorizationCodeGrant(configuration(settings), callback, {
      expectedState: transaction.state, expectedNonce: transaction.nonce, pkceCodeVerifier: transaction.verifier, idTokenExpected: true,
    });
    const idClaims = tokens.claims();
    const subject = idClaims?.sub;
    const audience = idClaims?.aud;
    const exactAudience = audience === settings.clientId
      || (Array.isArray(audience) && audience.length === 1 && audience[0] === settings.clientId);
    if (!exactAudience || tokens.token_type !== "bearer" || (idClaims?.azp !== undefined && idClaims.azp !== settings.clientId)
      || typeof subject !== "string" || subject.length === 0 || subject.length > 200
      || typeof tokens.access_token !== "string" || !tokens.access_token || tokens.access_token.length > 16_384
      || typeof tokens.expires_in !== "number" || !Number.isSafeInteger(tokens.expires_in) || tokens.expires_in < 1) throw new Error();
    // Core uses signed JWT access tokens. Check subject consistency now; Core
    // remains responsible for signature, issuer, audience, lifetime and roles.
    const pieces = tokens.access_token.split(".");
    if (pieces.length !== 3 || pieces.some((piece) => !/^[A-Za-z0-9_-]+$/.test(piece))) throw new Error();
    const accessClaims = JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(Buffer.from(pieces[1], "base64url")));
    const now = Math.floor(Date.now() / 1000);
    if (accessClaims?.sub !== subject || accessClaims.iss !== settings.issuer || Object.hasOwn(accessClaims, "cnf")
      || !Number.isSafeInteger(accessClaims.exp) || accessClaims.exp <= now
      || !idClaims || !Number.isSafeInteger(idClaims.exp) || idClaims.exp <= now) throw new Error();
    const expiresIn = Math.min(tokens.expires_in, 3600, accessClaims.exp - now, idClaims.exp - now);
    // Carry the absolute verified deadline through later API/Redis calls.
    // Reusing expiresIn relative to callback completion would extend the grant.
    return Object.freeze({ accessToken: tokens.access_token, expiresIn, expiresAt: now + expiresIn, subject });
  } catch (error) {
    reportBrowserProof(settings, "signed-exchange", error);
    // OIDC exceptions may contain provider bodies, parameters or private JWTs.
    throw new Error("Browser sign-in could not be verified.");
  }
}
