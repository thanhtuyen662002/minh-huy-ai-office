import { afterEach, expect, it, vi } from "vitest";
import { reportCoreCallFailure } from "./core-call-diagnostic";

afterEach(() => vi.restoreAllMocks());

it("logs only the route shape, failure kind and elapsed time", () => {
  const warn = vi.spyOn(console, "warn").mockImplementation(() => {});
  const timeout = new DOMException("The operation timed out.", "TimeoutError");
  reportCoreCallFailure("/api/companies/11111111-1111-4111-8111-111111111111/customer-requests?status=new&customer=Ann", timeout, performance.now());
  reportCoreCallFailure("/api/local/customer-requests/YC-0042", new TypeError("fetch failed"), performance.now());
  expect(warn.mock.calls.map(([line]) => String(line).replace(/\(\d+ ms\)/, "(n ms)"))).toEqual([
    "[aioffice-core-call] /api/companies/{id}/customer-requests answered 503 after timeout (n ms)",
    "[aioffice-core-call] /api/local/customer-requests/{code} answered 503 after network (n ms)",
  ]);
});
