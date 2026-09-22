import { describe, expect, it, vi } from "vitest";
import { bootstrapAuthenticatedSession, type SessionBootstrapTransport } from "./authenticated-session";

const invalidResponse = { status: "forbidden", reason: "invalid-response" } as const;

describe("bootstrapAuthenticatedSession hostile runtime boundary", () => {
  it("fails closed when a transport envelope throws during discriminator inspection", async () => {
    const envelope = new Proxy({}, { getOwnPropertyDescriptor() { throw new Error("hostile envelope"); } });
    const transport = (async () => envelope) as unknown as SessionBootstrapTransport;
    await expect(bootstrapAuthenticatedSession("company-a", transport)).resolves.toEqual(invalidResponse);
  });

  it("rejects accessor-backed envelope fields without executing transport getters", async () => {
    const okGetter = vi.fn(() => true);
    const membershipGetter = vi.fn(() => ({ tenantId: "tenant-a", companyId: "company-a", companyName: "Company A", userId: "user-a", userName: "User A", roles: ["member"] }));
    const envelope = {} as Record<string, unknown>;
    Object.defineProperty(envelope, "ok", { enumerable: true, get: okGetter });
    Object.defineProperty(envelope, "membership", { enumerable: true, get: membershipGetter });
    const transport = (async () => envelope) as unknown as SessionBootstrapTransport;
    await expect(bootstrapAuthenticatedSession("company-a", transport)).resolves.toEqual(invalidResponse);
    expect(okGetter).not.toHaveBeenCalled();
    expect(membershipGetter).not.toHaveBeenCalled();
  });

  it("rejects accessor-backed failure reasons without executing transport getters", async () => {
    const reasonGetter = vi.fn(() => "forbidden");
    const envelope = { ok: false } as Record<string, unknown>;
    Object.defineProperty(envelope, "reason", { enumerable: true, get: reasonGetter });
    const transport = (async () => envelope) as unknown as SessionBootstrapTransport;

    await expect(bootstrapAuthenticatedSession("company-a", transport)).resolves.toEqual(invalidResponse);
    expect(reasonGetter).not.toHaveBeenCalled();
  });

  it("fails closed when authoritative membership throws during validation", async () => {
    const membership = new Proxy({}, { getOwnPropertyDescriptor(_target, property) { if (property === "tenantId") throw new Error("hostile membership"); return undefined; } });
    const transport = (async () => ({ ok: true, membership })) as unknown as SessionBootstrapTransport;
    await expect(bootstrapAuthenticatedSession("company-a", transport)).resolves.toEqual(invalidResponse);
  });

  it("snapshots role data without executing a transport-controlled iterator", async () => {
    const iteratorGetter = vi.fn(() => function* () { yield "spoofed"; });
    const roles = ["member"];
    Object.defineProperty(roles, Symbol.iterator, { configurable: true, get: iteratorGetter });
    const membership = { tenantId: "tenant-a", companyId: "company-a", companyName: "Company A", userId: "user-a", userName: "User A", roles };
    const transport = (async () => ({ ok: true, membership })) as unknown as SessionBootstrapTransport;

    await expect(bootstrapAuthenticatedSession("company-a", transport)).resolves.toEqual({ status: "ready", membership: { ...membership, roles: ["member"] } });
    expect(iteratorGetter).not.toHaveBeenCalled();
  });

  it("rejects accessor-backed role entries without executing transport getters", async () => {
    const roleGetter = vi.fn(() => "admin");
    const roles = ["member"];
    Object.defineProperty(roles, "0", { configurable: true, enumerable: true, get: roleGetter });
    const membership = { tenantId: "tenant-a", companyId: "company-a", companyName: "Company A", userId: "user-a", userName: "User A", roles };
    const transport = (async () => ({ ok: true, membership })) as unknown as SessionBootstrapTransport;

    await expect(bootstrapAuthenticatedSession("company-a", transport)).resolves.toEqual(invalidResponse);
    expect(roleGetter).not.toHaveBeenCalled();
  });
});
