import { describe, expect, it, vi } from "vitest";
import {
  bootstrapAuthenticatedSession,
  COMPANY_SELECTOR_HEADER,
  type SessionBootstrapTransport,
} from "./authenticated-session";

const serverMembership = {
  tenantId: "tenant-server",
  companyId: "company-a",
  companyName: "Company A",
  userId: "user-server",
  userName: "Server User",
  roles: ["member"],
} as const;

describe("bootstrapAuthenticatedSession", () => {
  it("treats company selection as an untrusted selector and accepts server-derived identity", async () => {
    const transport = vi.fn<SessionBootstrapTransport>(async (request) => {
      expect(request).toEqual({
        selectedCompanyId: "company-a",
        headers: { [COMPANY_SELECTOR_HEADER]: "company-a" },
      });
      expect(request.headers).not.toHaveProperty("TenantId");
      expect(request.headers).not.toHaveProperty("UserId");
      return { ok: true, membership: serverMembership };
    });

    await expect(bootstrapAuthenticatedSession(" company-a ", transport)).resolves.toEqual({
      status: "ready",
      membership: serverMembership,
    });
  });

  it("fails closed when the server response does not match the selected company", async () => {
    const transport: SessionBootstrapTransport = async () => ({
      ok: true,
      membership: { ...serverMembership, companyId: "company-b" },
    });

    await expect(bootstrapAuthenticatedSession("company-a", transport)).resolves.toEqual({
      status: "forbidden",
      reason: "invalid-response",
    });
  });

  it("fails closed when the session transport rejects", async () => {
    const transport: SessionBootstrapTransport = async () => {
      throw new Error("network unavailable");
    };

    await expect(bootstrapAuthenticatedSession("company-a", transport)).resolves.toEqual({
      status: "forbidden",
      reason: "invalid-response",
    });
  });

  it("never calls transport without a company selector", async () => {
    const transport = vi.fn<SessionBootstrapTransport>();

    await expect(bootstrapAuthenticatedSession("  ", transport)).resolves.toEqual({
      status: "forbidden",
      reason: "invalid-response",
    });
    expect(transport).not.toHaveBeenCalled();
  });

  it.each([
    ["unauthenticated", { status: "unauthenticated" }],
    ["forbidden", { status: "forbidden", reason: "forbidden" }],
    ["inactive-membership", { status: "forbidden", reason: "inactive-membership" }],
    ["invalid-response", { status: "forbidden", reason: "invalid-response" }],
  ] as const)("maps %s without inventing local authority", async (reason, expected) => {
    const transport: SessionBootstrapTransport = async () => ({ ok: false, reason });
    await expect(bootstrapAuthenticatedSession("company-a", transport)).resolves.toEqual(expected);
  });
});
