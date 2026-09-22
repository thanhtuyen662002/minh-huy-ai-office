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

describe("AuthenticatedSessionShell company collection bound", () => {
  it("fails closed before inspecting entries when a browser company list is oversized", () => {
    const oversized = new Array(1001) as Array<{ companyId: string; companyName: string }>;
    const firstEntryInspection = vi.fn(() => ({
      configurable: true,
      enumerable: true,
      writable: true,
      value: { companyId: "internal", companyName: "Minh Huy" },
    }));
    const companies = new Proxy(oversized, {
      getOwnPropertyDescriptor(target, property) {
        if (property === "0") return firstEntryInspection();
        return Reflect.getOwnPropertyDescriptor(target, property);
      },
    });

    const onSelectCompany = vi.fn();
    render(
      <AuthenticatedSessionShell
        state={{ status: "ready", membership }}
        companies={companies}
        onSelectCompany={onSelectCompany}
      />,
    );

    expect(firstEntryInspection).not.toHaveBeenCalled();
    expect(screen.queryByLabelText("Đổi công ty")).toBeNull();
    expect(onSelectCompany).not.toHaveBeenCalled();
  });
});
