// @vitest-environment node
import { afterEach, expect, it, vi } from "vitest";
import { authorizeDiscoveredCompany, discoverBrowserCompanies } from "./browser-company-preselection";
import { createCompanyDiscoveryTransaction, createOidcTransaction, openOidcTransaction, readBrowserOidcSettings, sealOidcTransaction } from "./browser-oidc";
import { createBrowserSessionCoordinator } from "./browser-session-store";

const company = "22222222-2222-2222-2222-222222222222", other = "33333333-3333-3333-3333-333333333333";
const env: Record<string, string> = {
  AIOFFICE_BROWSER_OIDC_ENABLED: "true", AIOFFICE_BROWSER_OIDC_PUBLIC_ORIGIN: "https://office.example.test",
  AIOFFICE_BROWSER_OIDC_ISSUER: "https://identity.example.test/realm", AIOFFICE_BROWSER_OIDC_CLIENT_ID: "browser-office",
  AIOFFICE_BROWSER_OIDC_AUTHORIZATION_ENDPOINT: "https://identity.example.test/auth",
  AIOFFICE_BROWSER_OIDC_TOKEN_ENDPOINT: "https://identity.example.test/token", AIOFFICE_BROWSER_OIDC_JWKS_URI: "https://identity.example.test/certs",
  AIOFFICE_BROWSER_OIDC_TRANSACTION_KEY: Buffer.alloc(32, 8).toString("base64url"),
  AIOFFICE_BROWSER_CORE_API_ORIGIN: "https://core.example.test",
};
const settings = readBrowserOidcSettings((key) => env[key])!, environment = (key: string) => env[key];
const tokens = () => ({ accessToken: "private-token", subject: "opaque-Case-Subject", expiresAt: Math.floor(Date.now() / 1000) + 600, expiresIn: 600 });
const choices = [{ companyId: company, companyName: "Công ty khác" }, { companyId: other, companyName: "Công ty thứ hai" }];
const handle = Buffer.alloc(32, 9).toString("base64url"), binding = `${handle}.${handle}`;
afterEach(() => { vi.useRealTimers(); vi.restoreAllMocks(); });

it("seals discovery with no company authority and retains explicit v1 switching", () => {
  const transaction = createCompanyDiscoveryTransaction();
  expect(transaction.version).toBe(2); expect(transaction.companyId).toBeNull();
  expect(openOidcTransaction(sealOidcTransaction(transaction, settings), settings)).toEqual(transaction);
  const legacy = createOidcTransaction(company);
  expect(openOidcTransaction(sealOidcTransaction(legacy, settings), settings)).toEqual(legacy);
  expect(openOidcTransaction(sealOidcTransaction({ ...transaction, companyId: company }, settings), settings)).toBeNull();
  expect(openOidcTransaction(sealOidcTransaction({ ...legacy, companyId: null }, settings), settings)).toBeNull();
});

it.each([[], [choices[0]], choices].map((items) => [items]))("uses the identity-only directory for zero, one and multiple choices", async (items) => {
  const fetcher = vi.fn(async () => Response.json({ items }));
  const actual = await discoverBrowserCompanies(settings, createCompanyDiscoveryTransaction(), tokens(), fetcher, environment);
  expect(actual).toEqual(items); expect(Object.isFrozen(actual)).toBe(true);
  const [url, init] = fetcher.mock.calls[0] as unknown as [string, RequestInit];
  expect(url).toBe("https://core.example.test/api/auth/companies");
  expect(init.headers).toEqual({ Authorization: "Bearer private-token", Accept: "application/json" });
  expect(init.cache).toBe("no-store"); expect(init.redirect).toBe("error");
});

it.each([{ items: [choices[0], choices[0]] }, { items: [{ ...choices[0], roles: ["admin"] }] },
  { items: [{ ...choices[0], companyName: "private\u0000name" }] }, { items: choices, tenantId: company }, { items: Array(101).fill(choices[0]) }])(
  "refuses ambiguous, overflowing or malformed directory bytes", async (payload) => {
    await expect(discoverBrowserCompanies(settings, createCompanyDiscoveryTransaction(), tokens(), async () => Response.json(payload), environment))
      .rejects.toThrow(/^Browser company access could not be verified\.$/);
  });
