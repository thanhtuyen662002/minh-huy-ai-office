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

describe("AuthenticatedSessionShell runtime company-option boundary", () => {
  it("fails closed instead of throwing when untrusted option fields are non-strings", () => {
    const onSelectCompany = vi.fn();
    const malformedRuntimeOptions = [
      { companyId: "internal", companyName: "Minh Huy" },
      { companyId: 42, companyName: "Numeric id" },
      { companyId: "branch-2", companyName: { spoofed: true } },
    ] as unknown as readonly { companyId: string; companyName: string }[];

    expect(() =>
      render(
        <AuthenticatedSessionShell
          state={{ status: "ready", membership }}
          companies={malformedRuntimeOptions}
          onSelectCompany={onSelectCompany}
        />,
      ),
    ).not.toThrow();
    expect(screen.queryByLabelText("Đổi công ty")).toBeNull();
    expect(onSelectCompany).not.toHaveBeenCalled();
    expect(screen.getByRole("region", { name: "Công ty đang làm việc" }).textContent).toContain("Minh Huy");
  });
});
