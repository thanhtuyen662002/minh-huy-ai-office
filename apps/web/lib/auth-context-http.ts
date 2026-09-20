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

function hasDenseTextRoles(value: unknown): value is string[] {
  if (!Array.isArray(value)) return false;
  for (let index = 0; index < value.length; index += 1) {
    if (!hasOwn(value, index) || !hasText(value[index])) return false;
  }
  return true;
}

function parseContext(value: unknown, selectedCompanyId: string): AuthoritativeAuthContext | null {
  if (value === null || typeof value !== "object") return null;
  if (!hasOwn(value, "tenantId") || !hasOwn(value, "companyId") || !hasOwn(value, "userId") || !hasOwn(value, "roles")) return null;
  const candidate = value as Record<string, unknown>;
  if (!hasText(candidate.tenantId) || !hasText(candidate.companyId) || candidate.companyId !== selectedCompanyId || !hasText(candidate.userId)) return null;
  if (!hasDenseTextRoles(candidate.roles)) return null;
  return {
    tenantId: candidate.tenantId,
    companyId: candidate.companyId,
    userId: candidate.userId,
    roles: [...candidate.roles],
  };
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
  const normalizedCompanyId = selectedCompanyId.trim();
  if (!normalizedCompanyId) return { ok: false, reason: "invalid-response" };

  let response: Response;
  try {
    response = await fetcher("/api/auth/context", {
      method: "GET",
      credentials: "same-origin",
      headers: { [COMPANY_SELECTOR_HEADER]: normalizedCompanyId },
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

  const context = parseContext(payload, normalizedCompanyId);
  return context ? { ok: true, context } : { ok: false, reason: "invalid-response" };
}
