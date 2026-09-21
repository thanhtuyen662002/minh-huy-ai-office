import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { AuthenticatedSessionShell } from "./authenticated-session-shell";

const membership = { tenantId: "minh-huy", companyId: "internal", companyName: "Minh Huy", userId: "server-user", userName: "Nhân viên", roles: ["Workspace member"] };
const companies = [{ companyId: "internal", companyName: "Minh Huy" }, { companyId: "branch-2", companyName: "Chi nhánh 2" }];

afterEach(cleanup);

describe("AuthenticatedSessionShell", () => {
  it("fails closed while authentication is loading and announces progress accessibly", () => {
    render(<AuthenticatedSessionShell state={{ status: "loading", selectedCompanyId: "internal" }} />);
    expect(screen.getByRole("main").getAttribute("aria-busy")).toBe("true");
    const status = screen.getByRole("status");
    expect(status.getAttribute("aria-live")).toBe("polite");
    expect(status.getAttribute("aria-labelledby")).toBe("session-loading");
    expect(screen.queryByText("company.erp.production")).toBeNull();
  });

  it("fails closed for unauthenticated and forbidden sessions with accessible alerts", () => {
    const { rerender } = render(<AuthenticatedSessionShell state={{ status: "unauthenticated" }} />);
    expect(screen.getByRole("heading", { name: "Cần đăng nhập" })).toBeTruthy();
    expect(screen.getByRole("alert").getAttribute("aria-labelledby")).toBe("session-required");
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
    const selector = screen.getByLabelText("Đổi công ty") as HTMLSelectElement;
    expect(selector.value).toBe("internal");
    fireEvent.change(selector, { target: { value: "branch-2" } });
    expect(onSelectCompany).toHaveBeenCalledWith("branch-2");
    expect(screen.getByRole("region", { name: "Công ty đang làm việc" }).textContent).toContain("Minh Huy");
    expect(selector.value).toBe("internal");
    expect(screen.getByText(/máy chủ vẫn xác thực quyền truy cập/i)).toBeTruthy();
  });

  it("only emits company-switch requests for offered alternative scopes", () => {
    const onSelectCompany = vi.fn();
    render(<AuthenticatedSessionShell state={{ status: "ready", membership }} companies={companies} onSelectCompany={onSelectCompany} />);
    const selector = screen.getByLabelText("Đổi công ty") as HTMLSelectElement;
    fireEvent.change(selector, { target: { value: "internal" } });
    fireEvent.change(selector, { target: { value: "spoofed-company" } });
    expect(onSelectCompany).not.toHaveBeenCalled();
    expect(screen.getByRole("region", { name: "Công ty đang làm việc" }).textContent).toContain("Minh Huy");
  });

  it("filters malformed company selectors before they can become switch requests", () => {
    const onSelectCompany = vi.fn();
    const malformedCompanies = [companies[0], { companyId: " branch-2 ", companyName: "Spoofed padded scope" }, { companyId: "", companyName: "Blank scope" }];
    render(<AuthenticatedSessionShell state={{ status: "ready", membership }} companies={malformedCompanies} onSelectCompany={onSelectCompany} />);
    expect(screen.queryByLabelText("Đổi công ty")).toBeNull();
    expect(screen.queryByText("Spoofed padded scope")).toBeNull();
    expect(screen.queryByText("Blank scope")).toBeNull();
    expect(onSelectCompany).not.toHaveBeenCalled();
    expect(screen.getByRole("region", { name: "Công ty đang làm việc" }).textContent).toContain("Minh Huy");
  });

  it("filters malformed company labels instead of exposing ambiguous switch choices", () => {
    const onSelectCompany = vi.fn();
    const malformedLabels = [companies[0], { companyId: "branch-2", companyName: "" }, { companyId: "branch-3", companyName: " Padded label " }];
    render(<AuthenticatedSessionShell state={{ status: "ready", membership }} companies={malformedLabels} onSelectCompany={onSelectCompany} />);
    expect(screen.queryByLabelText("Đổi công ty")).toBeNull();
    expect(screen.queryByText("Padded label")).toBeNull();
    expect(onSelectCompany).not.toHaveBeenCalled();
    expect(screen.getByRole("region", { name: "Công ty đang làm việc" }).textContent).toContain("Minh Huy");
  });

  it("rejects duplicate company IDs instead of rendering ambiguous switch metadata", () => {
    const onSelectCompany = vi.fn();
    const ambiguousCompanies = [companies[0], companies[1], { companyId: "branch-2", companyName: "Spoofed branch label" }];
    render(<AuthenticatedSessionShell state={{ status: "ready", membership }} companies={ambiguousCompanies} onSelectCompany={onSelectCompany} />);
    expect(screen.queryByLabelText("Đổi công ty")).toBeNull();
    expect(screen.queryByText("Chi nhánh 2")).toBeNull();
    expect(screen.queryByText("Spoofed branch label")).toBeNull();
    expect(onSelectCompany).not.toHaveBeenCalled();
    expect(screen.getByRole("region", { name: "Công ty đang làm việc" }).textContent).toContain("Minh Huy");
  });

  it("keeps the authoritative company selected when untrusted options omit it", () => {
    const onSelectCompany = vi.fn();
    render(<AuthenticatedSessionShell state={{ status: "ready", membership }} companies={[companies[1]]} onSelectCompany={onSelectCompany} />);
    expect(screen.queryByLabelText("Đổi công ty")).toBeNull();
    expect(screen.getByRole("region", { name: "Công ty đang làm việc" }).textContent).toContain("Minh Huy");
    expect(onSelectCompany).not.toHaveBeenCalled();
  });

  it("does not expose switching when multiple untrusted options still omit the authoritative company", () => {
    const onSelectCompany = vi.fn();
    const untrustedCompanies = [companies[1], { companyId: "branch-3", companyName: "Chi nhánh 3" }];
    render(<AuthenticatedSessionShell state={{ status: "ready", membership }} companies={untrustedCompanies} onSelectCompany={onSelectCompany} />);
    expect(screen.queryByLabelText("Đổi công ty")).toBeNull();
    expect(screen.getByRole("region", { name: "Công ty đang làm việc" }).textContent).toContain("Minh Huy");
    expect(onSelectCompany).not.toHaveBeenCalled();
  });

  it("does not expose a company selector when switching cannot produce an alternative scope", () => {
    render(<AuthenticatedSessionShell state={{ status: "ready", membership }} companies={[companies[0]]} onSelectCompany={vi.fn()} />);
    expect(screen.queryByLabelText("Đổi công ty")).toBeNull();
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
