import { describe, expect, it } from "vitest";
import { bootstrapAuthenticatedSession, type SessionBootstrapTransport } from "./authenticated-session";

const membership = {
  tenantId: "tenant-server",
  companyId: "company-a",
  companyName: "Company A",
  userId: "user-server",
  userName: "Server User",
  roles: ["member"],
};

describe("authenticated session membership snapshot", () => {
  it("returns a plain snapshot that cannot be changed by later transport-object mutation", async () => {
    const transport = (async () => ({ ok: true, membership })) as SessionBootstrapTransport;
    const state = await bootstrapAuthenticatedSession("company-a", transport);

    membership.companyName = "Browser-mutated label";
    membership.roles[0] = "spoofed-role";

    expect(state).toEqual({
      status: "ready",
      membership: {
        tenantId: "tenant-server",
        companyId: "company-a",
        companyName: "Company A",
        userId: "user-server",
        userName: "Server User",
        roles: ["member"],
      },
    });
  });

  it("freezes the accepted authority snapshot so downstream runtime mutation cannot rewrite identity", async () => {
    const transport = (async () => ({ ok: true, membership })) as SessionBootstrapTransport;
    const state = await bootstrapAuthenticatedSession("company-a", transport);
    expect(state.status).toBe("ready");
    if (state.status !== "ready") return;

    expect(Object.isFrozen(state.membership)).toBe(true);
    expect(Object.isFrozen(state.membership.roles)).toBe(true);
    expect(() => {
      (state.membership as { companyId: string }).companyId = "browser-spoofed-company";
    }).toThrow();
    expect(() => {
      (state.membership.roles as string[])[0] = "browser-spoofed-role";
    }).toThrow();
    expect(state.membership.companyId).toBe("company-a");
    expect(state.membership.roles).toEqual(["member"]);
  });

  it("rejects accessor-backed authoritative fields without invoking their getters", async () => {
    let getterCalls = 0;
    const accessorMembership = { ...membership } as Record<string, unknown>;
    Object.defineProperty(accessorMembership, "companyName", {
      enumerable: true,
      get() {
        getterCalls += 1;
        return "Accessor Company";
      },
    });
    const transport = (async () => ({ ok: true, membership: accessorMembership })) as unknown as SessionBootstrapTransport;

    await expect(bootstrapAuthenticatedSession("company-a", transport)).resolves.toEqual({
      status: "forbidden",
      reason: "invalid-response",
    });
    expect(getterCalls).toBe(0);
  });
});
