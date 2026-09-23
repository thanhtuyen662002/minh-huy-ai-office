import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { AuthenticatedSessionState } from "../lib/authenticated-session";
import { AuthenticatedSessionShell } from "./authenticated-session-shell";
import { CustomerPortalShell } from "./customer-portal-shell";

const serverMembership = { tenantId: "tenant-server", companyId: "company-server", companyName: "Công ty từ máy chủ", userId: "user-server", userName: "Người dùng từ máy chủ", roles: ["Customer"] };

afterEach(cleanup);

describe("CustomerPortalShell authority boundary", () => {
  it("renders presentation identity only from the ready server membership snapshot", () => {
    render(<CustomerPortalShell state={{ status: "ready", membership: serverMembership }} />);
    expect(screen.getByRole("heading", { name: serverMembership.companyName })).toBeTruthy();
    expect(screen.getByText(`Đăng nhập với ${serverMembership.userName}`)).toBeTruthy();
    expect(screen.queryByText("browser-company")).toBeNull();
    expect(screen.queryByText("browser-user")).toBeNull();
  });

  it("fails closed for malformed presentation metadata instead of fabricating labels", () => {
    const malformed = { status: "ready", membership: { ...serverMembership, companyName: "", userName: " browser-user " } } as unknown as AuthenticatedSessionState;
    render(<CustomerPortalShell state={malformed} />);
    expect(screen.getByRole("alert")).toBeTruthy();
    expect(screen.getByText("Không thể mở cổng khách hàng")).toBeTruthy();
    expect(screen.queryByText("browser-user")).toBeNull();
  });

  it("rejects accessor-backed authority without executing hostile getters", () => {
    const companyNameGetter = vi.fn(() => "browser-company");
    const membership = { ...serverMembership } as Record<string, unknown>;
    Object.defineProperty(membership, "companyName", { configurable: true, get: companyNameGetter });
    render(<CustomerPortalShell state={{ status: "ready", membership } as unknown as AuthenticatedSessionState} />);
    expect(companyNameGetter).not.toHaveBeenCalled();
    expect(screen.getByRole("alert")).toBeTruthy();
    expect(screen.queryByText("browser-company")).toBeNull();
  });

  it("fails closed when the membership descriptor trap throws", () => {
    const hostileState = new Proxy({ status: "ready", membership: serverMembership }, { getOwnPropertyDescriptor(target, property) { if (property === "membership") throw new Error("hostile browser runtime"); return Reflect.getOwnPropertyDescriptor(target, property); } }) as unknown as AuthenticatedSessionState;
    render(<CustomerPortalShell state={hostileState} />);
    expect(screen.getByRole("alert")).toBeTruthy();
    expect(screen.queryByText(serverMembership.companyName)).toBeNull();
  });

  it("is reachable only after the authenticated-session boundary accepts a ready authority snapshot", () => {
    const { rerender } = render(<AuthenticatedSessionShell state={{ status: "unauthenticated" }} surface="customer-portal" />);
    expect(screen.getByRole("heading", { name: "Cần đăng nhập" })).toBeTruthy();
    expect(screen.queryByText(serverMembership.companyName)).toBeNull();
    rerender(<AuthenticatedSessionShell state={{ status: "ready", membership: serverMembership }} surface="customer-portal" />);
    expect(screen.getByRole("heading", { name: serverMembership.companyName })).toBeTruthy();
    expect(screen.getByText(`Đăng nhập với ${serverMembership.userName}`)).toBeTruthy();
  });

  it("keeps company switching as a request hint while the portal retains server authority", () => {
    const onSelectCompany = vi.fn();
    render(<AuthenticatedSessionShell state={{ status: "ready", membership: serverMembership }} surface="customer-portal" companies={[{ companyId: "company-server", companyName: "Công ty từ máy chủ" }, { companyId: "browser-company", companyName: "Browser company hint" }]} onSelectCompany={onSelectCompany} />);
    fireEvent.change(screen.getByLabelText("Đổi công ty"), { target: { value: "browser-company" } });
    expect(onSelectCompany).toHaveBeenCalledWith("browser-company");
    expect(screen.getByRole("heading", { name: serverMembership.companyName })).toBeTruthy();
    expect(screen.queryByRole("heading", { name: "Browser company hint" })).toBeNull();
  });

  it("shows only allowlisted capabilities carried by the ready server-derived session", () => {
    render(<CustomerPortalShell state={{ status: "ready", membership: serverMembership, portalCapabilities: ["customer-chat", "billing"] }} />);
    const navigation = screen.getByRole("navigation", { name: "Chức năng khách hàng" });
    expect(navigation.textContent).toContain("Trao đổi");
    expect(navigation.textContent).toContain("Sử dụng & chi phí");
    expect(navigation.textContent).not.toContain("Nhật ký hoạt động");
  });

  it("fails capability navigation closed when runtime metadata contains an unknown capability", () => {
    const state = { status: "ready", membership: serverMembership, portalCapabilities: ["customer-chat", "browser-admin"] } as unknown as AuthenticatedSessionState;
    render(<CustomerPortalShell state={state} />);
    expect(screen.queryByRole("navigation", { name: "Chức năng khách hàng" })).toBeNull();
    expect(screen.queryByText("Trao đổi")).toBeNull();
    expect(screen.getByRole("heading", { name: serverMembership.companyName })).toBeTruthy();
  });
});
