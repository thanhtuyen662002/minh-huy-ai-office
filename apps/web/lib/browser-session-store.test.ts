// @vitest-environment node
import { createServer } from "node:net";
import { afterEach, expect, it, vi } from "vitest";
import { createOidcTransaction, readBrowserOidcSettings } from "./browser-oidc";
import { browserBindingCookieName, browserSessionCookieName, createBrowserSessionCoordinator,
  createBrowserSessionRedis, readBrowserSessionRedisUrl } from "./browser-session-store";

const env: Record<string, string> = {
  AIOFFICE_BROWSER_OIDC_ENABLED: "true", AIOFFICE_BROWSER_OIDC_PUBLIC_ORIGIN: "https://office.example.test",
  AIOFFICE_BROWSER_OIDC_ISSUER: "https://identity.example.test/realm", AIOFFICE_BROWSER_OIDC_CLIENT_ID: "browser-office",
  AIOFFICE_BROWSER_OIDC_AUTHORIZATION_ENDPOINT: "https://identity.example.test/auth",
  AIOFFICE_BROWSER_OIDC_TOKEN_ENDPOINT: "https://identity.example.test/token", AIOFFICE_BROWSER_OIDC_JWKS_URI: "https://identity.example.test/certs",
  AIOFFICE_BROWSER_OIDC_TRANSACTION_KEY: Buffer.alloc(32, 8).toString("base64url"),
};
const settings = readBrowserOidcSettings((name) => env[name])!;
const companyId = "22222222-2222-2222-2222-222222222222";
const handle = Buffer.alloc(32, 7).toString("base64url");
const binding = `${handle}.${handle}`;
const transaction = () => createOidcTransaction(companyId);
const session = () => ({ companyId, subject: "private-subject", accessToken: "private-access-token", expiresAt: Math.floor(Date.now() / 1000) + 300 });
afterEach(() => vi.restoreAllMocks());

