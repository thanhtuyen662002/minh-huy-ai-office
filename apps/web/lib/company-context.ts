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

const required = (value: string) => value.trim().length > 0;

export function resolveCompanyContext(
  membership: CompanyMembershipView | null | undefined,
): CompanyContextResult {
  if (
    !membership ||
    !required(membership.tenantId) ||
    !required(membership.companyId) ||
    !required(membership.companyName) ||
    !required(membership.userId) ||
    !required(membership.userName)
  ) {
    return {
      status: "unavailable",
      reason: "Không thể xác định đầy đủ người dùng và công ty đang làm việc.",
    };
  }

  return { ...membership, status: "ready" };
}
