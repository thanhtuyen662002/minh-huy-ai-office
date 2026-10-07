// @vitest-environment node
import { expect, it } from "vitest";
import { createOidcTransaction, OIDC_CALLBACK_PATH, oidcStartIsSameOrigin, oidcTransactionCookieName,
  openOidcTransaction, readBrowserOidcSettings, sealOidcTransaction } from "./browser-oidc";

const company = "22222222-2222-2222-2222-222222222222";
const values: Record<string, string> = {
  AIOFFICE_BROWSER_OIDC_ENABLED: "true",
  AIOFFICE_BROWSER_OIDC_PUBLIC_ORIGIN: "https://office.example.test",
  AIOFFICE_BROWSER_OIDC_ISSUER: "https://login.example.test/realms/office",
  AIOFFICE_BROWSER_OIDC_AUTHORIZATION_ENDPOINT: "https://login.example.test/authorize",
  AIOFFICE_BROWSER_OIDC_TOKEN_ENDPOINT: "https://login.example.test/token",
  AIOFFICE_BROWSER_OIDC_JWKS_URI: "https://login.example.test/jwks",
  AIOFFICE_BROWSER_OIDC_CLIENT_ID: "office-browser",
  AIOFFICE_BROWSER_OIDC_TRANSACTION_KEY: Buffer.alloc(32, 11).toString("base64url"),
};
const settings = (overrides: Record<string, string | undefined> = {}) => readBrowserOidcSettings((key) =>
  Object.hasOwn(overrides, key) ? overrides[key] : values[key])!;

it("is disabled by default without requiring or returning secrets", () => {
  expect(readBrowserOidcSettings(() => undefined)).toBeNull();
  expect(settings({ AIOFFICE_BROWSER_OIDC_ENABLED: "false" })).toBeNull();
});

it("pins the callback and production cookie to trusted configuration", () => {
  const config = settings();
  expect(config.redirectUri).toBe(values.AIOFFICE_BROWSER_OIDC_PUBLIC_ORIGIN + OIDC_CALLBACK_PATH);
  expect(config.clientSecret).toBeNull();
  expect(oidcTransactionCookieName(config)).toBe("__Host-aioffice_oidc_transaction");
  expect(Object.isFrozen(config)).toBe(true);
});

it.each([
  ["AIOFFICE_BROWSER_OIDC_PUBLIC_ORIGIN", "http://office.example.test"],
  ["AIOFFICE_BROWSER_OIDC_PUBLIC_ORIGIN", "https://office.example.test/"],
  ["AIOFFICE_BROWSER_OIDC_PUBLIC_ORIGIN", "https://office.example.test/path"],
  ["AIOFFICE_BROWSER_OIDC_PUBLIC_ORIGIN", "https://user@office.example.test"],
  ["AIOFFICE_BROWSER_OIDC_PUBLIC_ORIGIN", " https://office.example.test"],
  ["AIOFFICE_BROWSER_OIDC_ISSUER", "http://login.example.test/realms/office"],
  ["AIOFFICE_BROWSER_OIDC_ISSUER", "https://login.example.test/realms/office?query=1"],
  ["AIOFFICE_BROWSER_OIDC_ISSUER", "https://login.example.test/realms/office#fragment"],
  ["AIOFFICE_BROWSER_OIDC_ISSUER", "https://login.example.test/realms/office#"],
  ["AIOFFICE_BROWSER_OIDC_ISSUER", "https://login.example.test/realms/office?"],
  ["AIOFFICE_BROWSER_OIDC_AUTHORIZATION_ENDPOINT", "https://user:password@login.example.test/auth"],
  ["AIOFFICE_BROWSER_OIDC_AUTHORIZATION_ENDPOINT", "https://login.example.test/a/../authorize"],
  ["AIOFFICE_BROWSER_OIDC_TOKEN_ENDPOINT", "http://identity:8080/token"],
  ["AIOFFICE_BROWSER_OIDC_JWKS_URI", "file:///private/keys"],
  ["AIOFFICE_BROWSER_OIDC_CLIENT_ID", "bad\nclient"],
  ["AIOFFICE_BROWSER_OIDC_CLIENT_ID", "x".repeat(101)],
  ["AIOFFICE_BROWSER_OIDC_TRANSACTION_KEY", "short"],
  ["AIOFFICE_BROWSER_OIDC_TRANSACTION_KEY", Buffer.alloc(31).toString("base64url")],
  ["AIOFFICE_BROWSER_OIDC_TRANSACTION_KEY", Buffer.alloc(32).toString("base64")],
  ["AIOFFICE_BROWSER_OIDC_CLIENT_SECRET", "private-secret\n"],
])("fails closed without reflecting invalid %s", (name, value) => {
  expect(() => settings({ [name]: value })).toThrow("Browser identity configuration is invalid.");
  try { settings({ [name]: value }); } catch (error) { expect(String(error)).not.toContain(value); }
});

