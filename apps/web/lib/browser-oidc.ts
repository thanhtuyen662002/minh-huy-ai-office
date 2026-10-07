import { createCipheriv, createDecipheriv, hkdfSync, randomBytes } from "node:crypto";
import { isCanonicalCompanyId } from "./company-scope";

export const OIDC_CALLBACK_PATH = "/api/local/session/oidc/callback";
export const OIDC_TRANSACTION_SECONDS = 300;
const invalidConfiguration = () => new Error("Browser identity configuration is invalid.");
const loopback = new Set(["localhost", "127.0.0.1", "[::1]"]);

export type BrowserOidcSettings = Readonly<{
  publicOrigin: string;
  issuer: string;
  authorizationEndpoint: string;
  tokenEndpoint: string;
  jwksUri: string;
  clientId: string;
  clientSecret: string | null;
  transactionKey: string;
  redirectUri: string;
  localHttp: boolean;
}>;

// Only operator configuration supplies endpoints. Do not derive these from a
// browser Host, forwarded header, return URL or authorization response.
export function readBrowserOidcSettings(
  environment: (name: string) => string | undefined = (name) => process.env[name],
): BrowserOidcSettings | null {
  if (environment("AIOFFICE_BROWSER_OIDC_ENABLED") !== "true") return null;
  const localHttp = environment("AIOFFICE_BROWSER_OIDC_LOCAL_HTTP") === "true";
  function endpoint(name: string, backchannel = false): URL {
    const value = environment(name);
    if (!value || value.length > 2048 || /[\s\\?#]/.test(value)) throw invalidConfiguration();
    let url: URL;
    try { url = new URL(value); } catch { throw invalidConfiguration(); }
    if (url.username || url.password || url.search || url.hash || url.href !== value) throw invalidConfiguration();
    if (url.protocol !== "https:") {
      const localAddress = loopback.has(url.hostname)
        || (backchannel && url.hostname === "identity" && url.port === "8080");
      if (!localHttp || url.protocol !== "http:" || !localAddress) throw invalidConfiguration();
    }
    return url;
  }
  const originValue = environment("AIOFFICE_BROWSER_OIDC_PUBLIC_ORIGIN");
  // URL serializes a bare origin with a trailing slash; the config contract is
  // the exact origin without a path, including for a trusted reverse proxy.
  if (!originValue || /[\s\\]/.test(originValue)) throw invalidConfiguration();
  let origin: URL;
  try { origin = new URL(originValue); } catch { throw invalidConfiguration(); }
  if (origin.origin !== originValue || origin.username || origin.password) throw invalidConfiguration();
  if (localHttp) {
    if (origin.protocol !== "http:" || !loopback.has(origin.hostname)) throw invalidConfiguration();
  } else if (origin.protocol !== "https:") throw invalidConfiguration();

  const issuer = endpoint("AIOFFICE_BROWSER_OIDC_ISSUER").href;
  const authorizationEndpoint = endpoint("AIOFFICE_BROWSER_OIDC_AUTHORIZATION_ENDPOINT").href;
  const tokenEndpoint = endpoint("AIOFFICE_BROWSER_OIDC_TOKEN_ENDPOINT", true).href;
  const jwksUri = endpoint("AIOFFICE_BROWSER_OIDC_JWKS_URI", true).href;
  const clientId = environment("AIOFFICE_BROWSER_OIDC_CLIENT_ID");
  const transactionKey = environment("AIOFFICE_BROWSER_OIDC_TRANSACTION_KEY");
  if (!clientId || !/^[A-Za-z0-9._-]{1,100}$/.test(clientId)) throw invalidConfiguration();
  if (!transactionKey || !/^[A-Za-z0-9_-]{43}$/.test(transactionKey)
    || Buffer.from(transactionKey, "base64url").length !== 32
    || Buffer.from(transactionKey, "base64url").toString("base64url") !== transactionKey) throw invalidConfiguration();
  const clientSecret = environment("AIOFFICE_BROWSER_OIDC_CLIENT_SECRET") || null;
  if (clientSecret && (clientSecret.length > 4096 || /[\r\n\0]/.test(clientSecret))) throw invalidConfiguration();
  return Object.freeze({ publicOrigin: originValue, issuer, authorizationEndpoint, tokenEndpoint,
    jwksUri, clientId, clientSecret, transactionKey, redirectUri: originValue + OIDC_CALLBACK_PATH, localHttp });
}

export function oidcStartIsSameOrigin(request: Request, settings: BrowserOidcSettings): boolean {
  if (request.headers.get("origin") !== settings.publicOrigin) return false;
  // TLS can terminate before Next.js sees the request. The configured browser
  // origin and exact raw Host are authoritative, never internal/forwarded URLs.
  const host = request.headers.get("host");
  return host !== null ? host === new URL(settings.publicOrigin).host
    : new URL(request.url).origin === settings.publicOrigin;
}

export type OidcTransaction = Readonly<{
  version: 1;
  companyId: string;
  issuedAt: number;
  expiresAt: number;
  state: string;
  nonce: string;
  verifier: string;
}>;

export function createOidcTransaction(companyId: string, now = Math.floor(Date.now() / 1000)): OidcTransaction {
  if (!isCanonicalCompanyId(companyId) || !Number.isSafeInteger(now) || now < 0
    || !Number.isSafeInteger(now + OIDC_TRANSACTION_SECONDS)) throw new Error("Invalid login transaction.");
  const random = () => randomBytes(32).toString("base64url");
  return Object.freeze({ version: 1, companyId, issuedAt: now, expiresAt: now + OIDC_TRANSACTION_SECONDS,
    state: random(), nonce: random(), verifier: random() });
}

function transactionContext(settings: BrowserOidcSettings) {
  return Buffer.from(JSON.stringify(["aioffice-browser-login-v1", settings.issuer, settings.clientId, settings.redirectUri]));
}

function encryptionKey(settings: BrowserOidcSettings) {
  return Buffer.from(hkdfSync("sha256", Buffer.from(settings.transactionKey, "base64url"),
    Buffer.alloc(0), transactionContext(settings), 32));
}

export function sealOidcTransaction(transaction: OidcTransaction, settings: BrowserOidcSettings): string {
  const iv = randomBytes(12);
  const cipher = createCipheriv("aes-256-gcm", encryptionKey(settings), iv);
  cipher.setAAD(transactionContext(settings));
  const ciphertext = Buffer.concat([cipher.update(JSON.stringify(transaction), "utf8"), cipher.final()]);
  return ["v1", iv.toString("base64url"), ciphertext.toString("base64url"), cipher.getAuthTag().toString("base64url")].join(".");
}

// Failure is deliberately opaque: neither crypto errors nor cookie contents
// may become a provider error, log message or frontend authority.
export function openOidcTransaction(
  cookie: string | undefined, settings: BrowserOidcSettings, now = Math.floor(Date.now() / 1000),
): OidcTransaction | null {
  if (!cookie || cookie.length > 2048 || !Number.isSafeInteger(now) || now < 0) return null;
  try {
    const parts = cookie.split(".");
    if (parts.length !== 4 || parts[0] !== "v1" || parts.slice(1).some((part) => !/^[A-Za-z0-9_-]+$/.test(part))) return null;
    const [iv, ciphertext, tag] = parts.slice(1).map((part) => Buffer.from(part, "base64url"));
    if (iv.length !== 12 || tag.length !== 16) return null;
    if ([iv, ciphertext, tag].some((part, index) => part.toString("base64url") !== parts[index + 1])) return null;
    const decipher = createDecipheriv("aes-256-gcm", encryptionKey(settings), iv);
    decipher.setAAD(transactionContext(settings));
    decipher.setAuthTag(tag);
    const value = JSON.parse(Buffer.concat([decipher.update(ciphertext), decipher.final()]).toString("utf8"));
    if (!value || typeof value !== "object" || Array.isArray(value)
      || Object.keys(value).sort().join(",") !== "companyId,expiresAt,issuedAt,nonce,state,verifier,version"
      || value.version !== 1 || !isCanonicalCompanyId(value.companyId)
      || !Number.isSafeInteger(value.issuedAt) || value.issuedAt < 0 || value.issuedAt > now + 10
      || !Number.isSafeInteger(value.expiresAt) || value.expiresAt !== value.issuedAt + OIDC_TRANSACTION_SECONDS || value.expiresAt <= now
      || [value.state, value.nonce, value.verifier].some((item) => typeof item !== "string" || !/^[A-Za-z0-9_-]{43}$/.test(item))) return null;
    return Object.freeze(value) as OidcTransaction;
  } catch { return null; }
}

export function oidcTransactionCookieName(settings: BrowserOidcSettings): string {
  return settings.localHttp ? "aioffice_oidc_transaction" : "__Host-aioffice_oidc_transaction";
}
