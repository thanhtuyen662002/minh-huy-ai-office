import { describe, expect, it, vi } from "vitest";
import { bootstrapAuthenticatedSession, type SessionBootstrapTransport } from "./authenticated-session";

const invalidResponse = { status: "forbidden", reason: "invalid-response" } as const;

describe("bootstrapAuthenticatedSession hostile runtime boundary", () => {
  it("fails closed when a transport envelope throws during discriminator inspection", async () => {
    const envelope = new Proxy(
      {},
      {
        getOwnPropertyDescriptor() {
          throw new Error("hostile envelope");
        },
      },
    );
    const transport = (async () => envelope) as unknown as SessionBootstrapTransport;

    await expect(bootstrapAuthenticatedSession("company-a", transport)).resolves.toEqual(invalidResponse);
  });

  it("rejects accessor-backed envelope fields without executing transport getters", async () => {
    const okGetter = vi.fn(() => true);
    const membershipGetter = vi.fn(() => ({
      tenantId: "tenant-a",
      companyId: "company-a",
      companyName: "Company A",
      userId: "user-a",
      userName: "User A",
      roles: ["member"],
    }));
    const envelope = {} as Record<string, unknown>;
    Object.defineProperty(envelope, "ok", { enumerable: true, get: okGetter });
    Object.defineProperty(envelope, "membership", { enumerable: true, get: membershipGetter });
    const transport = (async () => envelope) as unknown as SessionBootstrapTransport;

    await expect(bootstrapAuthenticatedSession("company-a", transport)).resolves.toEqual(invalidResponse);
    expect(okGetter).not.toHaveBeenCalled();
    expect(membershipGetter).not.toHaveBeenCalled();
  });

  it("fails closed when authoritative membership throws during validation", async () => {
    const membership = new Proxy(
      {},
      {
        getOwnPropertyDescriptor(_target, property) {
          if (property === "tenantId") throw new Error("hostile membership");
          return undefined;
        },
      },
    );
    const transport = (async () => ({ ok: true, membership })) as unknown as SessionBootstrapTransport;

    await expect(bootstrapAuthenticatedSession("company-a", transport)).resolves.toEqual(invalidResponse);
  });
});
