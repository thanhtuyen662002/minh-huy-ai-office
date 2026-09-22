export type CompanyMembershipView = {
  tenantId: string;
  companyId: string;
  companyName: string;
  userId: string;
  userName: string;
  roles: readonly string[];
};

export type CompanyContext = CompanyMembershipView & {
  status: "ready";
};

export type CompanyContextResult =
  | CompanyContext
  | { status: "unavailable"; reason: string };

const unavailable = (): CompanyContextResult => ({
  status: "unavailable",
  reason: "Không thể xác định đầy đủ người dùng và công ty đang làm việc.",
});

const ownDataValue = (value: object, key: PropertyKey): unknown => {
  const descriptor = Object.getOwnPropertyDescriptor(value, key);
  return descriptor && "value" in descriptor ? descriptor.value : undefined;
};

const canonicalText = (value: unknown): value is string =>
  typeof value === "string" && value.length > 0 && value.trim() === value;

const snapshotRoles = (value: unknown): readonly string[] | null => {
  if (!Array.isArray(value)) return null;
  const length = ownDataValue(value, "length");
  if (!Number.isSafeInteger(length) || (length as number) < 0) return null;
  const roles: string[] = [];
  const seen = new Set<string>();
  for (let index = 0; index < (length as number); index += 1) {
    const role = ownDataValue(value, String(index));
    if (!canonicalText(role) || seen.has(role)) return null;
    seen.add(role);
    roles.push(role);
  }
  return Object.freeze(roles);
};

export function resolveCompanyContext(
  membership: CompanyMembershipView | null | undefined,
): CompanyContextResult {
  try {
    if (!membership || typeof membership !== "object") return unavailable();
    const tenantId = ownDataValue(membership, "tenantId");
    const companyId = ownDataValue(membership, "companyId");
    const companyName = ownDataValue(membership, "companyName");
    const userId = ownDataValue(membership, "userId");
    const userName = ownDataValue(membership, "userName");
    const roles = snapshotRoles(ownDataValue(membership, "roles"));
    if (
      !canonicalText(tenantId) ||
      !canonicalText(companyId) ||
      !canonicalText(companyName) ||
      !canonicalText(userId) ||
      !canonicalText(userName) ||
      !roles
    ) return unavailable();
    return Object.freeze({ tenantId, companyId, companyName, userId, userName, roles, status: "ready" });
  } catch {
    return unavailable();
  }
}
