import { describe, expect, it } from "vitest";
import { resolveCompanyContext, type CompanyMembershipView } from "./company-context";

const membership = (roles: readonly string[]): CompanyMembershipView => ({
  tenantId: "minh-huy",
  companyId: "internal",
  companyName: "Minh Huy",
  userId: "server-user",
  userName: "Nhân viên",
  roles,
});

describe("resolveCompanyContext runtime role boundary", () => {
  it("fails closed when presentation membership contains duplicate roles", () => {
    expect(resolveCompanyContext(membership(["Workspace member", "Workspace member"]))).toEqual({
      status: "unavailable",
      reason: "Không thể xác định đầy đủ người dùng và công ty đang làm việc.",
    });
  });

  it("keeps an immutable role snapshot for unambiguous membership", () => {
    const roles = ["Workspace member", "Billing reader"];
    const context = resolveCompanyContext(membership(roles));

    expect(context.status).toBe("ready");
    if (context.status !== "ready") return;

    expect(context.roles).toEqual(roles);
    expect(context.roles).not.toBe(roles);
    expect(Object.isFrozen(context.roles)).toBe(true);
    expect(Object.isFrozen(context)).toBe(true);
  });
});
