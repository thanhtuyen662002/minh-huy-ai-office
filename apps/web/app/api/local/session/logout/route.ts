import { NextResponse } from "next/server";
import { cookies } from "next/headers";
import { getBrowserAuthRuntime } from "../../../../../lib/browser-auth-runtime";
import { browserAuthError, privateBrowserResponse, setPrivateBrowserCookie } from "../../../../../lib/browser-auth-http";
import { oidcStartIsSameOrigin, oidcTransactionCookieName } from "../../../../../lib/browser-oidc";
import { BROWSER_BINDING_SECONDS, browserBindingCookieName, browserSessionCookieName } from "../../../../../lib/browser-session-store";
import {
  LOCAL_ACCESS_TOKEN_COOKIE,
  isLocalAiUiEnabled,
  isBrowserAiUiEnabled,
  localCookieSecure,
  localUiDisabledResponse,
} from "../../../../../lib/local-ai-bff";
import { hasSameOrigin } from "../../../../../lib/request-origin";

export async function POST(request: Request) {
  if (isBrowserAiUiEnabled()) {
    try {
      const runtime = getBrowserAuthRuntime();
      if (!runtime) return browserAuthError(503);
      if (!oidcStartIsSameOrigin(request, runtime.settings)) return browserAuthError(403);
      const store = await cookies(), name = browserBindingCookieName(runtime.settings);
      const binding = store.get(name)?.value;
      if (!binding) return browserAuthError(409);
      // Successful logout must invalidate shared active AND pending authority.
      // A stale/outage result cannot clear cookies or overwrite a newer binding.
      const nextBinding = await runtime.sessions.revoke(binding);
      if (!nextBinding) return browserAuthError(409);
      const response = privateBrowserResponse(NextResponse.json({ ok: true }));
      setPrivateBrowserCookie(response, runtime.settings, name, nextBinding, BROWSER_BINDING_SECONDS);
      for (const cookie of [browserSessionCookieName(runtime.settings), oidcTransactionCookieName(runtime.settings), LOCAL_ACCESS_TOKEN_COOKIE]) {
        setPrivateBrowserCookie(response, runtime.settings, cookie, "", 0);
      }
      return response;
    } catch { return browserAuthError(503); }
  }
  if (!isLocalAiUiEnabled()) return localUiDisabledResponse();
  if (!hasSameOrigin(request)) return Response.json({ error: "Same-origin request is required." }, {
    status: 403, headers: { "Cache-Control": "no-store" },
  });
  const response = NextResponse.json({ ok: true });
  response.cookies.set({
    name: LOCAL_ACCESS_TOKEN_COOKIE,
    value: "",
    httpOnly: true,
    sameSite: "lax",
    secure: localCookieSecure(),
    path: "/",
    maxAge: 0,
  });
  response.headers.set("Cache-Control", "no-store");
  return response;
}
