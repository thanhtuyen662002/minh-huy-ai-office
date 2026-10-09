// @vitest-environment node
import { execFileSync } from "node:child_process";
import { createHash } from "node:crypto";
import { createClient } from "redis";
import { afterAll, beforeAll, expect, it } from "vitest";
import { createOidcTransaction, readBrowserOidcSettings } from "./browser-oidc";
import { createBrowserSessionCoordinator, createBrowserSessionRedis } from "./browser-session-store";

// Unit/mocked coordination is never reported as actual Redis proof. The web CI
// gate supplies a disposable Redis service and requires every test below.
const redisUrl = process.env.AIOFFICE_TEST_SESSION_REDIS_URL;
const actual = it.skipIf(!redisUrl);
const companyId = "22222222-2222-2222-2222-222222222222";
const env: Record<string, string> = {
  AIOFFICE_BROWSER_OIDC_ENABLED: "true", AIOFFICE_BROWSER_OIDC_LOCAL_HTTP: "true",
  AIOFFICE_BROWSER_OIDC_PUBLIC_ORIGIN: "http://127.0.0.1:3000", AIOFFICE_BROWSER_OIDC_ISSUER: "http://127.0.0.1:8081/realm",
  AIOFFICE_BROWSER_OIDC_CLIENT_ID: "browser-office", AIOFFICE_BROWSER_OIDC_AUTHORIZATION_ENDPOINT: "http://127.0.0.1:8081/auth",
  AIOFFICE_BROWSER_OIDC_TOKEN_ENDPOINT: "http://127.0.0.1:8081/token", AIOFFICE_BROWSER_OIDC_JWKS_URI: "http://127.0.0.1:8081/certs",
  AIOFFICE_BROWSER_OIDC_TRANSACTION_KEY: Buffer.alloc(32, 17).toString("base64url"),
};
const settings = readBrowserOidcSettings((name) => env[name])!;
const transports = [createBrowserSessionRedis(settings, () => redisUrl || "redis://127.0.0.1:1"),
  createBrowserSessionRedis(settings, () => redisUrl || "redis://127.0.0.1:1")];
const ownedKeys = new Set<string>();
const storeFor = (transport: ReturnType<typeof createBrowserSessionRedis>) => createBrowserSessionCoordinator(async (script, key, args) => {
  ownedKeys.add(key); return transport.evaluate(script, key, args);
}, settings);
const stores = transports.map(storeFor);
let observer = createClient({ url: redisUrl || "redis://127.0.0.1:1", socket: { reconnectStrategy: false }, commandOptions: { timeout: 2000 } });
observer.on("error", () => {});
const privateSession = (expiresAt = Math.floor(Date.now() / 1000) + 300) => ({ accessToken: "private-issued-token", companyId, subject: "private-subject", expiresAt });
const random = () => createOidcTransaction(companyId).state;
const onlyNewKey = (prior: Set<string>) => [...ownedKeys].find((key) => !prior.has(key))!;
function tamperAuthenticatedByte(payload: string) {
  const pieces = payload.split(".");
  const original = Buffer.from(pieces[3], "base64url"), damaged = Buffer.from(original);
  expect(original.length).toBe(16);
  // A random valid tag can already end in AA. Flip a decoded byte so this
  // adversary always changes authenticated data, with canonical encoding.
  damaged[0] ^= 1; expect(damaged.equals(original)).toBe(false);
  pieces[3] = damaged.toString("base64url");
  const tampered = pieces.join("."); expect(tampered).not.toBe(payload); return tampered;
}

async function pending(binding?: string) {
  const prior = new Set(ownedKeys);
  const registered = binding || await stores[0].register();
  const transaction = createOidcTransaction(companyId);
  const current = (await stores[0].begin(registered, transaction))!;
  expect(current).toBeTruthy();
  return { binding: current, transaction, key: binding ? undefined : onlyNewKey(prior) };
}
async function issued() {
  const value = await pending(), claim = (await stores[1].claim(value.binding, value.transaction))!;
  const session = privateSession(), sid = (await stores[0].complete(value.binding, value.transaction, claim, session))!;
  expect(sid).toBeTruthy();
  expect(await stores[1].read(value.binding, sid)).toEqual(session);
  return { ...value, claim, session, sid };
}
beforeAll(async () => { if (redisUrl) await observer.connect(); });
afterAll(async () => {
  transports.forEach((transport) => transport.close());
  if (observer.isReady && ownedKeys.size) await observer.del([...ownedKeys]);
  if (observer.isOpen) observer.destroy();
});

