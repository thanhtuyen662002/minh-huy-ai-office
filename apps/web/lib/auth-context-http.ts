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

const hasText = (value: unknown): value is string => typeof value === "string" && value.trim().length > 0;
const hasCanonicalText = (value: unknown): value is string => hasText(value) && value.trim() === value;

function ownDataValue(value: object, key: PropertyKey): unknown {
  const descriptor = Object.getOwnPropertyDescriptor(value, key);
  return descriptor && "value" in descriptor ? descriptor.value : undefined;
}

function snapshotCanonicalRoles(value: unknown): string[] | null {
  if (!Array.isArray(value)) return null;
  const length = ownDataValue(value, "length");
  if (!Number.isSafeInteger(length) || (length as number) < 0) return null;

  const roles: string[] = [];
  const seen = new Set<string>();
  for (let index = 0; index < (length as number); index += 1) {
    const role = ownDataValue(value, String(index));
    if (!hasCanonicalText(role) || seen.has(role)) return null;
    seen.add(role);
    roles.push(role);
  }
  return roles;
}

function parseContext(value: unknown, selectedCompanyId: string): AuthoritativeAuthContext | null {
  try {
    if (value === null || typeof value !== "object") return null;
    const tenantId = ownDataValue(value, "tenantId");
    const companyId = ownDataValue(value, "companyId");
    const userId = ownDataValue(value, "userId");
    const roles = snapshotCanonicalRoles(ownDataValue(value, "roles"));
    if (!hasCanonicalText(tenantId) || !hasCanonicalText(companyId) || companyId !== selectedCompanyId || !hasCanonicalText(userId) || !roles) return null;
    return { tenantId, companyId, userId, roles };
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

  // Treat the response object as an untrusted runtime boundary too. A custom fetcher,
  // browser extension, or compromised transport shim must not be able to crash the shell
  // through throwing status/ok/json accessors.
  try {
    if (response.status === 401) return { ok: false, reason: "unauthenticated" };
    if (response.status === 403) return { ok: false, reason: "forbidden" };
    if (!response.ok) return { ok: false, reason: "invalid-response" };

    const payload: unknown = await response.json();
    const context = parseContext(payload, selectedCompanyId);
    return context ? { ok: true, context } : { ok: false, reason: "invalid-response" };
  } catch {
    return { ok: false, reason: "invalid-response" };
  }
}
