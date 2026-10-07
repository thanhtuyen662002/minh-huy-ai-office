// @vitest-environment node
import { afterEach, expect, it, vi } from "vitest";
import { createOidcTransaction, readBrowserOidcSettings } from "./browser-oidc";
import { authorizeBrowserSession, readBrowserCoreApiOrigin } from "./browser-oidc-authority";

const companyId = "22222222-2222-2222-2222-222222222222";
const env: Record<string, string> = {
  AIOFFICE_BROWSER_OIDC_ENABLED: "true", AIOFFICE_BROWSER_OIDC_PUBLIC_ORIGIN: "https://office.example.test",
  AIOFFICE_BROWSER_OIDC_ISSUER: "https://identity.example.test/realm", AIOFFICE_BROWSER_OIDC_CLIENT_ID: "browser-office",
  AIOFFICE_BROWSER_OIDC_AUTHORIZATION_ENDPOINT: "https://identity.example.test/auth",
  AIOFFICE_BROWSER_OIDC_TOKEN_ENDPOINT: "https://identity.example.test/token", AIOFFICE_BROWSER_OIDC_JWKS_URI: "https://identity.example.test/certs",
  AIOFFICE_BROWSER_OIDC_TRANSACTION_KEY: Buffer.alloc(32, 9).toString("base64url"),
};
const settings = readBrowserOidcSettings((name) => env[name])!;
const context = { tenantId: "11111111-1111-1111-1111-111111111111", companyId,
  userId: "33333333-3333-3333-3333-333333333333", roles: ["Workspace member"] };
const tokens = () => ({ accessToken: "private-verified-token", subject: "opaque-exact-subject", expiresIn: 300, expiresAt: Math.floor(Date.now() / 1000) + 300 });
const coreOrigin = () => "https://core.example.test";
afterEach(() => vi.restoreAllMocks());

