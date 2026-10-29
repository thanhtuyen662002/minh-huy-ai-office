import { LocalAiWorkspace } from "../components/local-ai-workspace";
import { readBrowserOidcSettings } from "../lib/browser-oidc";
import type { PublicBrowserLogin } from "../lib/browser-login-navigation";
import { readSelectedBrowserCompany } from "../lib/browser-selected-company";
import type { WorkspaceSection } from "../lib/workspace-sections";

const DEFAULT_LOCAL_COMPANY_ID = "22222222-2222-2222-2222-222222222222";

export async function renderWorkspace(section: WorkspaceSection) {
  const defaultCompanyId = process.env.AIOFFICE_LOCAL_COMPANY_ID ?? DEFAULT_LOCAL_COMPANY_ID;
  let companyId = defaultCompanyId;
  let companyName = process.env.AIOFFICE_LOCAL_COMPANY_NAME ?? "Minh Huy";
  const loginMode = process.env.AIOFFICE_BROWSER_OIDC_ENABLED === "true" ? "browser" : "local";
  let browserLogin: PublicBrowserLogin | undefined;
  if (loginMode === "browser") {
    companyId = await readSelectedBrowserCompany() ?? defaultCompanyId;
    if (companyId.toLowerCase() !== defaultCompanyId.toLowerCase()) companyName = "Công ty đã chọn";
    try {
      const settings = readBrowserOidcSettings();
      if (settings) browserLogin = { authorizationEndpoint: settings.authorizationEndpoint,
        clientId: settings.clientId, redirectUri: settings.redirectUri, localHttp: settings.localHttp };
    } catch { /* The sign-in surface reports unavailable configuration generically. */ }
  }
  // Browser mode goes straight to the identity provider so there is a single sign-in page.
  return <LocalAiWorkspace companyId={companyId} companyName={companyName} loginMode={loginMode} browserLogin={browserLogin}
    initialSection={section} autoStartLogin={loginMode === "browser"} />;
}
