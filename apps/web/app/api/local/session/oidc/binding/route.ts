import { cookies } from "next/headers";
import { NextResponse } from "next/server";
import { getBrowserAuthRuntime } from "../../../../../../lib/browser-auth-runtime";
import { browserAuthError, privateBrowserResponse, setPrivateBrowserCookie } from "../../../../../../lib/browser-auth-http";
import { oidcStartIsSameOrigin } from "../../../../../../lib/browser-oidc";
import { BROWSER_BINDING_SECONDS, browserBindingCookieName } from "../../../../../../lib/browser-session-store";

export async function POST(request: Request) {
  try {
    const runtime = getBrowserAuthRuntime();
    if (!runtime) return browserAuthError(503);
    if (!oidcStartIsSameOrigin(request, runtime.settings)) return browserAuthError(403);
    const name = browserBindingCookieName(runtime.settings), cookieStore = await cookies();
    const existing = cookieStore.get(name)?.value;
    if (existing && await runtime.sessions.isCurrent(existing)) return privateBrowserResponse(NextResponse.json({ ok: true }));
    const binding = await runtime.sessions.register();
    const response = privateBrowserResponse(NextResponse.json({ ok: true }));
    setPrivateBrowserCookie(response, runtime.settings, name, binding, BROWSER_BINDING_SECONDS);
    return response;
  } catch { return browserAuthError(503); }
}
