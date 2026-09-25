import { describe, expect, it, vi } from "vitest";
import { fetchCustomerSlaStatus } from "./customer-sla-status";

function response(status: number, body: unknown): Response {
  return { status, ok: status >= 200 && status < 300, json: async () => body } as Response;
}

describe("fetchCustomerSlaStatus", () => {
  it("uses only the authenticated same-origin endpoint and maps allowlisted fields", async () => {
    const fetcher = vi.fn(async () => response(200, {
      serviceLabel: "Business", schedulerPriority: 2, priorityCeiling: 5,
      policyVersion: 7, effectiveAt: "2026-09-24T10:00:00Z",
      admissionId: "server-internal", tenantId: "ignored", companyName: "ignored",
    }));
    await expect(fetchCustomerSlaStatus(fetcher as typeof fetch)).resolves.toEqual({
      state: "available",
      data: { tierLabel: "Business", priorityLabel: "2 / 5", policyVersion: 7, effectiveAt: "2026-09-24T10:00:00Z" },
    });
    expect(fetcher).toHaveBeenCalledWith("/api/sla/status", {
      method: "GET", credentials: "same-origin", headers: { Accept: "application/json" },
    });
  });

  it.each([401, 403])("fails closed as forbidden for %s", async (status) => {
    await expect(fetchCustomerSlaStatus(async () => response(status, {}) as never)).resolves.toEqual({ state: "forbidden" });
  });

  it.each([404, 503])("fails closed as unavailable for %s", async (status) => {
    await expect(fetchCustomerSlaStatus(async () => response(status, {}) as never)).resolves.toEqual({ state: "unavailable" });
  });

  it.each([
    {},
    { serviceLabel: " Business", schedulerPriority: 2, priorityCeiling: 5, policyVersion: 7, effectiveAt: "2026-09-24T10:00:00Z" },
    { serviceLabel: "Business", schedulerPriority: 6, priorityCeiling: 5, policyVersion: 7, effectiveAt: "2026-09-24T10:00:00Z" },
    { serviceLabel: "Business", schedulerPriority: 2, priorityCeiling: 5, policyVersion: 7, effectiveAt: "2026-09-24T17:00:00+07:00" },
  ])("rejects malformed payload %#", async (body) => {
    await expect(fetchCustomerSlaStatus(async () => response(200, body) as never)).resolves.toEqual({ state: "unavailable" });
  });

  it("does not execute accessor-backed server fields", async () => {
    let reads = 0;
    const body = Object.defineProperty({}, "serviceLabel", { get() { reads += 1; return "Fabricated"; } });
    await expect(fetchCustomerSlaStatus(async () => response(200, body) as never)).resolves.toEqual({ state: "unavailable" });
    expect(reads).toBe(0);
  });

  it("fails closed on transport and JSON failures", async () => {
    await expect(fetchCustomerSlaStatus(async () => { throw new Error("network"); })).resolves.toEqual({ state: "unavailable" });
    await expect(fetchCustomerSlaStatus(async () => ({ status: 200, ok: true, json: async () => { throw new Error("json"); } }) as Response)).resolves.toEqual({ state: "unavailable" });
  });
});
