import type { CompanyMembershipView } from "./company-context";

export const COMPANY_SELECTOR_HEADER = "X-AIOffice-Company-Id";
export type PortalCapability = "customer-chat" | "billing" | "audit";

export type SessionFailure = "unauthenticated" | "forbidden" | "inactive-membership" | "invalid-response";
export type AuthenticatedSessionState =
  | { status: "loading"; selectedCompanyId: string | null }
  | { status: "unauthenticated" }
  | { status: "forbidden"; reason: "forbidden" | "inactive-membership" | "invalid-response" }
  | { status: "ready"; membership: CompanyMembershipView; portalCapabilities?: readonly PortalCapability[] };
export type SessionBootstrapResult =
  | { ok: true; membership: CompanyMembershipView; portalCapabilities?: readonly PortalCapability[] }
  | { ok: false; reason: SessionFailure };
export type SessionBootstrapTransport = (request: {
  /** Untrusted selector only. Server membership validation remains authoritative. */
  selectedCompanyId: string;
  headers: Readonly<Record<string, string>>;
}) => Promise<SessionBootstrapResult>;

const hasText = (value: unknown): value is string => typeof value === "string" && value.trim().length > 0;
const hasCanonicalText = (value: unknown): value is string => hasText(value) && value.trim() === value;

function ownDataValue(value: object, key: PropertyKey): unknown {
  const descriptor = Object.getOwnPropertyDescriptor(value, key);
  return descriptor && "value" in descriptor ? descriptor.value : undefined;
}

function snapshotCanonicalRoles(value: unknown): readonly string[] | null {
  if (!Array.isArray(value)) return null;
  const length = ownDataValue(value, "length");
  if (!Number.isSafeInteger(length) || (length as number) < 0) return null;
  const snapshot: string[] = [];
  for (let index = 0; index < (length as number); index += 1) {
    const role = ownDataValue(value, String(index));
    if (!hasCanonicalText(role)) return null;
    snapshot.push(role);
  }
  return new Set(snapshot).size === snapshot.length ? Object.freeze(snapshot) : null;
}

const isPortalCapability = (value: unknown): value is PortalCapability =>
  value === "customer-chat" || value === "billing" || value === "audit";

function snapshotPortalCapabilities(value: unknown): readonly PortalCapability[] | null {
  if (!Array.isArray(value)) return null;
  const length = ownDataValue(value, "length");
  if (!Number.isSafeInteger(length) || (length as number) < 0 || (length as number) > 32) return null;
  const snapshot: PortalCapability[] = [];
  for (let index = 0; index < (length as number); index += 1) {
    const capability = ownDataValue(value, String(index));
    if (!isPortalCapability(capability) || snapshot.includes(capability)) return null;
    snapshot.push(capability);
  }
  return Object.freeze(snapshot);
}

function normalizeMembership(value: unknown, selectedCompanyId: string): CompanyMembershipView | null {
  if (value === null || typeof value !== "object") return null;
  const tenantId = ownDataValue(value, "tenantId");
  const companyId = ownDataValue(value, "companyId");
  const companyName = ownDataValue(value, "companyName");
  const userId = ownDataValue(value, "userId");
  const userName = ownDataValue(value, "userName");
  const roles = snapshotCanonicalRoles(ownDataValue(value, "roles"));
  if (!hasCanonicalText(tenantId) || !hasCanonicalText(companyId) || companyId !== selectedCompanyId || !hasCanonicalText(companyName) || !hasCanonicalText(userId) || !hasCanonicalText(userName) || !roles) return null;
  return Object.freeze({ tenantId, companyId, companyName, userId, userName, roles });
}

function inspectSessionBootstrapResult(value: unknown):
  | { kind: "success"; membership: unknown; portalCapabilities: unknown; hasPortalCapabilities: boolean }
  | { kind: "failure"; reason: SessionFailure }
  | null {
  if (value === null || typeof value !== "object") return null;
  const ok = ownDataValue(value, "ok");
  const membership = ownDataValue(value, "membership");
  const reason = ownDataValue(value, "reason");
  const portalCapabilities = ownDataValue(value, "portalCapabilities");
  const hasPortalCapabilities = Object.prototype.hasOwnProperty.call(value, "portalCapabilities");
  if (ok === true && membership !== undefined && reason === undefined) return { kind: "success", membership, portalCapabilities, hasPortalCapabilities };
  if (ok === false && membership === undefined && (reason === "unauthenticated" || reason === "forbidden" || reason === "inactive-membership" || reason === "invalid-response")) return { kind: "failure", reason };
  return null;
}

/** Browser state may choose a company, but never supplies TenantId/UserId authority. */
export async function bootstrapAuthenticatedSession(selectedCompanyId: string | null | undefined, transport: SessionBootstrapTransport): Promise<AuthenticatedSessionState> {
  if (!hasCanonicalText(selectedCompanyId)) return { status: "forbidden", reason: "invalid-response" };
  let result: unknown;
  try {
    const headers = Object.freeze({ [COMPANY_SELECTOR_HEADER]: selectedCompanyId });
    const request = Object.freeze({ selectedCompanyId, headers });
    result = await transport(request);
  } catch {
    return { status: "forbidden", reason: "invalid-response" };
  }

  try {
    const inspected = inspectSessionBootstrapResult(result);
    if (!inspected) return { status: "forbidden", reason: "invalid-response" };
    if (inspected.kind === "failure") {
      if (inspected.reason === "unauthenticated") return { status: "unauthenticated" };
      return { status: "forbidden", reason: inspected.reason === "inactive-membership" ? "inactive-membership" : inspected.reason === "forbidden" ? "forbidden" : "invalid-response" };
    }
    const membership = normalizeMembership(inspected.membership, selectedCompanyId);
    if (!membership) return { status: "forbidden", reason: "invalid-response" };
    if (!inspected.hasPortalCapabilities) return { status: "ready", membership };
    const portalCapabilities = snapshotPortalCapabilities(inspected.portalCapabilities);
    if (!portalCapabilities) return { status: "forbidden", reason: "invalid-response" };
    return { status: "ready", membership, portalCapabilities };
  } catch {
    return { status: "forbidden", reason: "invalid-response" };
  }
}
