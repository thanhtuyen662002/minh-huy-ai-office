import { cookies } from "next/headers";
import { NextResponse } from "next/server";
import { getBrowserAuthRuntime } from "../../../../../../lib/browser-auth-runtime";
import { browserAuthError, privateBrowserResponse, readBrowserCompany, setPrivateBrowserCookie } from "../../../../../../lib/browser-auth-http";
import { createOidcTransaction, OIDC_TRANSACTION_SECONDS, oidcStartIsSameOrigin,
  oidcTransactionCookieName, sealOidcTransaction } from "../../../../../../lib/browser-oidc";
import { browserAuthorizationUrl } from "../../../../../../lib/browser-oidc-client";
import { BROWSER_BINDING_SECONDS, browserBindingCookieName } from "../../../../../../lib/browser-session-store";

export async function POST(request: Request) {
  try {
    const runtime = getBrowserAuthRuntime();
    if (!runtime) return browserAuthError(503);
    if (!oidcStartIsSameOrigin(request, runtime.settings)) return browserAuthError(403);
    const companyId = await readBrowserCompany(request);
    if (!companyId) return browserAuthError(400);
    const cookieStore = await cookies(), bindingName = browserBindingCookieName(runtime.settings);
    const binding = cookieStore.get(bindingName)?.value;
    // Preparation must settle before starting; callbacks never initialize or
    // replace browser bindings, including after logout or out-of-order replies.
    if (!binding) return browserAuthError(409);
    const transaction = createOidcTransaction(companyId);
    const authorizationUrl = await browserAuthorizationUrl(runtime.settings, transaction);
    const sealed = sealOidcTransaction(transaction, runtime.settings);
    const nextBinding = await runtime.sessions.begin(binding, transaction);
    if (!nextBinding) return browserAuthError(409);
    const response = privateBrowserResponse(NextResponse.json({ authorizationUrl: authorizationUrl.href }));
    setPrivateBrowserCookie(response, runtime.settings, bindingName, nextBinding, BROWSER_BINDING_SECONDS);
    setPrivateBrowserCookie(response, runtime.settings, oidcTransactionCookieName(runtime.settings), sealed, OIDC_TRANSACTION_SECONDS);
    return response;
  } catch { return browserAuthError(503); }
}
