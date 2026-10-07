import { cookies } from "next/headers";
import { COMPANY_SELECTOR_HEADER, isCanonicalCompanyId } from "./company-scope";
import { getBrowserAuthRuntime } from "./browser-auth-runtime";
import { oidcStartIsSameOrigin } from "./browser-oidc";
import { browserBindingCookieName, browserSessionCookieName } from "./browser-session-store";
import { hasSameOrigin } from "./request-origin";

export { COMPANY_SELECTOR_HEADER, isCanonicalCompanyId } from "./company-scope";

export const LOCAL_ACCESS_TOKEN_COOKIE = "aioffice_local_access_token";

const DEFAULT_CORE_API_URL = "http://127.0.0.1:8080";
const DEFAULT_TOKEN_URL =
  "http://127.0.0.1:18080/realms/aioffice-local/protocol/openid-connect/token";
const DEFAULT_CLIENT_ID = "aioffice-local-cli";

export function isLocalAiUiEnabled() {
  return process.env.AIOFFICE_LOCAL_UI_ENABLED === "true";
}

export function isBrowserAiUiEnabled() {
  return process.env.AIOFFICE_BROWSER_OIDC_ENABLED === "true";
}

export function isOfficeAiUiEnabled() {
  return isBrowserAiUiEnabled() || isLocalAiUiEnabled();
}

export function officeMutationIsSameOrigin(request: Request) {
  try {
    if (!isBrowserAiUiEnabled()) return hasSameOrigin(request);
    const runtime = getBrowserAuthRuntime();
    return !!runtime && oidcStartIsSameOrigin(request, runtime.settings);
  } catch { return false; }
}

export function localCoreApiUrl() {
  return (process.env.AIOFFICE_LOCAL_CORE_API_URL ?? DEFAULT_CORE_API_URL).replace(/\/$/, "");
}

export function localTokenUrl() {
  return process.env.AIOFFICE_LOCAL_OIDC_TOKEN_URL ?? DEFAULT_TOKEN_URL;
}

export function localClientId() {
  return process.env.AIOFFICE_LOCAL_OIDC_CLIENT_ID ?? DEFAULT_CLIENT_ID;
}

export function localCookieSecure() {
  return process.env.AIOFFICE_LOCAL_UI_COOKIE_SECURE === "true";
}

export async function readLocalAccessToken() {
  const store = await cookies();
  const token = store.get(LOCAL_ACCESS_TOKEN_COOKIE)?.value;
  return token && token.length <= 16_384 ? token : null;
}

export async function fetchCoreApi(
  path: string,
  companyId: string,
  init: RequestInit = {},
): Promise<Response | null> {
  if (!isOfficeAiUiEnabled() || !isCanonicalCompanyId(companyId)) return null;
  if (isBrowserAiUiEnabled()) {
    try {
      const runtime = getBrowserAuthRuntime();
      if (!runtime) return null;
      const store = await cookies();
      const binding = store.get(browserBindingCookieName(runtime.settings))?.value;
      const sid = store.get(browserSessionCookieName(runtime.settings))?.value;
      if (!binding || !sid) return null;
      const session = await runtime.sessions.read(binding, sid);
      if (!session || session.companyId.toLowerCase() !== companyId.toLowerCase()) return null;
      const headers = new Headers(init.headers);
      headers.set("Authorization", `Bearer ${session.accessToken}`);
      headers.set(COMPANY_SELECTOR_HEADER, companyId);
      headers.set("Accept", "application/json");
      const deadline = AbortSignal.timeout(10_000);
      const response = await fetch(`${runtime.coreApiOrigin}${path}`, {
        ...init, headers, cache: "no-store", redirect: "error",
        signal: init.signal ? AbortSignal.any([init.signal, deadline]) : deadline,
      });
      // Discard a reply whose browser authority expired or was revoked while
      // Core was running. Core independently resolves fresh membership/roles.
      if (!await runtime.sessions.read(binding, sid)) return null;
      return response;
    } catch { return null; }
  }
  const token = await readLocalAccessToken();
  if (!token) return null;

  const headers = new Headers(init.headers);
  headers.set("Authorization", `Bearer ${token}`);
  headers.set(COMPANY_SELECTOR_HEADER, companyId);
  headers.set("Accept", "application/json");

  return fetch(`${localCoreApiUrl()}${path}`, {
    ...init,
    headers,
    cache: "no-store",
  });
}

export async function copyCoreResponse(response: Response) {
  const text = await response.text();
  const headers = new Headers({
    "Cache-Control": "no-store",
    "Content-Type": response.headers.get("content-type") ?? "application/json; charset=utf-8",
  });
  return new Response(text, { status: response.status, headers });
}

export function localUiDisabledResponse() {
  return Response.json(
    { error: "Local AI Office UI is disabled." },
    { status: 503, headers: { "Cache-Control": "no-store" } },
  );
}

export function unauthenticatedResponse() {
  return Response.json(
    { error: "Local AI Office session is not authenticated." },
    { status: 401, headers: { "Cache-Control": "no-store" } },
  );
}
