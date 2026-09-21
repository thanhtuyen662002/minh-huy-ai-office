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
  it("fails closed when browser-controlled collection length inspection throws", () => {
    const lengthInspection = vi.fn(() => {
      throw new Error("hostile company collection length");
    });
    const companies = new Proxy(
      [
        { companyId: "internal", companyName: "Minh Huy" },
        { companyId: "branch-2", companyName: "Chi nhánh 2" },
      ],
      {
        getOwnPropertyDescriptor(target, property) {
          if (property === "length") return lengthInspection();
          return Reflect.getOwnPropertyDescriptor(target, property);
        },
      },
    );

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

    expect(lengthInspection).toHaveBeenCalledTimes(1);
    expect(screen.queryByLabelText("Đổi công ty")).toBeNull();
    expect(onSelectCompany).not.toHaveBeenCalled();
  });
});