it.each(Object.keys(values).filter((key) => key !== "AIOFFICE_BROWSER_OIDC_ENABLED"))("requires configured %s", (key) => {
  expect(() => settings({ [key]: undefined })).toThrow("Browser identity configuration is invalid.");
});

const local = {
  AIOFFICE_BROWSER_OIDC_LOCAL_HTTP: "true",
  AIOFFICE_BROWSER_OIDC_PUBLIC_ORIGIN: "http://127.0.0.1:3000",
  AIOFFICE_BROWSER_OIDC_ISSUER: "http://127.0.0.1:8081/realms/aioffice-local",
  AIOFFICE_BROWSER_OIDC_AUTHORIZATION_ENDPOINT: "http://127.0.0.1:8081/realms/aioffice-local/protocol/openid-connect/auth",
  AIOFFICE_BROWSER_OIDC_TOKEN_ENDPOINT: "http://identity:8080/realms/aioffice-local/protocol/openid-connect/token",
  AIOFFICE_BROWSER_OIDC_JWKS_URI: "http://identity:8080/realms/aioffice-local/protocol/openid-connect/certs",
};
it("allows explicit loopback browser HTTP and fixed local backchannel", () => {
  const config = settings(local);
  expect(config.localHttp).toBe(true);
  expect(oidcTransactionCookieName(config)).toBe("aioffice_oidc_transaction");
  expect(config.redirectUri).toBe("http://127.0.0.1:3000" + OIDC_CALLBACK_PATH);
});
it.each([
  ["AIOFFICE_BROWSER_OIDC_LOCAL_HTTP", "false"],
  ["AIOFFICE_BROWSER_OIDC_PUBLIC_ORIGIN", "http://office.example.test"],
  ["AIOFFICE_BROWSER_OIDC_PUBLIC_ORIGIN", "https://office.example.test"],
  ["AIOFFICE_BROWSER_OIDC_AUTHORIZATION_ENDPOINT", "http://foreign.invalid/authorize"],
  ["AIOFFICE_BROWSER_OIDC_TOKEN_ENDPOINT", "http://foreign.invalid/token"],
  ["AIOFFICE_BROWSER_OIDC_JWKS_URI", "http://identity:8082/certs"],
])("local flag cannot downgrade a deployed %s", (name, value) => {
  expect(() => settings({ ...local, [name]: value })).toThrow("Browser identity configuration is invalid.");
});

it("requires both the configured origin and addressed same origin", () => {
  const config = settings();
  const request = (headers: Record<string, string>) => new Request("https://web:3000/start", { method: "POST", headers });
  expect(oidcStartIsSameOrigin(request({ Origin: config.publicOrigin, Host: "office.example.test" }), config)).toBe(true);
  const deniedHeaders: Record<string, string>[] = [{ Host: "office.example.test" }, { Origin: "https://foreign.invalid", Host: "foreign.invalid" },
    { Origin: config.publicOrigin, Host: "foreign.invalid", "X-Forwarded-Host": "office.example.test" },
    { Origin: config.publicOrigin, Host: "office.example.test/a/.." }];
  for (const headers of deniedHeaders) {
    expect(oidcStartIsSameOrigin(request(headers), config)).toBe(false);
  }
});

