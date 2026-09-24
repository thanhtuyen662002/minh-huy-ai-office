import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { CustomerAuditPresentation } from "./customer-audit-presentation";

afterEach(cleanup);

const items = [
  { auditId: "audit-new", taskId: "task-2", resource: "Đơn hàng", action: "Phê duyệt", risk: "low", authorized: true, decisionReason: "policy", occurredAtUtc: "2026-09-24T03:00:00Z", executionId: null },
  { auditId: "audit-old", taskId: "task-1", resource: "Hóa đơn", action: "Xem chi tiết", risk: "low", authorized: true, decisionReason: "policy", occurredAtUtc: "2026-09-24T02:00:00Z", executionId: null },
];

describe("CustomerAuditPresentation filtering", () => {
  it("normalizes whitespace and case while preserving canonical newest-first ordering", () => {
    render(<CustomerAuditPresentation items={items} />);
    const before = screen.getAllByRole("listitem").map((node) => node.textContent);
    expect(before[0]).toContain("Phê duyệt");
    expect(before[1]).toContain("Xem chi tiết");

    fireEvent.change(screen.getByLabelText("Lọc theo hành động hoặc tài nguyên"), { target: { value: "  hóa   ĐƠN  " } });
    expect(screen.getByRole("status").textContent).toBe("Hiển thị 1 / 2 hoạt động");
    expect(screen.getAllByRole("listitem")[0].textContent).toContain("Hóa đơn");
  });

  it("shows an explicit no-match state and can reset the presentation filter", () => {
    render(<CustomerAuditPresentation items={items} />);
    const filter = screen.getByLabelText("Lọc theo hành động hoặc tài nguyên") as HTMLInputElement;
    fireEvent.change(filter, { target: { value: "browser-company browser-user" } });
    expect(screen.getByText("Không có hoạt động phù hợp với bộ lọc.")).toBeTruthy();
    expect(screen.queryByRole("listitem")).toBeNull();
    expect(screen.getByRole("status").textContent).toBe("Hiển thị 0 / 2 hoạt động");

    fireEvent.click(screen.getByRole("button", { name: "Xóa bộ lọc" }));
    expect(filter.value).toBe("");
    expect(screen.getByRole("status").textContent).toBe("Hiển thị 2 / 2 hoạt động");
    expect(screen.queryByRole("button", { name: "Xóa bộ lọc" })).toBeNull();
  });

  it("keeps filtering presentation-only and ignores authority-like extra fields", () => {
    render(<CustomerAuditPresentation items={items.map((item) => ({ ...item, tenantId: "browser-tenant", companyName: "browser-company", userName: "browser-user" }))} />);
    expect(screen.queryByText("browser-company")).toBeNull();
    expect(screen.queryByText("browser-user")).toBeNull();
    expect(screen.queryByText("browser-tenant")).toBeNull();
    expect(screen.getByRole("status").textContent).toBe("Hiển thị 2 / 2 hoạt động");
  });
});
