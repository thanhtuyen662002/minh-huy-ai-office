import { COMPANY_SELECTOR_HEADER } from "./authenticated-session";

export type AuthoritativeAuthContext = {
  tenantId: string;
  companyId: string;
  userId: string;
  roles: readonly string[];
};

export type AuthContextHttpResult =
  | { ok: true; context: AuthoritativeAuthContext }
  | { ok: false; reason: "unauthenticated" | "forbidden" | "invalid-response" };

const hasOwn = (value: object, key: PropertyKey): boolean => Object.prototype.hasOwnProperty.call(value, key);
const hasText = (value: unknown): value is string => typeof value === "string" && value.trim().length > 0;
const hasCanonicalText = (value: unknown): value is string => hasText(value) && value.trim() === value;

function hasDenseCanonicalRoles(value: unknown): value is string[] {
  if (!Array.isArray(value)) return false;
  const seen = new Set<string>();
  for (let index = 0; index < value.length; index += 1) {
    if (!hasOwn(value, index) || !hasCanonicalText(value[index]) || seen.has(value[index])) return false;
    seen.add(value[index]);
  }
  return true;
}

function parseContext(value: unknown, selectedCompanyId: string): AuthoritativeAuthContext | null {
  try {
    if (value === null || typeof value !== "object") return null;
    if (!hasOwn(value, "tenantId") || !hasOwn(value, "companyId") || !hasOwn(value, "userId") || !hasOwn(value, "roles")) return null;
    const candidate = value as Record<string, unknown>;
    if (!hasCanonicalText(candidate.tenantId) || !hasCanonicalText(candidate.companyId) || candidate.companyId !== selectedCompanyId || !hasCanonicalText(candidate.userId)) return null;
    if (!hasDenseCanonicalRoles(candidate.roles)) return null;
    return {
      tenantId: candidate.tenantId,
      companyId: candidate.companyId,
      userId: candidate.userId,
      roles: [...candidate.roles],
    };
  } catch {
    return null;
  }
}

/**
 * Calls the merged trusted auth boundary. The selected company is only a selector;
 * tenant/user authority is accepted exclusively from the protected server response.
 * Presentation names deliberately remain outside this transport because /api/auth/context
 * does not currently expose authoritative company/user display metadata.
 */
export async function fetchAuthoritativeAuthContext(
  selectedCompanyId: string,
  fetcher: typeof fetch = fetch,
): Promise<AuthContextHttpResult> {
  // Keep browser-controlled selector semantics aligned with the session adapter: reject
  // ambiguous values instead of silently canonicalizing them before transport.
  if (!hasCanonicalText(selectedCompanyId)) return { ok: false, reason: "invalid-response" };

  let response: Response;
  try {
    response = await fetcher("/api/auth/context", {
      method: "GET",
      credentials: "same-origin",
      headers: { [COMPANY_SELECTOR_HEADER]: selectedCompanyId },
    });
  } catch {
    return { ok: false, reason: "invalid-response" };
  }

  if (response.status === 401) return { ok: false, reason: "unauthenticated" };
  if (response.status === 403) return { ok: false, reason: "forbidden" };
  if (!response.ok) return { ok: false, reason: "invalid-response" };

  let payload: unknown;
  try {
    payload = await response.json();
  } catch {
    return { ok: false, reason: "invalid-response" };
  }

  const context = parseContext(payload, selectedCompanyId);
  return context ? { ok: true, context } : { ok: false, reason: "invalid-response" };
}
