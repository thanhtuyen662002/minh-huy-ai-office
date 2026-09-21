import { describe, expect, it, vi } from "vitest";
import { fetchAuthoritativeAuthContext } from "./auth-context-http";

describe("fetchAuthoritativeAuthContext hostile runtime payloads", () => {
  it("fails closed when authoritative response inspection throws", async () => {
    const hostileResponse = new Proxy({}, {
      get(_target, property) {
        if (property === "status") throw new Error("hostile auth response");
        return undefined;
      },
    }) as Response;
    const fetcher = vi.fn<typeof fetch>(async () => hostileResponse);

    await expect(fetchAuthoritativeAuthContext("company-a", fetcher)).resolves.toEqual({
      ok: false,
      reason: "invalid-response",
    });
  });

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

  it("rejects accessor-backed authoritative identity without executing the getter", async () => {
    const tenantGetter = vi.fn(() => "tenant-server");
    const payload = {
      companyId: "company-a",
      userId: "user-server",
      roles: ["member"],
    };
    Object.defineProperty(payload, "tenantId", { enumerable: true, get: tenantGetter });
    const fetcher = vi.fn<typeof fetch>(async () => ({ ok: true, status: 200, json: async () => payload }) as Response);

    await expect(fetchAuthoritativeAuthContext("company-a", fetcher)).resolves.toEqual({ ok: false, reason: "invalid-response" });
    expect(tenantGetter).not.toHaveBeenCalled();
  });

  it("rejects hostile role iteration without executing the iterator", async () => {
    const iterator = vi.fn(function* () { yield "admin"; });
    const roles = ["member"];
    Object.defineProperty(roles, Symbol.iterator, { value: iterator });
    const fetcher = vi.fn<typeof fetch>(async () => ({
      ok: true,
      status: 200,
      json: async () => ({ tenantId: "tenant-server", companyId: "company-a", userId: "user-server", roles }),
    }) as Response);

    await expect(fetchAuthoritativeAuthContext("company-a", fetcher)).resolves.toEqual({
      ok: true,
      context: { tenantId: "tenant-server", companyId: "company-a", userId: "user-server", roles: ["member"] },
    });
    expect(iterator).not.toHaveBeenCalled();
  });

  it("returns an immutable authoritative snapshot detached from the transport payload", async () => {
    const roles = ["member"];
    const payload = { tenantId: "tenant-server", companyId: "company-a", userId: "user-server", roles };
    const fetcher = vi.fn<typeof fetch>(async () => ({ ok: true, status: 200, json: async () => payload }) as Response);

    const result = await fetchAuthoritativeAuthContext("company-a", fetcher);
    expect(result.ok).toBe(true);
    if (!result.ok) throw new Error("expected authoritative context");

    expect(Object.isFrozen(result.context)).toBe(true);
    expect(Object.isFrozen(result.context.roles)).toBe(true);
    payload.companyId = "company-b";
    roles[0] = "admin";
    expect(result.context).toEqual({ tenantId: "tenant-server", companyId: "company-a", userId: "user-server", roles: ["member"] });
  });

  it("fails closed when authoritative role inspection throws", async () => {
    const hostileRoles = new Proxy(["member"], {
      getOwnPropertyDescriptor(target, property) {
        if (property === "0") throw new Error("hostile role payload");
        return Reflect.getOwnPropertyDescriptor(target, property);
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
