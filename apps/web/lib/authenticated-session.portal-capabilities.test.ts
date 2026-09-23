import { describe, expect, it } from "vitest";
import { bootstrapAuthenticatedSession, type SessionBootstrapTransport } from "./authenticated-session";

const membership = {
  tenantId: "tenant-server",
  companyId: "company-a",
  companyName: "Company A",
  userId: "user-server",
  userName: "Server User",
  roles: ["Customer"],
} as const;

describe("portal capability bootstrap", () => {
  it("snapshots allowlisted capabilities only from the server bootstrap result", async () => {
    const transport: SessionBootstrapTransport = async () => ({
      ok: true,
      membership,
      portalCapabilities: ["customer-chat", "billing"],
    });

    await expect(bootstrapAuthenticatedSession("company-a", transport)).resolves.toEqual({
      status: "ready",
      membership,
      portalCapabilities: ["customer-chat", "billing"],
    });
  });

  it("rejects unknown server capability metadata instead of partially widening navigation", async () => {
    const transport = (async () => ({
      ok: true,
      membership,
      portalCapabilities: ["customer-chat", "browser-admin"],
    })) as unknown as SessionBootstrapTransport;

    await expect(bootstrapAuthenticatedSession("company-a", transport)).resolves.toEqual({
      status: "forbidden",
      reason: "invalid-response",
    });
  });

  it("keeps legacy server responses valid but grants no portal capabilities by default", async () => {
    const transport: SessionBootstrapTransport = async () => ({ ok: true, membership });
    await expect(bootstrapAuthenticatedSession("company-a", transport)).resolves.toEqual({ status: "ready", membership });
  });
});
