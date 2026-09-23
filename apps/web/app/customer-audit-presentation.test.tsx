import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { CustomerAuditPresentation } from "../components/customer-audit-presentation";

const item = (overrides: Record<string, unknown> = {}) => ({
  auditId: "audit-1",
  taskId: "task-1",
  resource: "erp.invoice",
  action: "read",
  risk: "Low",
  authorized: true,
  decisionReason: "Policy allowed",
  occurredAtUtc: "2026-09-23T21:00:00Z",
  executionId: "execution-1",
  ...overrides,
});

afterEach(() => cleanup());

describe("CustomerAuditPresentation", () => {
  it("renders server-projected fields newest first without fabricating identity authority", () => {
    render(<CustomerAuditPresentation items={[
      item({ auditId: "a", occurredAtUtc: "2026-09-23T20:00:00Z", action: "older" }),
      item({ auditId: "b", occurredAtUtc: "2026-09-23T22:00:00Z", action: "newer", tenantId: "tenant-secret", companyId: "company-secret", userId: "user-secret", companyName: "Tên giả", userName: "Người giả" }),
    ]} />);
    const rows = screen.getAllByRole("listitem");
    expect(rows[0]?.textContent).toContain("newer");
    expect(rows[1]?.textContent).toContain("older");
    expect(screen.queryByText(/tenant-secret|company-secret|user-secret|Tên giả|Người giả/)).toBeNull();
  });

  it("shows an explicit empty state", () => {
    render(<CustomerAuditPresentation items={[]} />);
    expect(screen.queryByText("Chưa có hoạt động nào.")).not.toBeNull();
    expect(screen.queryByRole("listitem")).toBeNull();
  });

  it.each([
    [null],
    [[item({ auditId: "" })]],
    [[item({ authorized: "true" })]],
    [[item({ occurredAtUtc: "not-a-date" })]],
    [[item({ executionId: " execution-1" })]],
  ])("fails closed on malformed audit payload", (payload) => {
    render(<CustomerAuditPresentation items={payload} />);
    expect(screen.queryByRole("alert")).not.toBeNull();
  });

  it("deduplicates exact AuditId replay but rejects conflicting reuse", () => {
    const { rerender } = render(<CustomerAuditPresentation items={[item(), item()]} />);
    expect(screen.getAllByRole("listitem")).toHaveLength(1);
    rerender(<CustomerAuditPresentation items={[item(), item({ action: "write" })]} />);
    expect(screen.queryByRole("alert")).not.toBeNull();
  });

  it("uses AuditId as stable tie-breaker for equal timestamps", () => {
    render(<CustomerAuditPresentation items={[item({ auditId: "z", action: "Z" }), item({ auditId: "a", action: "A" })]} />);
    expect(screen.getAllByRole("listitem").map((row) => row.textContent?.startsWith("A"))).toEqual([true, false]);
  });

  it("does not render provider or worker diagnostics from extra fields", () => {
    render(<CustomerAuditPresentation items={[item({ provider: "provider-secret", model: "model-secret", workerId: "worker-secret" })]} />);
    expect(screen.queryByText(/provider-secret|model-secret|worker-secret/)).toBeNull();
    expect(screen.queryByText("read · erp.invoice")).not.toBeNull();
  });
});
