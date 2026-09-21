import type { CompanyMembershipView } from "./company-context";

export const COMPANY_SELECTOR_HEADER = "X-AIOffice-Company-Id";

export type SessionFailure = "unauthenticated" | "forbidden" | "inactive-membership" | "invalid-response";
export type AuthenticatedSessionState =
  | { status: "loading"; selectedCompanyId: string | null }
  | { status: "unauthenticated" }
  | { status: "forbidden"; reason: "forbidden" | "inactive-membership" | "invalid-response" }
  | { status: "ready"; membership: CompanyMembershipView };
export type SessionBootstrapResult =
  | { ok: true; membership: CompanyMembershipView }
  | { ok: false; reason: SessionFailure };
export type SessionBootstrapTransport = (request: {
  /** Untrusted selector only. Server membership validation remains authoritative. */
  selectedCompanyId: string;
  headers: Readonly<Record<string, string>>;
}) => Promise<SessionBootstrapResult>;

const hasText = (value: unknown): value is string => typeof value === "string" && value.trim().length > 0;
const hasCanonicalText = (value: unknown): value is string => hasText(value) && value.trim() === value;
const hasOwn = (value: object, key: PropertyKey): boolean => Object.prototype.hasOwnProperty.call(value, key);

function isValidMembership(membership: CompanyMembershipView, selectedCompanyId: string): boolean {
  return membership !== null && typeof membership === "object" && hasCanonicalText(membership.tenantId) && hasCanonicalText(membership.companyId) && membership.companyId === selectedCompanyId && hasCanonicalText(membership.companyName) && hasCanonicalText(membership.userId) && hasCanonicalText(membership.userName) && Array.isArray(membership.roles) && membership.roles.every(hasCanonicalText);
}

function isSessionBootstrapResult(value: unknown): value is SessionBootstrapResult {
  if (value === null || typeof value !== "object" || !hasOwn(value, "ok")) return false;
  if (value.ok === true) return hasOwn(value, "membership") && !hasOwn(value, "reason");
  return value.ok === false && !hasOwn(value, "membership") && hasOwn(value, "reason") && (value.reason === "unauthenticated" || value.reason === "forbidden" || value.reason === "inactive-membership" || value.reason === "invalid-response");
}

/** Browser state may choose a company, but never supplies TenantId/UserId authority. */
export async function bootstrapAuthenticatedSession(selectedCompanyId: string | null | undefined, transport: SessionBootstrapTransport): Promise<AuthenticatedSessionState> {
  if (!selectedCompanyId || !hasText(selectedCompanyId)) return { status: "forbidden", reason: "invalid-response" };
  const normalizedCompanyId = selectedCompanyId.trim();
  let result: unknown;
  try {
    result = await transport({ selectedCompanyId: normalizedCompanyId, headers: { [COMPANY_SELECTOR_HEADER]: normalizedCompanyId } });
  } catch {
    return { status: "forbidden", reason: "invalid-response" };
  }
  if (!isSessionBootstrapResult(result)) return { status: "forbidden", reason: "invalid-response" };
  if (!result.ok) {
    if (result.reason === "unauthenticated") return { status: "unauthenticated" };
    return { status: "forbidden", reason: result.reason === "inactive-membership" ? "inactive-membership" : result.reason === "forbidden" ? "forbidden" : "invalid-response" };
  }
  if (!isValidMembership(result.membership, normalizedCompanyId)) return { status: "forbidden", reason: "invalid-response" };
  return { status: "ready", membership: result.membership };
}
