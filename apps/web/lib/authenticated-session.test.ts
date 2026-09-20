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
      expect(request).toEqual({ selectedCompanyId: "company-a", headers: { [COMPANY_SELECTOR_HEADER]: "company-a" } });
      expect(request.headers).not.toHaveProperty("TenantId");
      expect(request.headers).not.toHaveProperty("UserId");
      expect(request.headers).not.toHaveProperty("X-AIOffice-Tenant-Id");
      expect(request.headers).not.toHaveProperty("X-AIOffice-User-Id");
      return { ok: true, membership: serverMembership };
    });
    await expect(bootstrapAuthenticatedSession(" company-a ", transport)).resolves.toEqual({ status: "ready", membership: serverMembership });
  });

  it.each([
    ["company mismatch", { ...serverMembership, companyId: "company-b" }],
    ["missing tenant", { ...serverMembership, tenantId: "   " }],
    ["missing company", { ...serverMembership, companyId: "" }],
    ["missing company name", { ...serverMembership, companyName: " " }],
    ["missing user", { ...serverMembership, userId: "" }],
    ["missing user name", { ...serverMembership, userName: "   " }],
    ["missing roles", { ...serverMembership, roles: undefined }],
    ["non-array roles", { ...serverMembership, roles: "admin" }],
    ["blank role", { ...serverMembership, roles: ["member", " "] }],
    ["non-string tenant", { ...serverMembership, tenantId: 42 }],
  ] as const)("fails closed for server membership with %s", async (_case, membership) => {
    const transport = (async () => ({ ok: true, membership })) as unknown as SessionBootstrapTransport;
    await expect(bootstrapAuthenticatedSession("company-a", transport)).resolves.toEqual({ status: "forbidden", reason: "invalid-response" });
  });

  it.each([
    ["null envelope", null],
    ["missing ok discriminator", { membership: serverMembership }],
    ["non-boolean ok discriminator", { ok: "true", membership: serverMembership }],
    ["success without membership", { ok: true }],
    ["failure without reason", { ok: false }],
    ["unknown failure reason", { ok: false, reason: "server-error" }],
    ["success carrying failure reason", { ok: true, membership: serverMembership, reason: "forbidden" }],
    ["failure carrying membership", { ok: false, reason: "forbidden", membership: serverMembership }],
    ["inherited success discriminator", Object.create({ ok: true, membership: serverMembership })],
    ["inherited success membership", Object.assign(Object.create({ membership: serverMembership }), { ok: true })],
    ["inherited failure reason", Object.assign(Object.create({ reason: "forbidden" }), { ok: false })],
  ] as const)("fails closed for %s", async (_case, envelope) => {
    const transport = (async () => envelope) as unknown as SessionBootstrapTransport;
    await expect(bootstrapAuthenticatedSession("company-a", transport)).resolves.toEqual({ status: "forbidden", reason: "invalid-response" });
  });

  it("fails closed when the session transport rejects", async () => {
    const transport: SessionBootstrapTransport = async () => { throw new Error("network unavailable"); };
    await expect(bootstrapAuthenticatedSession("company-a", transport)).resolves.toEqual({ status: "forbidden", reason: "invalid-response" });
  });

  it("never calls transport without a company selector", async () => {
    const transport = vi.fn<SessionBootstrapTransport>();
    await expect(bootstrapAuthenticatedSession("  ", transport)).resolves.toEqual({ status: "forbidden", reason: "invalid-response" });
    expect(transport).not.toHaveBeenCalled();
  });

  it("does not let local spoofed identity become transport authority", async () => {
    const localIdentity = { tenantId: "tenant-browser-spoof", userId: "user-browser-spoof", companyId: "company-a" };
    const transport = vi.fn<SessionBootstrapTransport>(async (request) => {
      expect(request).toEqual({ selectedCompanyId: localIdentity.companyId, headers: { [COMPANY_SELECTOR_HEADER]: localIdentity.companyId } });
      expect(JSON.stringify(request)).not.toContain(localIdentity.tenantId);
      expect(JSON.stringify(request)).not.toContain(localIdentity.userId);
      return { ok: true, membership: serverMembership };
    });
    await expect(bootstrapAuthenticatedSession(localIdentity.companyId, transport)).resolves.toEqual({ status: "ready", membership: serverMembership });
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
