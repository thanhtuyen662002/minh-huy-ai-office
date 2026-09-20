import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { AuthenticatedSessionShell } from "./authenticated-session-shell";

const membership = { tenantId: "minh-huy", companyId: "internal", companyName: "Minh Huy", userId: "server-user", userName: "Nhân viên", roles: ["Workspace member"] };
const companies = [{ companyId: "internal", companyName: "Minh Huy" }, { companyId: "branch-2", companyName: "Chi nhánh 2" }];

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
    render(<AuthenticatedSessionShell state={{ status: "ready", membership }} companies={companies} onSelectCompany={onSelectCompany} />);
    fireEvent.change(screen.getByLabelText("Đổi công ty"), { target: { value: "branch-2" } });
    expect(onSelectCompany).toHaveBeenCalledWith("branch-2");
    expect(screen.getByRole("region", { name: "Công ty đang làm việc" }).textContent).toContain("Minh Huy");
    expect(screen.getByText(/máy chủ vẫn xác thực quyền truy cập/i)).toBeTruthy();
  });

  it("clears stale company data while a newly selected company is being validated", () => {
    const onSelectCompany = vi.fn();
    const { rerender } = render(<AuthenticatedSessionShell state={{ status: "ready", membership }} companies={companies} onSelectCompany={onSelectCompany} />);
    expect(screen.getByRole("heading", { name: "company.erp.production" })).toBeTruthy();
    fireEvent.change(screen.getByLabelText("Đổi công ty"), { target: { value: "branch-2" } });
    expect(onSelectCompany).toHaveBeenCalledWith("branch-2");
    rerender(<AuthenticatedSessionShell state={{ status: "loading", selectedCompanyId: "branch-2" }} companies={companies} onSelectCompany={onSelectCompany} />);
    expect(screen.getByRole("main").getAttribute("aria-busy")).toBe("true");
    expect(screen.queryByRole("region", { name: "Công ty đang làm việc" })).toBeNull();
    expect(screen.queryByRole("heading", { name: "company.erp.production" })).toBeNull();
    expect(screen.queryByLabelText("Đổi công ty")).toBeNull();
  });

  it.each([
    ["unauthenticated", { status: "unauthenticated" } as const],
    ["forbidden", { status: "forbidden", reason: "forbidden" } as const],
    ["inactive membership", { status: "forbidden", reason: "inactive-membership" } as const],
    ["invalid response", { status: "forbidden", reason: "invalid-response" } as const],
  ])("clears stale company data when a ready session becomes %s", (_label, failedState) => {
    const { rerender } = render(<AuthenticatedSessionShell state={{ status: "ready", membership }} companies={companies} onSelectCompany={vi.fn()} />);
    expect(screen.getByRole("heading", { name: "company.erp.production" })).toBeTruthy();
    rerender(<AuthenticatedSessionShell state={failedState} companies={companies} onSelectCompany={vi.fn()} />);
    expect(screen.queryByRole("region", { name: "Công ty đang làm việc" })).toBeNull();
    expect(screen.queryByRole("heading", { name: "company.erp.production" })).toBeNull();
    expect(screen.queryByLabelText("Đổi công ty")).toBeNull();
    expect(screen.queryByText("Minh Huy")).toBeNull();
    expect(screen.queryByText("Chi nhánh 2")).toBeNull();
  });

  it("exposes accessible failure semantics without leaking company data", () => {
    render(<AuthenticatedSessionShell state={{ status: "forbidden", reason: "invalid-response" }} companies={companies} onSelectCompany={vi.fn()} />);
    const alert = screen.getByRole("alert");
    expect(alert.getAttribute("aria-labelledby")).toBe("scope-denied");
    expect(screen.getByRole("heading", { name: "Không thể mở phạm vi công ty" })).toBeTruthy();
    expect(alert.textContent).toMatch(/không thể xác thực phạm vi công ty/i);
    expect(screen.queryByText("Minh Huy")).toBeNull();
    expect(screen.queryByText("Chi nhánh 2")).toBeNull();
    expect(screen.queryByLabelText("Đổi công ty")).toBeNull();
  });
});