it.each([403, 500, 302])("does not disclose directory failure %s", async (status) => {
  await expect(discoverBrowserCompanies(settings, createCompanyDiscoveryTransaction(), tokens(), async () => new Response("private", { status }), environment))
    .rejects.toThrow(/^Browser company access could not be verified\.$/);
});
it("refuses expired token, selected-company transactions and oversized actual directory bytes", async () => {
  const fetcher = vi.fn(async () => Response.json({ items: choices }));
  await expect(discoverBrowserCompanies(settings, createOidcTransaction(company), tokens(), fetcher, environment)).rejects.toThrow();
  await expect(discoverBrowserCompanies(settings, createCompanyDiscoveryTransaction(), { ...tokens(), expiresAt: 1 }, fetcher, environment)).rejects.toThrow();
  expect(fetcher).not.toHaveBeenCalled();
  await expect(discoverBrowserCompanies(settings, createCompanyDiscoveryTransaction(), tokens(), async () =>
    new Response(" ".repeat(131_073), { headers: { "Content-Type": "application/json" } }), environment)).rejects.toThrow();
});
it("revalidates the selected company's current context rather than using directory names as authority", async () => {
  const fetcher = vi.fn(async () => Response.json({ tenantId: company, userId: other, companyId: other, roles: ["viewer"] }));
  const value = await authorizeDiscoveredCompany(settings, createCompanyDiscoveryTransaction(), tokens(), other, fetcher, environment);
  expect(value.session.companyId).toBe(other);
  expect((fetcher.mock.calls[0] as unknown as [string, RequestInit])[1].headers).toMatchObject({ "X-AIOffice-Company-Id": other });
  await expect(authorizeDiscoveredCompany(settings, createCompanyDiscoveryTransaction(), tokens(), company, fetcher, environment)).rejects.toThrow();
});
it("encrypts and purpose-binds private selection to the original transaction and generation", async () => {
  let ciphertext: unknown;
  const evaluate = vi.fn(async (script: string, _key: string, args: string[]) => {
    if (script.includes("'selectionPayload', ARGV[4]")) { ciphertext = args[3]; return 1; }
    if (script.includes("return {1, redis.call('HGET', key, 'payload')}")) return [1, ciphertext];
    return ciphertext;
  });
  const store = createBrowserSessionCoordinator(evaluate, settings), transaction = createCompanyDiscoveryTransaction();
  const selection = { ...tokens(), choices }; const { expiresIn: _unused, ...privateSelection } = selection;
  expect(await store.stageSelection(binding, transaction, handle, privateSelection)).toBe(true);
  for (const secret of [selection.accessToken, selection.subject, company, choices[0].companyName]) expect(ciphertext).not.toContain(secret);
  expect(await store.readSelection(binding, transaction)).toEqual(privateSelection);
  expect(await store.readSelection(binding, createCompanyDiscoveryTransaction())).toBeNull();
  expect(await store.readSelection(`${handle}.${Buffer.alloc(32, 10).toString("base64url")}`, transaction)).toBeNull();
  expect(await store.read(binding, handle)).toBeNull();
  const parts = String(ciphertext).split("."); const tag = Buffer.from(parts[3], "base64url"); tag[0] ^= 1; parts[3] = tag.toString("base64url"); ciphertext = parts.join(".");
  expect(await store.readSelection(binding, transaction)).toBeNull();
});
it("refuses legacy transactions or fewer than two retained choices before storing tokens", async () => {
  const evaluate = vi.fn(), store = createBrowserSessionCoordinator(evaluate, settings), value = tokens();
  for (const retained of [[], [choices[0]], [choices[0], choices[0]]]) {
    expect(await store.stageSelection(binding, createCompanyDiscoveryTransaction(), handle, { accessToken: value.accessToken,
      subject: value.subject, expiresAt: value.expiresAt, choices: retained })).toBe(false);
  }
  expect(await store.stageSelection(binding, createOidcTransaction(company), handle, { accessToken: value.accessToken,
    subject: value.subject, expiresAt: value.expiresAt, choices })).toBe(false);
  expect(evaluate).not.toHaveBeenCalled();
});
