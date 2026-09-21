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

  it("rejects inherited company metadata instead of treating prototype values as offered scopes", () => {
    const onSelectCompany = vi.fn();
    const inherited = Object.create({ companyId: "branch-2", companyName: "Spoofed branch" });
    const inheritedRuntimeOptions = [
      { companyId: "internal", companyName: "Minh Huy" },
      inherited,
    ] as readonly { companyId: string; companyName: string }[];

    render(
      <AuthenticatedSessionShell
        state={{ status: "ready", membership }}
        companies={inheritedRuntimeOptions}
        onSelectCompany={onSelectCompany}
      />,
    );

    expect(screen.queryByLabelText("Đổi công ty")).toBeNull();
    expect(onSelectCompany).not.toHaveBeenCalled();
    expect(screen.getByRole("region", { name: "Công ty đang làm việc" }).textContent).toContain("Minh Huy");
  });

  it("rejects accessor-backed company metadata without invoking browser-controlled getters", () => {
    const onSelectCompany = vi.fn();
    const companyIdGetter = vi.fn(() => "branch-2");
    const accessorBacked = { companyName: "Spoofed branch" } as Record<string, unknown>;
    Object.defineProperty(accessorBacked, "companyId", { enumerable: true, get: companyIdGetter });
    const accessorRuntimeOptions = [
      { companyId: "internal", companyName: "Minh Huy" },
      accessorBacked,
    ] as unknown as readonly { companyId: string; companyName: string }[];

    expect(() =>
      render(
        <AuthenticatedSessionShell
          state={{ status: "ready", membership }}
          companies={accessorRuntimeOptions}
          onSelectCompany={onSelectCompany}
        />,
      ),
    ).not.toThrow();

    expect(companyIdGetter).not.toHaveBeenCalled();
    expect(screen.queryByLabelText("Đổi công ty")).toBeNull();
    expect(onSelectCompany).not.toHaveBeenCalled();
    expect(screen.getByRole("region", { name: "Công ty đang làm việc" }).textContent).toContain("Minh Huy");
  });

  it("fails closed when hostile browser metadata throws during property inspection", () => {
    const onSelectCompany = vi.fn();
    const hostileOption = new Proxy(
      {},
      {
        getOwnPropertyDescriptor() {
          throw new Error("hostile browser metadata");
        },
      },
    );
    const hostileRuntimeOptions = [
      { companyId: "internal", companyName: "Minh Huy" },
      hostileOption,
    ] as unknown as readonly { companyId: string; companyName: string }[];

    expect(() =>
      render(
        <AuthenticatedSessionShell
          state={{ status: "ready", membership }}
          companies={hostileRuntimeOptions}
          onSelectCompany={onSelectCompany}
        />,
      ),
    ).not.toThrow();
    expect(screen.queryByLabelText("Đổi công ty")).toBeNull();
    expect(onSelectCompany).not.toHaveBeenCalled();
    expect(screen.getByRole("region", { name: "Công ty đang làm việc" }).textContent).toContain("Minh Huy");
  });
});