it("uses Host cookies for HTTPS and explicit separate loopback names", () => {
  expect(browserBindingCookieName(settings)).toBe("__Host-aioffice_browser_binding");
  expect(browserSessionCookieName(settings)).toBe("__Host-aioffice_browser_session");
  expect(browserBindingCookieName({ ...settings, localHttp: true })).toBe("aioffice_browser_binding");
  expect(browserSessionCookieName({ ...settings, localHttp: true })).toBe("aioffice_browser_session");
});
it.each([undefined, "", "redis://foreign.example:6379", "http://redis:6379", "rediss://redis:6379?mode=private",
  "rediss://redis:6379#", "rediss://redis:6379/16", "rediss://redis:6379/-1", "rediss://redis:6379/path",
  "rediss://redis:6379/0/", " rediss://redis:6379", "rediss://redis:6379\n", "rediss://redis:6379/0\\anything",
])("refuses unsafe production Redis configuration without reflecting %s", (value) => {
  expect(() => readBrowserSessionRedisUrl(settings, () => value)).toThrow("Browser session coordination is unavailable.");
});
it.each(["rediss://private-redis.example:6380/0", "rediss://app:encoded%40secret@private-redis.example:6380/15"])("accepts operator TLS Redis configuration", (value) => {
  expect(readBrowserSessionRedisUrl(settings, () => value)).toBe(value);
});
it.each(["redis://127.0.0.1:6379", "redis://localhost:16379/0", "redis://[::1]:6379", "redis://redis:6379"])("allows only explicit local loopback or the fixed Redis service", (value) => {
  expect(readBrowserSessionRedisUrl({ ...settings, localHttp: true }, () => value)).toBe(value);
});
it.each(["redis://foreign.invalid:6379", "redis://redis:6380", "redis://identity:6379", "redis://redis"])("rejects foreign local Redis routing", (value) => {
  expect(() => readBrowserSessionRedisUrl({ ...settings, localHttp: true }, () => value)).toThrow("Browser session coordination is unavailable.");
});
it.each(["", "short", `${handle}.${handle}=`, `${handle}.${handle}.extra`, `${handle}.` + "_".repeat(43)])("rejects invalid or noncanonical handles before store access", async (value) => {
  const evaluate = vi.fn(), store = createBrowserSessionCoordinator(evaluate, settings);
  expect(await store.begin(value, transaction())).toBeNull();
  expect(await store.claim(value, transaction())).toBeNull();
  expect(await store.complete(value, transaction(), handle, session())).toBeNull();
  expect(await store.read(value, handle)).toBeNull();
  expect(await store.revoke(value)).toBeNull();
  expect(evaluate).not.toHaveBeenCalled();
});
it.each([{ companyId: "bad" }, { state: "bad" }, { nonce: "bad" }, { verifier: "bad" }, { version: 2 },
  { issuedAt: 1, expiresAt: 301 }, { expiresAt: Math.floor(Date.now() / 1000) + 999 },
])("refuses invalid transactions before coordination", async (changes) => {
  const evaluate = vi.fn(), store = createBrowserSessionCoordinator(evaluate, settings);
  const value = { ...transaction(), ...changes } as ReturnType<typeof transaction>;
  expect(await store.begin(binding, value)).toBeNull();
  expect(await store.claim(binding, value)).toBeNull();
  expect(evaluate).not.toHaveBeenCalled();
});
it.each([{ companyId: "33333333-3333-3333-3333-333333333333" }, { subject: "" }, { subject: "x".repeat(201) },
  { accessToken: "" }, { accessToken: "x".repeat(16_385) }, { accessToken: "private\r\nheader" },
  { expiresAt: 1 }, { expiresAt: Math.floor(Date.now() / 1000) + 4000 },
])("refuses unauthorized-company or invalid private session inputs before publication", async (changes) => {
  const evaluate = vi.fn(), store = createBrowserSessionCoordinator(evaluate, settings);
  expect(await store.complete(binding, transaction(), handle, { ...session(), ...changes })).toBeNull();
  expect(evaluate).not.toHaveBeenCalled();
});
it("encrypts private data and authenticates it against binding, SID and configuration", async () => {
  let encrypted: unknown;
  const evaluate = vi.fn(async (_script: string, _key: string, args: string[]) => {
    if (args.length === 6) { encrypted = args[4]; return 1; }
    return encrypted;
  });
  const store = createBrowserSessionCoordinator(evaluate, settings), value = session();
  const sid = (await store.complete(binding, transaction(), handle, value))!;
  expect(sid).toMatch(/^[A-Za-z0-9_-]{43}$/);
  expect(encrypted).not.toContain(value.accessToken);
  expect(encrypted).not.toContain(value.subject);
  expect(encrypted).not.toContain(value.companyId);
  expect(await store.read(binding, sid)).toEqual(value);
  expect(await store.read(binding, handle)).toBeNull();
  expect(await store.read(`${Buffer.alloc(32, 9).toString("base64url")}.${handle}`, sid)).toBeNull();
  const foreign = createBrowserSessionCoordinator(evaluate, { ...settings, transactionKey: Buffer.alloc(32, 9).toString("base64url") });
  expect(await foreign.read(binding, sid)).toBeNull();
  encrypted = String(encrypted).slice(0, -2) + "AA";
  expect(await store.read(binding, sid)).toBeNull();
});
it("does not return private store errors or fall back to a process session", async () => {
  const store = createBrowserSessionCoordinator(async () => { throw new Error("private-password/token/provider-details"); }, settings);
  await expect(store.register()).rejects.toThrow(/^Browser session coordination is unavailable\.$/);
  await expect(store.read(binding, handle)).rejects.toThrow(/^Browser session coordination is unavailable\.$/);
  await expect(store.revoke(binding)).rejects.toThrow(/^Browser session coordination is unavailable\.$/);
});
it("bounds a stalled Redis handshake without replaying offline work", async () => {
  const sockets = new Set<import("node:net").Socket>();
  const server = createServer((socket) => { sockets.add(socket); socket.on("data", () => {}); socket.on("close", () => sockets.delete(socket)); });
  await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
  const address = server.address() as import("node:net").AddressInfo;
  const transport = createBrowserSessionRedis({ ...settings, localHttp: true }, () => `redis://private:secret@127.0.0.1:${address.port}`);
  const store = createBrowserSessionCoordinator(transport.evaluate, settings);
  const started = Date.now();
  try {
    await expect(store.register()).rejects.toThrow(/^Browser session coordination is unavailable\.$/);
    expect(Date.now() - started).toBeLessThan(3500);
  } finally {
    transport.close(); for (const socket of sockets) socket.destroy();
    await new Promise<void>((resolve) => server.close(() => resolve()));
  }
}, 5000);
