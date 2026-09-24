import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { CustomerSlaPresentation } from "./customer-sla-presentation";

describe("CustomerSlaPresentation", () => {
  it("renders only explicit server presentation fields", () => {
    render(<CustomerSlaPresentation data={{ tierLabel: "Business", priorityLabel: "Cao", policyVersion: 7, effectiveAt: "2026-09-24T10:00:00Z" }} />);

    expect(screen.getByText("Business")).toBeTruthy();
    expect(screen.getByText("Cao")).toBeTruthy();
    expect(screen.getByText("7")).toBeTruthy();
    expect(screen.getByText("2026-09-24T10:00:00Z")).toBeTruthy();
    expect(screen.queryByText(/tenant|company|user/i)).toBeNull();
  });

  it.each([
    null,
    {},
    { tierLabel: " Business", priorityLabel: "Cao", policyVersion: 7, effectiveAt: "2026-09-24T10:00:00Z" },
    { tierLabel: "Business", priorityLabel: "Cao", policyVersion: 0, effectiveAt: "2026-09-24T10:00:00Z" },
    { tierLabel: "Business", priorityLabel: "Cao", policyVersion: 7, effectiveAt: "not-a-date" },
  ])("fails closed for malformed presentation data %#", (data) => {
    render(<CustomerSlaPresentation data={data} />);
    expect(screen.getByText("Trạng thái SLA hiện chưa khả dụng từ máy chủ.")).toBeTruthy();
    expect(screen.queryByText("Business")).toBeNull();
  });

  it("does not execute accessor-backed presentation fields", () => {
    let reads = 0;
    const data = Object.defineProperty({}, "tierLabel", {
      enumerable: true,
      get() {
        reads += 1;
        return "Fabricated";
      },
    });

    render(<CustomerSlaPresentation data={data} />);

    expect(reads).toBe(0);
    expect(screen.getByText("Trạng thái SLA hiện chưa khả dụng từ máy chủ.")).toBeTruthy();
    expect(screen.queryByText("Fabricated")).toBeNull();
  });

  it("ignores identity-shaped browser metadata instead of presenting it as authority", () => {
    render(<CustomerSlaPresentation data={{
      tierLabel: "Business",
      priorityLabel: "Cao",
      policyVersion: 7,
      effectiveAt: "2026-09-24T10:00:00Z",
      tenantId: "browser-tenant",
      companyName: "Browser Company",
      userName: "Browser User",
    }} />);

    expect(screen.getByText("Business")).toBeTruthy();
    expect(screen.queryByText("Browser Company")).toBeNull();
    expect(screen.queryByText("Browser User")).toBeNull();
    expect(screen.queryByText("browser-tenant")).toBeNull();
  });
});
