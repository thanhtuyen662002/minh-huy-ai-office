import { describe, expect, it } from "vitest";
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

  it("fails closed when authoritative membership throws during validation", async () => {
    const membership = new Proxy(
      {},
      {
        get(_target, property) {
          if (property === "tenantId") throw new Error("hostile membership");
          return undefined;
        },
      },
    );
    const transport = (async () => ({ ok: true, membership })) as unknown as SessionBootstrapTransport;

    await expect(bootstrapAuthenticatedSession("company-a", transport)).resolves.toEqual(invalidResponse);
  });
});
