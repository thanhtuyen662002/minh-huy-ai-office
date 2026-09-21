import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { AuthenticatedSessionShell } from "./authenticated-session-shell";

const membership = {
  tenantId: "minh-huy",
  companyId: "internal",
  companyName: "Minh Huy",
  userId: "server-user",
  userName: "Nhân viên",
  roles: ["Workspace member"],
};

afterEach(cleanup);

describe("AuthenticatedSessionShell company collection length boundary", () => {
  it("does not execute an accessor-backed collection length while deciding offered scopes", () => {
    const lengthGetter = vi.fn(() => 2);
    const companies = [
      { companyId: "internal", companyName: "Minh Huy" },
      { companyId: "branch-2", companyName: "Chi nhánh 2" },
    ];
    Object.defineProperty(companies, "length", { get: lengthGetter });

    const onSelectCompany = vi.fn();
    expect(() =>
      render(
        <AuthenticatedSessionShell
          state={{ status: "ready", membership }}
          companies={companies}
          onSelectCompany={onSelectCompany}
        />,
      ),
    ).not.toThrow();

    expect(lengthGetter).not.toHaveBeenCalled();
    expect(screen.queryByLabelText("Đổi công ty")).toBeNull();
    expect(onSelectCompany).not.toHaveBeenCalled();
  });
});
