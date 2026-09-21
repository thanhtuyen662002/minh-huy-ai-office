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
    expect(() => render(<AuthenticatedSessionShell state={{ status: "ready", membership }} companies={malformedRuntimeOptions} onSelectCompany={onSelectCompany} />)).not.toThrow();
    expect(screen.queryByLabelText("Đổi công ty")).toBeNull();
    expect(onSelectCompany).not.toHaveBeenCalled();
    expect(screen.getByRole("region", { name: "Công ty đang làm việc" }).textContent).toContain("Minh Huy");
  });

  it("rejects inherited company metadata instead of treating prototype values as offered scopes", () => {
    const onSelectCompany = vi.fn();
    const inherited = Object.create({ companyId: "branch-2", companyName: "Spoofed branch" });
    const inheritedRuntimeOptions = [{ companyId: "internal", companyName: "Minh Huy" }, inherited] as readonly { companyId: string; companyName: string }[];
    render(<AuthenticatedSessionShell state={{ status: "ready", membership }} companies={inheritedRuntimeOptions} onSelectCompany={onSelectCompany} />);
    expect(screen.queryByLabelText("Đổi công ty")).toBeNull();
    expect(onSelectCompany).not.toHaveBeenCalled();
  });

  it("rejects accessor-backed company metadata without invoking browser-controlled getters", () => {
    const onSelectCompany = vi.fn();
    const companyIdGetter = vi.fn(() => "branch-2");
    const accessorBacked = { companyName: "Spoofed branch" } as Record<string, unknown>;
    Object.defineProperty(accessorBacked, "companyId", { enumerable: true, get: companyIdGetter });
    const accessorRuntimeOptions = [{ companyId: "internal", companyName: "Minh Huy" }, accessorBacked] as unknown as readonly { companyId: string; companyName: string }[];
    expect(() => render(<AuthenticatedSessionShell state={{ status: "ready", membership }} companies={accessorRuntimeOptions} onSelectCompany={onSelectCompany} />)).not.toThrow();
    expect(companyIdGetter).not.toHaveBeenCalled();
    expect(screen.queryByLabelText("Đổi công ty")).toBeNull();
    expect(onSelectCompany).not.toHaveBeenCalled();
  });

  it("fails closed when hostile browser metadata throws during property inspection", () => {
    const onSelectCompany = vi.fn();
    const hostileOption = new Proxy({}, { getOwnPropertyDescriptor() { throw new Error("hostile browser metadata"); } });
    const hostileRuntimeOptions = [{ companyId: "internal", companyName: "Minh Huy" }, hostileOption] as unknown as readonly { companyId: string; companyName: string }[];
    expect(() => render(<AuthenticatedSessionShell state={{ status: "ready", membership }} companies={hostileRuntimeOptions} onSelectCompany={onSelectCompany} />)).not.toThrow();
    expect(screen.queryByLabelText("Đổi công ty")).toBeNull();
    expect(onSelectCompany).not.toHaveBeenCalled();
  });

  it("fails closed when a hostile company collection throws during iteration", () => {
    const onSelectCompany = vi.fn();
    const hostileCollection = new Proxy([{ companyId: "internal", companyName: "Minh Huy" }], { get(target, property, receiver) { if (property === Symbol.iterator) throw new Error("hostile company collection"); return Reflect.get(target, property, receiver); } }) as readonly { companyId: string; companyName: string }[];
    expect(() => render(<AuthenticatedSessionShell state={{ status: "ready", membership }} companies={hostileCollection} onSelectCompany={onSelectCompany} />)).not.toThrow();
    expect(screen.queryByLabelText("Đổi công ty")).toBeNull();
    expect(onSelectCompany).not.toHaveBeenCalled();
  });

  it("fails closed instead of throwing when authoritative company membership is hostile", () => {
    const onSelectCompany = vi.fn();
    const hostileMembership = new Proxy(membership, { get(target, property, receiver) { if (property === "companyId") throw new Error("hostile authoritative membership"); return Reflect.get(target, property, receiver); } });
    expect(() => render(<AuthenticatedSessionShell state={{ status: "ready", membership: hostileMembership }} companies={[{ companyId: "internal", companyName: "Minh Huy" }, { companyId: "branch-2", companyName: "Chi nhánh 2" }]} onSelectCompany={onSelectCompany} />)).not.toThrow();
    expect(screen.queryByLabelText("Đổi công ty")).toBeNull();
    expect(onSelectCompany).not.toHaveBeenCalled();
  });

  it("rejects accessor-backed authoritative company id without invoking its getter", () => {
    const onSelectCompany = vi.fn();
    const companyIdGetter = vi.fn(() => "internal");
    const accessorMembership = { ...membership } as Record<string, unknown>;
    Object.defineProperty(accessorMembership, "companyId", { enumerable: true, get: companyIdGetter });
    expect(() => render(<AuthenticatedSessionShell state={{ status: "ready", membership: accessorMembership as typeof membership }} companies={[{ companyId: "internal", companyName: "Minh Huy" }, { companyId: "branch-2", companyName: "Chi nhánh 2" }]} onSelectCompany={onSelectCompany} />)).not.toThrow();
    expect(companyIdGetter).not.toHaveBeenCalled();
    expect(screen.getByRole("alert").textContent).toContain("Không thể xác thực phạm vi công ty");
    expect(screen.queryByLabelText("Đổi công ty")).toBeNull();
    expect(onSelectCompany).not.toHaveBeenCalled();
  });

  it("fails closed when the runtime company-switch callback is not callable", () => {
    const invalidCallback = { spoofed: true } as unknown as (companyId: string) => void;
    expect(() => render(<AuthenticatedSessionShell state={{ status: "ready", membership }} companies={[{ companyId: "internal", companyName: "Minh Huy" }, { companyId: "branch-2", companyName: "Chi nhánh 2" }]} onSelectCompany={invalidCallback} />)).not.toThrow();
    expect(screen.queryByLabelText("Đổi công ty")).toBeNull();
  });
});
