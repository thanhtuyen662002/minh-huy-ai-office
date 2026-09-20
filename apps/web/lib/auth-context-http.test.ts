import { describe, expect, it, vi } from "vitest";
import { COMPANY_SELECTOR_HEADER } from "./authenticated-session";
import { fetchAuthoritativeAuthContext } from "./auth-context-http";

const jsonResponse = (body: unknown, status = 200) => new Response(JSON.stringify(body), {
  status,
  headers: { "content-type": "application/json" },
});

describe("fetchAuthoritativeAuthContext", () => {
  it("sends only the untrusted company selector and accepts server-derived identity", async () => {
    const fetcher = vi.fn<typeof fetch>(async (_input, init) => {
      expect(init?.headers).toEqual({ [COMPANY_SELECTOR_HEADER]: "company-a" });
      expect(init).not.toHaveProperty("tenantId");
      expect(init).not.toHaveProperty("userId");
      return jsonResponse({ tenantId: "tenant-server", companyId: "company-a", userId: "user-server", roles: ["member"] });
    });

    await expect(fetchAuthoritativeAuthContext(" company-a ", fetcher)).resolves.toEqual({
      ok: true,
      context: { tenantId: "tenant-server", companyId: "company-a", userId: "user-server", roles: ["member"] },
    });
  });

  it("rejects an empty company selector before any authenticated transport is attempted", async () => {
    const fetcher = vi.fn<typeof fetch>();
    await expect(fetchAuthoritativeAuthContext("   ", fetcher)).resolves.toEqual({ ok: false, reason: "invalid-response" });
    expect(fetcher).not.toHaveBeenCalled();
  });

  it("allowlists authoritative identity fields and never promotes unexpected secret or presentation metadata", async () => {
    const fetcher = vi.fn<typeof fetch>(async () => jsonResponse({
      tenantId: "tenant-server",
      companyId: "company-a",
      userId: "user-server",
      roles: ["member"],
      companyName: "browser-visible-name",
      userName: "browser-visible-user",
      secretReference: "vault://company-a/erp",
      connectionString: "Server=secret-host;Password=do-not-surface",
    }));

    const result = await fetchAuthoritativeAuthContext("company-a", fetcher);
    expect(result).toEqual({
      ok: true,
      context: { tenantId: "tenant-server", companyId: "company-a", userId: "user-server", roles: ["member"] },
    });
    if (result.ok) {
      expect(result.context).not.toHaveProperty("companyName");
      expect(result.context).not.toHaveProperty("userName");
      expect(result.context).not.toHaveProperty("secretReference");
      expect(result.context).not.toHaveProperty("connectionString");
    }
  });

  it.each([
    [401, "unauthenticated"],
    [403, "forbidden"],
    [503, "invalid-response"],
  ] as const)("maps HTTP %s fail closed", async (status, reason) => {
    const fetcher = vi.fn<typeof fetch>(async () => jsonResponse({}, status));
    await expect(fetchAuthoritativeAuthContext("company-a", fetcher)).resolves.toEqual({ ok: false, reason });
  });

  it("rejects a server context for a different company", async () => {
    const fetcher = vi.fn<typeof fetch>(async () => jsonResponse({ tenantId: "tenant-server", companyId: "company-b", userId: "user-server", roles: [] }));
    await expect(fetchAuthoritativeAuthContext("company-a", fetcher)).resolves.toEqual({ ok: false, reason: "invalid-response" });
  });

  it.each([
    null,
    {},
    { tenantId: "tenant-server", companyId: "company-a", userId: "user-server" },
    { tenantId: "tenant-server", companyId: "company-a", userId: "user-server", roles: [""] },
    { tenantId: 7, companyId: "company-a", userId: "user-server", roles: [] },
    { tenantId: "tenant-server", companyId: 7, userId: "user-server", roles: [] },
    { tenantId: "tenant-server", companyId: "company-a", userId: 7, roles: [] },
    { tenantId: "tenant-server", companyId: "company-a", userId: "user-server", roles: "member" },
  ])("rejects malformed authoritative payloads", async (payload) => {
    const fetcher = vi.fn<typeof fetch>(async () => jsonResponse(payload));
    await expect(fetchAuthoritativeAuthContext("company-a", fetcher)).resolves.toEqual({ ok: false, reason: "invalid-response" });
  });

  it("rejects sparse authoritative role arrays instead of letting array holes bypass validation", async () => {
    const sparseRoles = new Array<string>(1);
    const fetcher = vi.fn<typeof fetch>(async () => ({
      ok: true,
      status: 200,
      json: async () => ({ tenantId: "tenant-server", companyId: "company-a", userId: "user-server", roles: sparseRoles }),
    }) as Response);

    await expect(fetchAuthoritativeAuthContext("company-a", fetcher)).resolves.toEqual({ ok: false, reason: "invalid-response" });
  });

  it("rejects inherited identity fields", async () => {
    const payload = Object.create({ tenantId: "tenant-server", companyId: "company-a", userId: "user-server", roles: ["member"] });
    Object.assign(payload, { companyId: "company-a", userId: "user-server", roles: ["member"] });
    const fetcher = vi.fn<typeof fetch>(async () => jsonResponse(payload));
    await expect(fetchAuthoritativeAuthContext("company-a", fetcher)).resolves.toEqual({ ok: false, reason: "invalid-response" });
  });

  it("fails closed on transport and JSON failures", async () => {
    const rejected = vi.fn<typeof fetch>(async () => { throw new Error("network"); });
    await expect(fetchAuthoritativeAuthContext("company-a", rejected)).resolves.toEqual({ ok: false, reason: "invalid-response" });

    const malformed = vi.fn<typeof fetch>(async () => new Response("not-json", { status: 200 }));
    await expect(fetchAuthoritativeAuthContext("company-a", malformed)).resolves.toEqual({ ok: false, reason: "invalid-response" });
  });
});
