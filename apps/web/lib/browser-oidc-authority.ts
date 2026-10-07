import { COMPANY_SELECTOR_HEADER, isCanonicalCompanyId } from "./local-ai-bff";
import { parseAuthContext, type LocalAuthContext } from "./local-ai-workspace";
import type { BrowserOidcSettings, OidcTransaction } from "./browser-oidc";
import type { VerifiedBrowserTokens } from "./browser-oidc-client";
import type { PrivateBrowserSession } from "./browser-session-store";

const refused = () => new Error("Browser company access could not be verified.");

export function readBrowserCoreApiOrigin(settings: BrowserOidcSettings,
  environment: (name: string) => string | undefined = (name) => process.env[name]): string {
  const value = environment("AIOFFICE_BROWSER_CORE_API_ORIGIN");
  try {
    if (!value || value.length > 2048 || /[\s\\?#]/.test(value)) throw refused();
    const url = new URL(value);
    if (url.origin !== value || url.username || url.password) throw refused();
    if (url.protocol !== "https:") {
      const loopback = ["localhost", "127.0.0.1", "[::1]"].includes(url.hostname);
      if (!settings.localHttp || url.protocol !== "http:"
        || (!loopback && !(url.hostname === "core-api" && url.port === "8080"))) throw refused();
    }
    return value;
  } catch { throw refused(); }
}

// Called only after verified code exchange. Core validates the signed access
// token and resolves current provider/subject/company/membership authority.
// ID-token claims and caller-supplied tenant/roles never authorize the session.
export async function authorizeBrowserSession(settings: BrowserOidcSettings, transaction: OidcTransaction,
  tokens: VerifiedBrowserTokens, fetcher: typeof fetch = fetch,
  environment?: (name: string) => string | undefined): Promise<Readonly<{ context: LocalAuthContext; session: PrivateBrowserSession }>> {
  try {
    const now = Math.floor(Date.now() / 1000);
    if (!isCanonicalCompanyId(transaction.companyId) || transaction.expiresAt <= now
      || typeof tokens.accessToken !== "string" || !tokens.accessToken || tokens.accessToken.length > 16_384 || /[\r\n\0]/.test(tokens.accessToken)
      || typeof tokens.subject !== "string" || !tokens.subject || tokens.subject.length > 200
      || !Number.isSafeInteger(tokens.expiresAt) || tokens.expiresAt <= now || tokens.expiresAt > now + 3600) throw refused();
    const response = await fetcher(readBrowserCoreApiOrigin(settings, environment) + "/api/auth/context", {
      method: "GET", headers: { Authorization: `Bearer ${tokens.accessToken}`,
        [COMPANY_SELECTOR_HEADER]: transaction.companyId, Accept: "application/json" },
      cache: "no-store", redirect: "error", signal: AbortSignal.timeout(10_000),
    });
    if (!response.ok || response.redirected || response.headers.get("content-type")?.split(";")[0].trim().toLowerCase() !== "application/json") throw refused();
    const reader = response.body?.getReader();
    if (!reader) throw refused();
    const chunks: Uint8Array[] = [];
    let length = 0;
    try {
      while (true) {
        const chunk = await reader.read();
        if (chunk.done) break;
        length += chunk.value.byteLength;
        if (length > 65_536) {
          try { await reader.cancel(); } catch { /* Cleanup cannot bypass refusal. */ }
          throw refused();
        }
        chunks.push(chunk.value);
      }
    } finally { reader.releaseLock(); }
    const bytes = new Uint8Array(length);
    let offset = 0;
    for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.byteLength; }
    const value = JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(bytes));
    if (!value || typeof value !== "object" || Array.isArray(value)
      || Object.keys(value).sort().join(",") !== "companyId,roles,tenantId,userId") throw refused();
    const context = parseAuthContext(value);
    const after = Math.floor(Date.now() / 1000);
    if (!context || !isCanonicalCompanyId(context.tenantId) || !isCanonicalCompanyId(context.userId)
      || !isCanonicalCompanyId(context.companyId) || context.companyId.toLowerCase() !== transaction.companyId.toLowerCase()
      || context.roles.length > 32 || context.roles.some((role) => role.length > 100 || /[\r\n\0]/.test(role))
      || tokens.expiresAt <= after || transaction.expiresAt <= after) throw refused();
    // A GUID's case is a selector spelling, unlike opaque provider/subject IDs.
    // Store the selected spelling only after Core proves the same GUID scope.
    const session = Object.freeze({ accessToken: tokens.accessToken, subject: tokens.subject,
      companyId: transaction.companyId, expiresAt: tokens.expiresAt });
    return Object.freeze({ context, session });
  } catch { throw refused(); }
}
