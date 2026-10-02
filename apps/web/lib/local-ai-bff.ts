import { cookies } from "next/headers";

export const LOCAL_ACCESS_TOKEN_COOKIE = "aioffice_local_access_token";
export const COMPANY_SELECTOR_HEADER = "X-AIOffice-Company-Id";

const DEFAULT_CORE_API_URL = "http://127.0.0.1:8080";
const DEFAULT_TOKEN_URL =
  "http://127.0.0.1:18080/realms/aioffice-local/protocol/openid-connect/token";
const DEFAULT_CLIENT_ID = "aioffice-local-cli";

export function isLocalAiUiEnabled() {
  return process.env.AIOFFICE_LOCAL_UI_ENABLED === "true";
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

export function isCanonicalCompanyId(value: unknown): value is string {
  return (
    typeof value === "string"
    && value.trim() === value
    && /^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[1-5][0-9a-fA-F]{3}-[89abAB][0-9a-fA-F]{3}-[0-9a-fA-F]{12}$/.test(value)
  );
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
  if (!isLocalAiUiEnabled() || !isCanonicalCompanyId(companyId)) return null;
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
