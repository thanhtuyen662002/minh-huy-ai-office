import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { AuthenticatedSessionShell } from "./authenticated-session-shell";

const membership = { tenantId: "minh-huy", companyId: "internal", companyName: "Minh Huy", userId: "server-user", userName: "Nhân viên", roles: ["Workspace member"] };

afterEach(cleanup);

describe("AuthenticatedSessionShell", () => {
  it("fails closed while authentication is loading", () => {
    render(<AuthenticatedSessionShell state={{ status: "loading", selectedCompanyId: "internal" }} />);
    expect(screen.getByRole("main").getAttribute("aria-busy")).toBe("true");
    expect(screen.queryByText("company.erp.production")).toBeNull();
  });

  it("fails closed for unauthenticated and forbidden sessions", () => {
    const { rerender } = render(<AuthenticatedSessionShell state={{ status: "unauthenticated" }} />);
    expect(screen.getByRole("heading", { name: "Cần đăng nhập" })).toBeTruthy();
    expect(screen.queryByText("company.erp.production")).toBeNull();
    rerender(<AuthenticatedSessionShell state={{ status: "forbidden", reason: "inactive-membership" }} />);
    expect(screen.getByRole("alert").textContent).toMatch(/không còn hoạt động/i);
    expect(screen.queryByText("company.erp.production")).toBeNull();
  });

  it("renders company data only from a ready server-validated membership", () => {
    render(<AuthenticatedSessionShell state={{ status: "ready", membership }} />);
    expect(screen.getByRole("region", { name: "Công ty đang làm việc" }).textContent).toContain("Minh Huy");
    expect(screen.getByRole("heading", { name: "company.erp.production" })).toBeTruthy();
  });

  it("treats company switching as a selector request rather than identity authority", () => {
    const onSelectCompany = vi.fn();
    render(<AuthenticatedSessionShell state={{ status: "ready", membership }} companies={[{ companyId: "internal", companyName: "Minh Huy" }, { companyId: "branch-2", companyName: "Chi nhánh 2" }]} onSelectCompany={onSelectCompany} />);
    fireEvent.change(screen.getByLabelText("Đổi công ty"), { target: { value: "branch-2" } });
    expect(onSelectCompany).toHaveBeenCalledWith("branch-2");
    expect(screen.getByRole("region", { name: "Công ty đang làm việc" }).textContent).toContain("Minh Huy");
    expect(screen.getByText(/máy chủ vẫn xác thực quyền truy cập/i)).toBeTruthy();
  });
});
