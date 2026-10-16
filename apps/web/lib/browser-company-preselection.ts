import { isCanonicalCompanyId } from "./company-scope";
import { parseCompanyChoices, type CompanyChoice } from "./company-choices";
import { authorizeBrowserSession, readBrowserCoreApiOrigin } from "./browser-oidc-authority";
import type { BrowserOidcSettings, OidcTransaction } from "./browser-oidc";
import type { VerifiedBrowserTokens } from "./browser-oidc-client";

const refused = () => new Error("Browser company access could not be verified.");

// Identity-only Core endpoint. No default-company header, browser tenant,
// roles or provider claims are used as directory authority.
export async function discoverBrowserCompanies(settings: BrowserOidcSettings, transaction: OidcTransaction,
  tokens: VerifiedBrowserTokens, fetcher: typeof fetch = fetch,
  environment?: (name: string) => string | undefined): Promise<readonly CompanyChoice[]> {
  let timer: ReturnType<typeof setTimeout> | undefined;
  const controller = new AbortController();
  try {
    const now = Math.floor(Date.now() / 1000);
    if (transaction.version !== 2 || transaction.companyId !== null || transaction.expiresAt <= now
      || !tokens.accessToken || tokens.accessToken.length > 16_384 || /[\r\n\0]/.test(tokens.accessToken)
      || !tokens.subject || tokens.subject.length > 200 || !Number.isSafeInteger(tokens.expiresAt)
      || tokens.expiresAt <= now || tokens.expiresAt > now + 3600) throw refused();
    const pending = async () => {
      const response = await fetcher(readBrowserCoreApiOrigin(settings, environment) + "/api/auth/companies", {
        headers: { Authorization: `Bearer ${tokens.accessToken}`, Accept: "application/json" },
        cache: "no-store", redirect: "error", signal: controller.signal,
      });
      if (!response.ok || response.redirected || response.headers.get("content-type")?.split(";")[0].trim().toLowerCase() !== "application/json") throw refused();
      const reader = response.body?.getReader();
      if (!reader) throw refused();
      const chunks: Uint8Array[] = []; let length = 0;
      try {
        while (true) {
          const chunk = await reader.read();
          if (chunk.done) break;
          length += chunk.value.byteLength;
          if (length > 131_072) { void reader.cancel().catch(() => {}); throw refused(); }
          chunks.push(chunk.value);
        }
      } finally { reader.releaseLock(); }
      const bytes = new Uint8Array(length); let offset = 0;
      for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.byteLength; }
      const choices = parseCompanyChoices(JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(bytes)));
      const after = Math.floor(Date.now() / 1000);
      if (!choices || transaction.expiresAt <= after || tokens.expiresAt <= after) throw refused();
      return choices;
    };
    return await Promise.race([pending(), new Promise<never>((_resolve, reject) => {
      timer = setTimeout(() => { controller.abort(); reject(refused()); }, 10_000);
    })]);
  } catch { throw refused(); }
  finally { clearTimeout(timer); }
}

export async function authorizeDiscoveredCompany(settings: BrowserOidcSettings, transaction: OidcTransaction,
  tokens: VerifiedBrowserTokens, companyId: string, fetcher: typeof fetch = fetch,
  environment?: (name: string) => string | undefined) {
  if (transaction.version !== 2 || transaction.companyId !== null || !isCanonicalCompanyId(companyId)) throw refused();
  // This scoped object is derived only after the authenticated directory choice;
  // it cannot be sealed, begun or used as a discovery/store transaction.
  return authorizeBrowserSession(settings, { ...transaction, companyId }, tokens, fetcher, environment);
}
