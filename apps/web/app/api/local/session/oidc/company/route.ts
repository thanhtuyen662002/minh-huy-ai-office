import { cookies } from "next/headers";
import { NextResponse } from "next/server";
import { browserAuthError, privateBrowserResponse, readBrowserCompany, setPrivateBrowserCookie } from "../../../../../../lib/browser-auth-http";
import { getBrowserAuthRuntime } from "../../../../../../lib/browser-auth-runtime";
import { authorizeDiscoveredCompany, discoverBrowserCompanies } from "../../../../../../lib/browser-company-preselection";
import { oidcStartIsSameOrigin, oidcTransactionCookieName, openOidcTransaction } from "../../../../../../lib/browser-oidc";
import { browserBindingCookieName, browserSessionCookieName } from "../../../../../../lib/browser-session-store";

async function selectionRequest(request: Request, mutation: boolean) {
  const runtime = getBrowserAuthRuntime();
  if (!runtime) return null;
  if (mutation ? !oidcStartIsSameOrigin(request, runtime.settings)
    : request.headers.get("host") !== new URL(runtime.settings.publicOrigin).host) return null;
  if (new URL(request.url).search) return null;
  const jar = await cookies(), binding = jar.get(browserBindingCookieName(runtime.settings))?.value;
  const transaction = openOidcTransaction(jar.get(oidcTransactionCookieName(runtime.settings))?.value, runtime.settings);
  return binding && transaction?.version === 2 ? { runtime, binding, transaction } : null;
}

export async function GET(request: Request) {
  try {
    const value = await selectionRequest(request, false);
    if (!value) return browserAuthError(401);
    const selection = await value.runtime.sessions.readSelection(value.binding, value.transaction);
    if (!selection) return browserAuthError(401);
    // Never serialize tokens, subject, roles, tenant or an existing SID here.
    return privateBrowserResponse(Response.json({ items: selection.choices }));
  } catch { return browserAuthError(401); }
}

export async function POST(request: Request) {
  try {
    const value = await selectionRequest(request, true);
    if (!value) return browserAuthError(401);
    const companyId = await readBrowserCompany(request);
    if (!companyId) return browserAuthError(400);
    const { runtime, binding, transaction } = value;
    const claimed = await runtime.sessions.claimSelection(binding, transaction);
    if (!claimed || !claimed.selection.choices.some((choice) => choice.companyId.toLowerCase() === companyId.toLowerCase())) return browserAuthError(401);
    const tokens = { accessToken: claimed.selection.accessToken, subject: claimed.selection.subject,
      expiresAt: claimed.selection.expiresAt, expiresIn: claimed.selection.expiresAt - Math.floor(Date.now() / 1000) };
    const environment = (name: string) => name === "AIOFFICE_BROWSER_CORE_API_ORIGIN" ? runtime.coreApiOrigin : undefined;
    const choices = await discoverBrowserCompanies(runtime.settings, transaction, tokens, fetch, environment);
    if (!choices.some((choice) => choice.companyId.toLowerCase() === companyId.toLowerCase())) return browserAuthError(403);
    const authorized = await authorizeDiscoveredCompany(runtime.settings, transaction, tokens, companyId, fetch, environment);
    const sid = await runtime.sessions.complete(binding, transaction, claimed.claim, authorized.session);
    const maxAge = tokens.expiresAt - Math.floor(Date.now() / 1000);
    if (!sid || maxAge <= 0) return browserAuthError(401);
    const response = privateBrowserResponse(NextResponse.json({ redirectTo: "/" }));
    setPrivateBrowserCookie(response, runtime.settings, browserSessionCookieName(runtime.settings), sid, maxAge);
    setPrivateBrowserCookie(response, runtime.settings, oidcTransactionCookieName(runtime.settings), "", 0);
    return response;
  } catch { return browserAuthError(401); }
}

export async function DELETE(request: Request) {
  try {
    const value = await selectionRequest(request, true);
    if (!value || !await value.runtime.sessions.cancelSelection(value.binding, value.transaction)) return browserAuthError(401);
    // No SID or binding cookie mutation: cancellation cannot replace/logout an
    // old working session or clobber another generation's binding.
    return privateBrowserResponse(Response.json({ redirectTo: "/" }));
  } catch { return browserAuthError(401); }
}
