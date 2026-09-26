import { render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { CustomerSlaStatusPanel } from "./customer-sla-status-panel";

afterEach(() => vi.unstubAllGlobals());

describe("CustomerSlaStatusPanel", () => {
  it("renders loading then available server-derived presentation", async () => {
    let resolve!: (value: Response) => void;
    vi.stubGlobal("fetch", vi.fn(() => new Promise<Response>((done) => { resolve = done; })));
    render(<CustomerSlaStatusPanel />);
    expect(screen.getByText("Đang tải trạng thái SLA…")).toBeTruthy();
    resolve({ status: 200, ok: true, json: async () => ({
      serviceLabel: "Business", schedulerPriority: 2, priorityCeiling: 5,
      policyVersion: 7, effectiveAt: "2026-09-24T10:00:00Z",
    }) } as Response);
    await waitFor(() => expect(screen.getByText("Business")).toBeTruthy());
    expect(screen.getByText("2 / 5")).toBeTruthy();
  });

  it("renders forbidden without fabricating identity", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => ({ status: 403, ok: false } as Response)));
    render(<CustomerSlaStatusPanel />);
    await waitFor(() => expect(screen.getByText("Bạn không có quyền xem trạng thái SLA.")).toBeTruthy());
  });

  it("renders unavailable for malformed server data", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => ({ status: 200, ok: true, json: async () => ({ tenantId: "browser-tenant" }) } as Response)));
    render(<CustomerSlaStatusPanel />);
    await waitFor(() => expect(screen.getByText("Trạng thái SLA hiện chưa khả dụng từ máy chủ.")).toBeTruthy());
    expect(screen.queryByText("browser-tenant")).toBeNull();
  });
});