actual("checks shared registered binding generations without granting session authority", async () => {
  const binding = await stores[0].register();
  expect(await stores[1].isCurrent(binding)).toBe(true);
  expect(await stores[1].read(binding, random())).toBeNull();
  const current = (await stores[0].begin(binding, createOidcTransaction(companyId)))!;
  expect(await stores[1].isCurrent(binding)).toBe(false);
  expect(await stores[1].isCurrent(current)).toBe(true);
  const loggedOut = (await stores[0].revoke(current))!;
  expect(await stores[1].isCurrent(current)).toBe(false);
  expect(await stores[1].isCurrent(loggedOut)).toBe(true);
});

actual("consumes exactly one of32 parallel callback claims across independent clients", async () => {
  const value = await pending();
  const claims = await Promise.all(Array.from({ length: 32 }, (_, i) => stores[i % 2].claim(value.binding, value.transaction)));
  expect(claims.filter(Boolean)).toHaveLength(1);
  const claim = claims.find(Boolean)!;
  const session = privateSession(), sid = (await stores[1].complete(value.binding, value.transaction, claim, session))!;
  expect(sid).toMatch(/^[A-Za-z0-9_-]{43}$/);
  expect(await stores[0].read(value.binding, sid)).toEqual(session);
  expect(await stores[1].read(value.binding, sid)).toEqual(session);
  expect(await stores[0].claim(value.binding, value.transaction)).toBeNull();
  expect(await stores[0].complete(value.binding, value.transaction, claim, session)).toBeNull();
  const row = JSON.stringify(await observer.hGetAll(value.key!));
  for (const secret of [value.binding.split(".")[0], value.binding.split(".")[1], value.transaction.state,
    value.transaction.verifier, session.accessToken, session.subject, session.companyId, sid]) expect(row).not.toContain(secret);
  expect(await observer.ttl(value.key!)).toBeGreaterThan(0);
  expect(await observer.ttl(value.key!)).toBeLessThanOrEqual(86_400);
});
actual("publishes only one session for concurrent completion of the same claimed callback", async () => {
  const value = await pending(), claim = (await stores[0].claim(value.binding, value.transaction))!;
  const session = privateSession();
  const results = await Promise.all(stores.map((store) => store.complete(value.binding, value.transaction, claim, session)));
  expect(results.filter(Boolean)).toHaveLength(1);
  expect(await stores[1].read(value.binding, results.find(Boolean)!)).toEqual(session);
});
actual("preserves active authority when replacement publication applied but its reply was lost", async () => {
  const old = await issued(), value = await pending(old.binding);
  const claim = (await stores[0].claim(value.binding, value.transaction))!;
  const uncertain = createBrowserSessionCoordinator(async (script, key, args) => {
    const reply = await transports[0].evaluate(script, key, args);
    if (args.length === 6) throw new Error("deliberately lost completion reply");
    return reply;
  }, settings);
  await expect(uncertain.complete(value.binding, value.transaction, claim, privateSession()))
    .rejects.toThrow(/^Browser session coordination is unavailable\.$/);
  const row = await observer.hGetAll(old.key!);
  expect(row.replacementSid).toMatch(/^[a-f0-9]{64}$/);
  expect(row.sid).toMatch(/^[a-f0-9]{64}$/);
  expect(await stores[1].read(value.binding, old.sid)).toEqual(old.session);
  expect(await stores[1].complete(value.binding, value.transaction, claim, privateSession())).toBeNull();
});
actual("retains active authority if a published replacement expires before browser receipt", async () => {
  const old = await issued(), value = await pending(old.binding), claim = (await stores[0].claim(value.binding, value.transaction))!;
  const sid = (await stores[1].complete(value.binding, value.transaction, claim, privateSession()))!;
  await observer.hSet(old.key!, "replacementExpiry", String(Math.floor(Date.now() / 1000) - 1));
  expect(await stores[0].read(value.binding, sid)).toBeNull();
  expect(await stores[1].read(value.binding, old.sid)).toEqual(old.session);
});
actual("promotes a delivered replacement atomically across16 readers and revokes the previous SID", async () => {
  const old = await issued(), value = await pending(old.binding), claim = (await stores[0].claim(value.binding, value.transaction))!;
  const session = { ...privateSession(), accessToken: "private-replacement-token" };
  const sid = (await stores[1].complete(value.binding, value.transaction, claim, session))!;
  expect(await stores[0].read(value.binding, old.sid)).toEqual(old.session);
  const replies = await Promise.all(Array.from({ length: 16 }, (_, i) => stores[i % 2].read(value.binding, sid)));
  expect(replies.every((reply) => JSON.stringify(reply) === JSON.stringify(session))).toBe(true);
  expect(await stores[1].read(value.binding, old.sid)).toBeNull();
  expect(await observer.hExists(old.key!, "replacementSid")).toBe(0);
  expect(await stores[0].read(value.binding, sid)).toEqual(session);
});
actual("does not promote tampered replacement ciphertext or evict the valid active SID", async () => {
  const old = await issued(), value = await pending(old.binding), claim = (await stores[0].claim(value.binding, value.transaction))!;
  const sid = (await stores[1].complete(value.binding, value.transaction, claim, privateSession()))!;
  const payload = (await observer.hGet(old.key!, "replacementPayload"))!;
  await observer.hSet(old.key!, "replacementPayload", tamperAuthenticatedByte(payload));
  expect(await stores[0].read(value.binding, sid)).toBeNull();
  expect(await stores[1].read(value.binding, old.sid)).toEqual(old.session);
});
actual("fences undelivered replacements on a new start and revokes active/provisional authority on logout", async () => {
  const old = await issued(), first = await pending(old.binding), firstClaim = (await stores[0].claim(first.binding, first.transaction))!;
  const sid = (await stores[1].complete(first.binding, first.transaction, firstClaim, privateSession()))!;
  const latest = await pending(first.binding);
  expect(await stores[0].read(latest.binding, sid)).toBeNull();
  expect(await stores[1].read(latest.binding, old.sid)).toEqual(old.session);
  const claim = (await stores[0].claim(latest.binding, latest.transaction))!;
  const replacement = (await stores[1].complete(latest.binding, latest.transaction, claim, privateSession()))!;
  const loggedOut = (await stores[0].revoke(latest.binding))!;
  for (const cookie of [old.sid, replacement]) {
    expect(await stores[1].read(latest.binding, cookie)).toBeNull();
    expect(await stores[1].read(loggedOut, cookie)).toBeNull();
  }
});
actual("recovers a delivered SID after promotion applied but the acknowledgment reply was lost", async () => {
  const old = await issued(), value = await pending(old.binding), claim = (await stores[0].claim(value.binding, value.transaction))!;
  const session = privateSession(), sid = (await stores[1].complete(value.binding, value.transaction, claim, session))!;
  const uncertain = createBrowserSessionCoordinator(async (script, key, args) => {
    const reply = await transports[0].evaluate(script, key, args);
    if (script.includes("redis.call('HSET', key, 'sid'")) throw new Error("deliberately lost promotion reply");
    return reply;
  }, settings);
  await expect(uncertain.read(value.binding, sid)).rejects.toThrow(/^Browser session coordination is unavailable\.$/);
  expect(await stores[0].read(value.binding, sid)).toEqual(session);
  expect(await stores[1].read(value.binding, old.sid)).toBeNull();
});
actual("refuses a replacement after an older worker rotates generation without knowing replacement fields", async () => {
  const old = await issued(), value = await pending(old.binding), claim = (await stores[0].claim(value.binding, value.transaction))!;
  const sid = (await stores[1].complete(value.binding, value.transaction, claim, privateSession()))!;
  const generation = random(), loggedOut = `${value.binding.split(".")[0]}.${generation}`;
  // Simulate the previous protocol's atomic logout, which cleared active and
  // pending fields but did not know the newly expanded replacement fields.
  await observer.multi().hSet(old.key!, "generation", createHash("sha256").update(generation).digest("hex"))
    .hDel(old.key!, ["sid", "payload", "sessionExpiry", "pending", "pendingExpiry", "phase", "claim"]).exec();
  expect(await observer.hExists(old.key!, "replacementSid")).toBe(1);
  expect(await stores[0].isCurrent(loggedOut)).toBe(true);
  expect(await stores[0].read(loggedOut, sid)).toBeNull();
  expect(await stores[1].read(value.binding, sid)).toBeNull();
});
actual("preserves the issued session through wrong-state/claim/company denials", async () => {
  const old = await issued(), value = await pending(old.binding);
  expect(await stores[0].read(old.binding, old.sid)).toBeNull();
  expect(await stores[1].read(value.binding, old.sid)).toEqual(old.session);
  expect(await stores[0].claim(value.binding, createOidcTransaction(companyId))).toBeNull();
  const claim = (await stores[1].claim(value.binding, value.transaction))!;
  expect(await stores[0].complete(value.binding, value.transaction, random(), privateSession())).toBeNull();
  expect(await stores[0].complete(value.binding, value.transaction, claim, { ...privateSession(), companyId: "33333333-3333-3333-3333-333333333333" })).toBeNull();
  expect(await stores[0].read(value.binding, old.sid)).toEqual(old.session);
});
actual("fences superseded callbacks, stale starts and reordered old binding/SID cookies", async () => {
  const old = await issued(), first = await pending(old.binding);
  const claim = (await stores[0].claim(first.binding, first.transaction))!;
  const latest = await pending(first.binding);
  expect(await stores[1].begin(first.binding, createOidcTransaction(companyId))).toBeNull();
  expect(await stores[1].complete(first.binding, first.transaction, claim, privateSession())).toBeNull();
  expect(await stores[0].read(latest.binding, old.sid)).toEqual(old.session);
  const currentClaim = (await stores[1].claim(latest.binding, latest.transaction))!;
  const sid = (await stores[0].complete(latest.binding, latest.transaction, currentClaim, privateSession()))!;
  expect(await stores[1].read(latest.binding, sid)).toBeTruthy();
  expect(await stores[1].read(latest.binding, old.sid)).toBeNull();
  expect(await stores[1].read(first.binding, sid)).toBeNull();
});
actual("logout revokes issued authority and prevents delayed callback/start resurrection", async () => {
  const old = await issued(), value = await pending(old.binding);
  const claim = (await stores[0].claim(value.binding, value.transaction))!;
  const loggedOut = (await stores[1].revoke(value.binding))!;
  expect(loggedOut).toBeTruthy();
  expect(await stores[0].read(loggedOut, old.sid)).toBeNull();
  expect(await stores[0].read(value.binding, old.sid)).toBeNull();
  expect(await stores[0].complete(value.binding, value.transaction, claim, privateSession())).toBeNull();
  expect(await stores[0].begin(value.binding, createOidcTransaction(companyId))).toBeNull();
  const fresh = await pending(loggedOut), freshClaim = (await stores[1].claim(fresh.binding, fresh.transaction))!;
  const sid = (await stores[0].complete(fresh.binding, fresh.transaction, freshClaim, privateSession()))!;
  expect(await stores[1].read(fresh.binding, sid)).toBeTruthy();
});
actual("serializes16 actual logout/completion races without restoring active authority", async () => {
  for (let i = 0; i < 16; i++) {
    const value = await pending(), claim = (await stores[0].claim(value.binding, value.transaction))!;
    const [sid, loggedOut] = await Promise.all([stores[0].complete(value.binding, value.transaction, claim, privateSession()), stores[1].revoke(value.binding)]);
    expect(loggedOut).toBeTruthy();
    if (sid) {
      expect(await stores[0].read(value.binding, sid)).toBeNull();
      expect(await stores[1].read(loggedOut!, sid)).toBeNull();
    }
    expect(await stores[1].complete(value.binding, value.transaction, claim, privateSession())).toBeNull();
  }
});
actual("uses Redis time to reject expired pending authority while retaining the active SID", async () => {
  const old = await issued(), value = await pending(old.binding);
  await observer.hSet(old.key!, "pendingExpiry", String(Math.floor(Date.now() / 1000) - 1));
  expect(await stores[0].claim(value.binding, value.transaction)).toBeNull();
  expect(await stores[1].read(value.binding, old.sid)).toEqual(old.session);
});
actual("rejects expired sessions and missing bindings without recreating old generations", async () => {
  const value = await issued();
  await observer.hSet(value.key!, "sessionExpiry", String(Math.floor(Date.now() / 1000) - 1));
  expect(await stores[1].read(value.binding, value.sid)).toBeNull();
  await observer.del(value.key!);
  expect(await stores[0].begin(value.binding, createOidcTransaction(companyId))).toBeNull();
  expect(await stores[1].complete(value.binding, value.transaction, value.claim, value.session)).toBeNull();
  expect(await stores[1].read(value.binding, value.sid)).toBeNull();
  expect(await stores[0].register()).not.toBe(value.binding);
});
actual("refuses restored rows from a different Redis process incarnation", async () => {
  const value = await issued(), incarnation = (await observer.hGet(value.key!, "incarnation"))!;
  await observer.hSet(value.key!, "incarnation", "0".repeat(40));
  expect(await stores[1].read(value.binding, value.sid)).toBeNull();
  expect(await stores[0].begin(value.binding, createOidcTransaction(companyId))).toBeNull();
  expect(await stores[1].revoke(value.binding)).toBeNull();
  await observer.hSet(value.key!, "incarnation", incarnation);
  expect(await stores[0].read(value.binding, value.sid)).toEqual(value.session);
});
actual("denies ciphertext tampering, cross-binding copies and private key changes", async () => {
  const original = await issued(), foreign = await issued();
  const payload = (await observer.hGet(original.key!, "payload"))!;
  await observer.hSet(foreign.key!, "payload", payload);
  expect(await stores[1].read(foreign.binding, foreign.sid)).toBeNull();
  expect(await stores[1].read(original.binding, original.sid)).toEqual(original.session);
  const wrongKey = createBrowserSessionCoordinator(transports[0].evaluate, { ...settings, transactionKey: Buffer.alloc(32, 18).toString("base64url") });
  expect(await wrongKey.read(original.binding, original.sid)).toBeNull();
  await observer.hSet(original.key!, "payload", tamperAuthenticatedByte(payload));
  expect(await stores[1].read(original.binding, original.sid)).toBeNull();
});
actual("fails closed with generic errors during an unavailable Redis endpoint", async () => {
  const value = await issued();
  const unavailable = createBrowserSessionRedis(settings, () => "redis://127.0.0.1:1");
  try {
    const store = createBrowserSessionCoordinator(unavailable.evaluate, settings);
    await expect(store.read(value.binding, value.sid)).rejects.toThrow(/^Browser session coordination is unavailable\.$/);
    await expect(store.revoke(value.binding)).rejects.toThrow(/^Browser session coordination is unavailable\.$/);
    expect(await stores[0].read(value.binding, value.sid)).toEqual(value.session);
  } finally { unavailable.close(); }
});
actual("denies a physically restored pre-logout RDB after a disposable Redis restart", async () => {
  const container = process.env.AIOFFICE_TEST_SESSION_REDIS_CONTAINER;
  expect(process.env.AIOFFICE_TEST_SESSION_REDIS_DISPOSABLE).toBe("true");
  expect(container).toMatch(/^[a-f0-9]{64}$/);
  const value = await issued(), oldRow = await observer.hGetAll(value.key!);
  expect(oldRow.sid).toMatch(/^[a-f0-9]{64}$/);
  // The saved snapshot has active authority; the later logout is deliberately
  // absent from that RDB. Only this CI service is restarted, never product Redis.
  await observer.configSet("save", "");
  await observer.sendCommand(["SAVE"]);
  expect(await stores[1].revoke(value.binding)).toBeTruthy();
  expect(await stores[0].read(value.binding, value.sid)).toBeNull();
  observer.destroy();
  transports.forEach((transport) => transport.close());
  execFileSync("docker", ["restart", container!], { stdio: "pipe", timeout: 20_000 });
  // docker restart reports process launch before Redis has finished loading
  // its snapshot. Probe only fresh read-only connections, never replay EVALs.
  const deadline = Date.now() + 10_000;
  while (true) {
    observer = createClient({ url: redisUrl!, socket: { reconnectStrategy: false, connectTimeout: 2000 }, commandOptions: { timeout: 2000 } });
    observer.on("error", () => {});
    try { await observer.connect(); expect(await observer.ping()).toBe("PONG"); break; }
    catch {
      observer.destroy();
      if (Date.now() >= deadline) throw new Error("Disposable Redis restart readiness deadline exceeded.");
      await new Promise((resolve) => setTimeout(resolve, 50));
    }
  }
  for (let index = 0; index < transports.length; index++) {
    transports[index] = createBrowserSessionRedis(settings, () => redisUrl!);
    stores[index] = storeFor(transports[index]);
  }
  const restored = await observer.hGetAll(value.key!);
  expect(restored.sid).toBe(oldRow.sid);
  expect(restored.generation).toBe(oldRow.generation);
  expect(restored.incarnation).toBe(oldRow.incarnation);
  expect(await observer.info("server")).not.toContain(`run_id:${oldRow.incarnation}`);
  expect(await stores[0].read(value.binding, value.sid)).toBeNull();
  expect(await stores[1].begin(value.binding, createOidcTransaction(companyId))).toBeNull();
  const fresh = await issued();
  expect(await stores[1].read(fresh.binding, fresh.sid)).toEqual(fresh.session);
}, 30_000);