it("uses independent unpredictable state, nonce and verifier for every login", () => {
  const transactions = Array.from({ length: 16 }, () => createOidcTransaction(company, 1000));
  const tokens = transactions.flatMap((item) => [item.state, item.nonce, item.verifier]);
  expect(new Set(tokens).size).toBe(48);
  for (const token of tokens) expect(Buffer.from(token, "base64url").length).toBe(32);
  expect(Object.isFrozen(transactions[0])).toBe(true);
});
it.each(["", "00000000-0000-0000-0000-000000000000", "foreign-company"])("rejects invalid company selector %s", (value) => {
  expect(() => createOidcTransaction(value)).toThrow("Invalid login transaction.");
});
it.each([-1, 1000.5, Number.NaN, Number.MAX_SAFE_INTEGER])("refuses unsafe transaction timestamps", (now) => {
  expect(() => createOidcTransaction(company, now)).toThrow("Invalid login transaction.");
});

it("encrypts company/verifier/state/nonce and accepts only its bounded lifetime", () => {
  const config = settings();
  const transaction = createOidcTransaction(company, 1000);
  const cookie = sealOidcTransaction(transaction, config);
  for (const privateValue of [company, transaction.state, transaction.nonce, transaction.verifier, config.transactionKey]) expect(cookie).not.toContain(privateValue);
  expect(cookie.length).toBeLessThan(2048);
  expect(openOidcTransaction(cookie, config, 1000)).toEqual(transaction);
  expect(openOidcTransaction(cookie, config, 1299)).toEqual(transaction);
  expect(openOidcTransaction(cookie, config, 1300)).toBeNull();
  expect(openOidcTransaction(cookie, config, 989)).toBeNull();
  expect(openOidcTransaction(cookie, config, 990)).toEqual(transaction);
  expect(sealOidcTransaction(transaction, config)).not.toBe(cookie);
});
it("denies corruption, truncation, malformed encodings and oversized cookie", () => {
  const config = settings();
  const cookie = sealOidcTransaction(createOidcTransaction(company, 1000), config);
  const parts = cookie.split(".");
  const damaged = Buffer.from(parts[2], "base64url"); damaged[0] ^= 1;
  for (const invalid of [undefined, "", "v2" + cookie.slice(2), cookie.slice(0, -1), "x".repeat(2049),
    `${parts[0]}.${parts[1]}.${damaged.toString("base64url")}.${parts[3]}`, `${cookie}.extra`, cookie.replace(".", ".!")]) {
    expect(openOidcTransaction(invalid, config, 1000)).toBeNull();
  }
});
it.each([
  { AIOFFICE_BROWSER_OIDC_TRANSACTION_KEY: Buffer.alloc(32, 12).toString("base64url") },
  { AIOFFICE_BROWSER_OIDC_PUBLIC_ORIGIN: "https://another-office.example.test" },
  { AIOFFICE_BROWSER_OIDC_ISSUER: "https://login.example.test/realms/another" },
  { AIOFFICE_BROWSER_OIDC_CLIENT_ID: "another-client" },
])("binds encrypted transaction to the configured key/app/issuer/client", (overrides) => {
  const cookie = sealOidcTransaction(createOidcTransaction(company, 1000), settings());
  expect(openOidcTransaction(cookie, settings(overrides), 1000)).toBeNull();
});
it.each([
  { version: 2 }, { companyId: "foreign" }, { issuedAt: -1 }, { issuedAt: 1000.5 },
  { expiresAt: 1301 }, { state: "short" }, { nonce: "bad\nnonce" }, { verifier: "" }, { tenantId: "caller-authority" },
])("denies malformed authenticated transaction contents", (changes) => {
  const config = settings();
  const transaction = { ...createOidcTransaction(company, 1000), ...changes };
  const cookie = sealOidcTransaction(transaction as ReturnType<typeof createOidcTransaction>, config);
  expect(openOidcTransaction(cookie, config, 1000)).toBeNull();
});
