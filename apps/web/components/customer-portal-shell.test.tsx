import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { AuthenticatedSessionState } from "../lib/authenticated-session";
import { CustomerPortalShell } from "./customer-portal-shell";

const serverMembership = {
  tenantId: "tenant-server",
  companyId: "company-server",
  companyName: "Công ty từ máy chủ",
  userId: "user-server",
  userName: "Người dùng từ máy chủ",
  roles: ["Customer"],
};

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
    const malformed = {
      status: "ready",
      membership: { ...serverMembership, companyName: "", userName: " browser-user " },
    } as unknown as AuthenticatedSessionState;

    render(<CustomerPortalShell state={malformed} />);

    expect(screen.getByRole("alert")).toBeTruthy();
    expect(screen.getByText("Không thể mở cổng khách hàng")).toBeTruthy();
    expect(screen.queryByText("browser-user")).toBeNull();
  });

  it("rejects accessor-backed authority without executing hostile getters", () => {
    const companyNameGetter = vi.fn(() => "browser-company");
    const membership = { ...serverMembership } as Record<string, unknown>;
    Object.defineProperty(membership, "companyName", { configurable: true, get: companyNameGetter });

    render(
      <CustomerPortalShell
        state={{ status: "ready", membership } as unknown as AuthenticatedSessionState}
      />,
    );

    expect(companyNameGetter).not.toHaveBeenCalled();
    expect(screen.getByRole("alert")).toBeTruthy();
    expect(screen.queryByText("browser-company")).toBeNull();
  });

  it("fails closed when the membership descriptor trap throws", () => {
    const hostileState = new Proxy(
      { status: "ready", membership: serverMembership },
      {
        getOwnPropertyDescriptor(target, property) {
          if (property === "membership") throw new Error("hostile browser runtime");
          return Reflect.getOwnPropertyDescriptor(target, property);
        },
      },
    ) as unknown as AuthenticatedSessionState;

    render(<CustomerPortalShell state={hostileState} />);

    expect(screen.getByRole("alert")).toBeTruthy();
    expect(screen.queryByText(serverMembership.companyName)).toBeNull();
  });
});
