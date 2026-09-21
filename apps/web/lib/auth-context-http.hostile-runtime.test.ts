import { describe, expect, it, vi } from "vitest";
import { fetchAuthoritativeAuthContext } from "./auth-context-http";

describe("fetchAuthoritativeAuthContext hostile runtime payloads", () => {
  it("fails closed when authoritative payload inspection throws", async () => {
    const hostilePayload = new Proxy({}, {
      getOwnPropertyDescriptor() {
        throw new Error("hostile auth payload");
      },
    });
    const fetcher = vi.fn<typeof fetch>(async () => ({
      ok: true,
      status: 200,
      json: async () => hostilePayload,
    }) as Response);

    await expect(fetchAuthoritativeAuthContext("company-a", fetcher)).resolves.toEqual({
      ok: false,
      reason: "invalid-response",
    });
  });

  it("fails closed when authoritative role inspection throws", async () => {
    const hostileRoles = new Proxy(["member"], {
      get(target, property, receiver) {
        if (property === "0") throw new Error("hostile role payload");
        return Reflect.get(target, property, receiver);
      },
    });
    const fetcher = vi.fn<typeof fetch>(async () => ({
      ok: true,
      status: 200,
      json: async () => ({
        tenantId: "tenant-server",
        companyId: "company-a",
        userId: "user-server",
        roles: hostileRoles,
      }),
    }) as Response);

    await expect(fetchAuthoritativeAuthContext("company-a", fetcher)).resolves.toEqual({
      ok: false,
      reason: "invalid-response",
    });
  });
});
