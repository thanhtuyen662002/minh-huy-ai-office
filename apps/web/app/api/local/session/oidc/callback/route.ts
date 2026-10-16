import { cookies } from "next/headers";
import { NextResponse } from "next/server";
import { getBrowserAuthRuntime } from "../../../../../../lib/browser-auth-runtime";
import { browserAuthError, privateBrowserResponse, setPrivateBrowserCookie } from "../../../../../../lib/browser-auth-http";
import { OIDC_CALLBACK_PATH, oidcTransactionCookieName, openOidcTransaction } from "../../../../../../lib/browser-oidc";
import { exchangeBrowserAuthorizationCode } from "../../../../../../lib/browser-oidc-client";
import { authorizeBrowserSession } from "../../../../../../lib/browser-oidc-authority";
import { authorizeDiscoveredCompany, discoverBrowserCompanies } from "../../../../../../lib/browser-company-preselection";
import { browserBindingCookieName, browserSessionCookieName } from "../../../../../../lib/browser-session-store";
import { reportBrowserProof, type BrowserProofStep } from "../../../../../../lib/browser-proof-diagnostic";
import type { BrowserOidcSettings } from "../../../../../../lib/browser-oidc";

export async function GET(request: Request) {
  let settings: BrowserOidcSettings | undefined, step: BrowserProofStep = "configuration";
  const refused = (status: number, error?: unknown) => { reportBrowserProof(settings, step, error); return browserAuthError(status); };
  try {
    const runtime = getBrowserAuthRuntime();
    if (!runtime) return refused(503);
    const incoming = new URL(request.url); settings = runtime.settings; step = "origin";
    if (request.headers.get("host") !== new URL(settings.publicOrigin).host
      || incoming.pathname !== OIDC_CALLBACK_PATH || incoming.hash || incoming.href.length > 8192) return refused(403);
    step = "transaction";
    const cookieStore = await cookies();
    const binding = cookieStore.get(browserBindingCookieName(settings))?.value;
    const transaction = openOidcTransaction(cookieStore.get(oidcTransactionCookieName(settings))?.value ?? "", settings);
    if (!binding || !transaction) return refused(401);
    step = "response";
    const callback = new URL(settings.redirectUri); callback.search = incoming.search;
    if (callback.searchParams.getAll("state").length !== 1 || callback.searchParams.get("state") !== transaction.state
      || callback.searchParams.getAll("code").length !== 1 || !callback.searchParams.get("code")
      || callback.searchParams.has("error") || callback.searchParams.getAll("iss").length > 1
      || (callback.searchParams.has("iss") && callback.searchParams.get("iss") !== settings.issuer)) return refused(401);
    step = "claim";
    const claim = await runtime.sessions.claim(binding, transaction);
    if (!claim) return refused(401);
    step = "exchange";
    const tokens = await exchangeBrowserAuthorizationCode(settings, callback, transaction);
    step = "authority";
    const environment = (name: string) => name === "AIOFFICE_BROWSER_CORE_API_ORIGIN" ? runtime.coreApiOrigin : undefined;
    let authorized;
    if (transaction.version === 2) {
      const choices = await discoverBrowserCompanies(settings, transaction, tokens, fetch, environment);
      if (choices.length === 0) return privateBrowserResponse(Response.json({
        error: "Tài khoản chưa có quyền vào công ty nào. Vui lòng liên hệ quản trị viên để được cấp quyền.",
      }, { status: 403 }));
      if (choices.length > 1) {
        if (!await runtime.sessions.stageSelection(binding, transaction, claim, {
          accessToken: tokens.accessToken, subject: tokens.subject, expiresAt: tokens.expiresAt, choices,
        })) return refused(401);
        return privateBrowserResponse(NextResponse.redirect(new URL("/sign-in/company", settings.publicOrigin), 303));
      }
      authorized = await authorizeDiscoveredCompany(settings, transaction, tokens, choices[0].companyId, fetch, environment);
    } else authorized = await authorizeBrowserSession(settings, transaction, tokens, fetch, environment);
    step = "publish";
    const sid = await runtime.sessions.complete(binding, transaction, claim, authorized.session);
    if (!sid) return refused(401);
    step = "lifetime";
    const maxAge = tokens.expiresAt - Math.floor(Date.now() / 1000);
    if (maxAge <= 0) return refused(401);
    const response = privateBrowserResponse(NextResponse.redirect(new URL(settings.publicOrigin), 303));
    setPrivateBrowserCookie(response, settings, browserSessionCookieName(settings), sid, maxAge);
    setPrivateBrowserCookie(response, settings, oidcTransactionCookieName(settings), "", 0);
    // The current binding is never written by callback, so delayed/reordered
    // completion cannot overwrite a later logout/new-login generation cookie.
    return response;
  } catch (error) { return refused(401, error); }
}