it.each([undefined, "", "http://core.example.test", "https://core.example.test/", "https://core.example.test/path",
  "https://private:password@core.example.test", "https://core.example.test?", "https://core.example.test#", " https://core.example.test",
])("refuses unpinned or unsafe Core origins generically", (value) => {
  expect(() => readBrowserCoreApiOrigin(settings, () => value)).toThrow(/^Browser company access could not be verified\.$/);
});
it.each(["http://127.0.0.1:8080", "http://localhost:18080", "http://[::1]:8080", "http://core-api:8080"])("accepts only explicit local Core backchannels", (value) => {
  expect(readBrowserCoreApiOrigin({ ...settings, localHttp: true }, () => value)).toBe(value);
});
it.each(["http://foreign.invalid:8080", "http://core-api:9999", "http://identity:8080"])("rejects foreign local Core routing", (value) => {
  expect(() => readBrowserCoreApiOrigin({ ...settings, localHttp: true }, () => value)).toThrow(/^Browser company access could not be verified\.$/);
});
it("accepts authority only from fresh scoped Core and retains the signed absolute expiry", async () => {
  const grant = tokens(), transaction = createOidcTransaction(companyId);
  const fetcher = vi.fn(async () => Response.json(context));
  const result = await authorizeBrowserSession(settings, transaction, grant, fetcher, coreOrigin);
  expect(fetcher).toHaveBeenCalledWith("https://core.example.test/api/auth/context", expect.objectContaining({
    method: "GET", cache: "no-store", redirect: "error", signal: expect.any(AbortSignal),
    headers: { Authorization: `Bearer ${grant.accessToken}`, "X-AIOffice-Company-Id": companyId, Accept: "application/json" },
  }));
  expect(result.context).toEqual(context);
  expect(result.session).toEqual({ companyId, accessToken: grant.accessToken, subject: grant.subject, expiresAt: grant.expiresAt });
  expect(Object.isFrozen(result)).toBe(true); expect(Object.isFrozen(result.context.roles)).toBe(true);
});
it.each([401, 403, 500, 503])("refuses Core denial/unavailability without exposing its body", async (status) => {
  const fetcher = vi.fn(async () => Response.json({ token: "private-provider-details" }, { status }));
  await expect(authorizeBrowserSession(settings, createOidcTransaction(companyId), tokens(), fetcher, coreOrigin)).rejects.toThrow(/^Browser company access could not be verified\.$/);
});
it.each([{ ...context, companyId: "44444444-4444-4444-4444-444444444444" }, { ...context, tenantId: "caller-tenant" },
  { ...context, userId: "" }, { ...context, roles: ["Workspace member", "Workspace member"] }, { ...context, roles: [" fake-admin "] },
  { ...context, roles: ["admin\r\nheader"] }, { ...context, accessToken: "private-leak" }, null, [],
])("rejects invalid Core scope/authority shape", async (value) => {
  await expect(authorizeBrowserSession(settings, createOidcTransaction(companyId), tokens(), async () => Response.json(value), coreOrigin)).rejects.toThrow(/^Browser company access could not be verified\.$/);
});
it("compares GUID selector case by GUID value while retaining opaque subject spelling", async () => {
  const selected = "abcdefab-abcd-abcd-abcd-abcdefabcdef";
  const result = await authorizeBrowserSession(settings, createOidcTransaction(selected.toUpperCase()), tokens(),
    async () => Response.json({ ...context, companyId: selected }), coreOrigin);
  expect(result.session.companyId).toBe(selected.toUpperCase());
  expect(result.session.subject).toBe("opaque-exact-subject");
});
it.each([32, 33, 256])("accepts all%d unique roles supported by the Core/member contract", async (count) => {
  const roles = Array.from({ length: count }, (_, index) => `Existing role ${index}`);
  const result = await authorizeBrowserSession(settings, createOidcTransaction(companyId), tokens(),
    async () => Response.json({ ...context, roles }), coreOrigin);
  expect(result.context.roles).toEqual(roles);
});
it("rejects more than256 roles before accepting Core authority", async () => {
  await expect(authorizeBrowserSession(settings, createOidcTransaction(companyId), tokens(),
    async () => Response.json({ ...context, roles: Array.from({ length: 257 }, (_, index) => `Role ${index}`) }), coreOrigin))
    .rejects.toThrow(/^Browser company access could not be verified\.$/);
});
it("refuses expiry reached during Core verification instead of extending relative lifetime", async () => {
  const start = Date.now(), grant = tokens(), transaction = createOidcTransaction(companyId);
  const fetcher = vi.fn(async () => { vi.spyOn(Date, "now").mockReturnValue(start + 301_000); return Response.json(context); });
  await expect(authorizeBrowserSession(settings, transaction, grant, fetcher, coreOrigin)).rejects.toThrow(/^Browser company access could not be verified\.$/);
});
it("checks expiry before any Core request", async () => {
  const fetcher = vi.fn();
  await expect(authorizeBrowserSession(settings, createOidcTransaction(companyId), { ...tokens(), expiresAt: 1 }, fetcher, coreOrigin)).rejects.toThrow(/^Browser company access could not be verified\.$/);
  expect(fetcher).not.toHaveBeenCalled();
});
it("bounds actual Core body bytes despite dishonest Content-Length", async () => {
  const fetcher = vi.fn(async () => new Response(" ".repeat(65_537), { headers: { "Content-Type": "application/json", "Content-Length": "1" } }));
  await expect(authorizeBrowserSession(settings, createOidcTransaction(companyId), tokens(), fetcher, coreOrigin)).rejects.toThrow(/^Browser company access could not be verified\.$/);
});
it.each(["text/html", "application/octet-stream"])("refuses incompatible media types before accepting authority", async (type) => {
  await expect(authorizeBrowserSession(settings, createOidcTransaction(companyId), tokens(),
    async () => new Response(JSON.stringify(context), { headers: { "Content-Type": type } }), coreOrigin)).rejects.toThrow(/^Browser company access could not be verified\.$/);
});
it("refuses malformed UTF8 and generic transport errors", async () => {
  await expect(authorizeBrowserSession(settings, createOidcTransaction(companyId), tokens(),
    async () => new Response(new Uint8Array([0xff]), { headers: { "Content-Type": "application/json" } }), coreOrigin)).rejects.toThrow(/^Browser company access could not be verified\.$/);
  await expect(authorizeBrowserSession(settings, createOidcTransaction(companyId), tokens(),
    async () => { throw new Error("private-provider/token/endpoint"); }, coreOrigin)).rejects.toThrow(/^Browser company access could not be verified\.$/);
});
