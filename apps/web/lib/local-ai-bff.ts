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
export const BROWSER_CORE_RESPONSE_BYTES = 8 * 1024 * 1024;

async function readBrowserCoreBody(response: Response, signal: AbortSignal): Promise<Uint8Array<ArrayBuffer>> {
  const reader = response.body?.getReader();
  if (!reader) return new Uint8Array(0);
  let onAbort: (() => void) | undefined;
  async function consume() {
    const chunks: Uint8Array[] = [];
    let length = 0;
    while (true) {
      const chunk = await reader!.read();
      if (chunk.done) break;
      length += chunk.value.byteLength;
      if (length > BROWSER_CORE_RESPONSE_BYTES) {
        void reader!.cancel().catch(() => {});
        throw new Error("Core response could not be verified.");
      }
      chunks.push(chunk.value);
    }
    const bytes = new Uint8Array(length);
    let offset = 0;
    for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.byteLength; }
    return bytes;
  }
  try {
    if (signal.aborted) throw new Error("Core response could not be verified.");
    return await Promise.race([consume(), new Promise<never>((_resolve, reject) => {
      onAbort = () => {
        void reader.cancel().catch(() => {});
        reject(new Error("Core response could not be verified."));
      };
      signal.addEventListener("abort", onAbort, { once: true });
    })]);
  } finally {
    if (onAbort) signal.removeEventListener("abort", onAbort);
    reader.releaseLock();
  }
}

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

/** Recheck the incoming issued SID after asynchronous private receipt validation. */
export async function officeSessionIsCurrent(companyId: string): Promise<boolean> {
  if (!isOfficeAiUiEnabled() || !isCanonicalCompanyId(companyId)) return false;
  try {
    if (!isBrowserAiUiEnabled()) return !!await readLocalAccessToken();
    const runtime = getBrowserAuthRuntime(); if (!runtime) return false;
    const store = await cookies();
    const binding = store.get(browserBindingCookieName(runtime.settings))?.value;
    const sid = store.get(browserSessionCookieName(runtime.settings))?.value;
    if (!binding || !sid) return false;
    const current = await runtime.sessions.read(binding, sid);
    return !!current && current.companyId.toLowerCase() === companyId.toLowerCase();
  } catch { return false; }
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
      const signal = init.signal ? AbortSignal.any([init.signal, deadline]) : deadline;
      let response: Response;
      let bytes: Uint8Array<ArrayBuffer>;
      try {
        response = await fetch(`${runtime.coreApiOrigin}${path}`, {
          ...init, headers, cache: "no-store", redirect: "error",
          signal,
        });
        // fetch resolves at headers. Consume the bounded body before the final
        // shared authority fence, so a slow stream cannot carry post-logout data.
        bytes = await readBrowserCoreBody(response, signal);
      } catch {
        // Core may have committed a mutation before its reply was lost. A
        // still-issued session permits a retry with the same operation ID;
        // session loss or coordinator failure remains an authentication denial.
        const current = await runtime.sessions.read(binding, sid);
        if (!current || current.companyId.toLowerCase() !== companyId.toLowerCase()) return null;
        return new Response(null, {
          status: 503,
          headers: { "Content-Type": "application/json; charset=utf-8", "Cache-Control": "no-store" },
        });
      }
      // Discard a reply whose browser authority expired or was revoked while
      // Core was running. Core independently resolves fresh membership/roles.
      const current = await runtime.sessions.read(binding, sid);
      if (!current || current.companyId.toLowerCase() !== companyId.toLowerCase()) return null;
      return new Response([204, 205, 304].includes(response.status) ? null : bytes, {
        status: response.status,
        headers: { "Content-Type": response.headers.get("content-type") ?? "application/json; charset=utf-8", "Cache-Control": "no-store" },
      });
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
  return new Response([204, 205, 304].includes(response.status) ? null : text, { status: response.status, headers });
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
