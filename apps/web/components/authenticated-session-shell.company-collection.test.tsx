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

describe("AuthenticatedSessionShell company collection boundary", () => {
  it("does not execute a browser-controlled company iterator", () => {
    const iterator = vi.fn(function* () {
      yield { companyId: "spoofed", companyName: "Spoofed" };
    });
    const companies = [
      { companyId: "internal", companyName: "Minh Huy" },
      { companyId: "branch-2", companyName: "Chi nhánh 2" },
    ];
    Object.defineProperty(companies, Symbol.iterator, { value: iterator });

    render(
      <AuthenticatedSessionShell
        state={{ status: "ready", membership }}
        companies={companies}
        onSelectCompany={vi.fn()}
      />,
    );

    expect(iterator).not.toHaveBeenCalled();
    expect(screen.getByRole("combobox", { name: "Đổi công ty" })).toBeTruthy();
    expect(screen.getByRole("option", { name: "Chi nhánh 2" })).toBeTruthy();
  });
});
