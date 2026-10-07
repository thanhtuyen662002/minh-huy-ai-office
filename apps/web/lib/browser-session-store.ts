import { createCipheriv, createDecipheriv, createHash, hkdfSync, randomBytes } from "node:crypto";
import { createClient } from "redis";
import { isCanonicalCompanyId } from "./local-ai-bff";
import { OIDC_TRANSACTION_SECONDS, type BrowserOidcSettings, type OidcTransaction } from "./browser-oidc";

export const BROWSER_BINDING_SECONDS = 86_400;
const unavailable = () => new Error("Browser session coordination is unavailable.");
const randomHandle = () => randomBytes(32).toString("base64url");
const digest = (value: string) => createHash("sha256").update(value).digest("hex");
const nowSeconds = () => Math.floor(Date.now() / 1000);

function isHandle(value: unknown): value is string {
  return typeof value === "string" && /^[A-Za-z0-9_-]{43}$/.test(value)
    && Buffer.from(value, "base64url").toString("base64url") === value;
}
function parseBinding(value: unknown): { id: string; generation: string } | null {
  if (typeof value !== "string" || value.length !== 87) return null;
  const pieces = value.split(".");
  return pieces.length === 2 && pieces.every(isHandle) ? { id: pieces[0], generation: pieces[1] } : null;
}
function validTransaction(value: OidcTransaction): boolean {
  const now = nowSeconds();
  return value.version === 1 && isCanonicalCompanyId(value.companyId)
    && [value.state, value.nonce, value.verifier].every(isHandle)
    && Number.isSafeInteger(value.issuedAt) && value.issuedAt >= 0 && value.issuedAt <= now + 10
    && Number.isSafeInteger(value.expiresAt) && value.expiresAt === value.issuedAt + OIDC_TRANSACTION_SECONDS && value.expiresAt > now;
}

export type RedisSessionEval = (script: string, key: string, args: string[]) => Promise<unknown>;
export type PrivateBrowserSession = Readonly<{
  accessToken: string;
  companyId: string;
  subject: string;
  expiresAt: number;
}>;

// Each operation has one explicitly declared Redis key. No process-local
// authority/cache is used. TIME and INFO are read in the same atomic script;
// a restored pre-revocation AOF/RDB row from a prior process cannot authenticate.
const clock = `
local key = KEYS[1]
local now = tonumber(redis.call('TIME')[1])
local incarnation = string.match(redis.call('INFO', 'server'), 'run_id:([a-f0-9]+)')
if not incarnation or string.len(incarnation) ~= 40 then return redis.error_reply('Session store unavailable') end
`;
const currentBinding = clock + `
local bindingExpiry = tonumber(redis.call('HGET', key, 'bindingExpiry') or '0')
if bindingExpiry <= now or redis.call('HGET', key, 'incarnation') ~= incarnation
  or redis.call('HGET', key, 'generation') ~= ARGV[1] then return false end
`;
const registerScript = clock + `
if redis.call('EXISTS', key) ~= 0 then return false end
local expiry = now + ${BROWSER_BINDING_SECONDS}
redis.call('HSET', key, 'incarnation', incarnation, 'generation', ARGV[1], 'bindingExpiry', expiry)
redis.call('EXPIREAT', key, expiry)
return 1
`;
const beginScript = currentBinding + `
local expiry = tonumber(ARGV[4])
if expiry <= now or expiry > now + ${OIDC_TRANSACTION_SECONDS + 10} or expiry > bindingExpiry then return false end
redis.call('HSET', key, 'generation', ARGV[2], 'pending', ARGV[3], 'pendingExpiry', expiry, 'phase', 'pending')
redis.call('HDEL', key, 'claim')
return 1
`;
const claimScript = currentBinding + `
if redis.call('HGET', key, 'pending') ~= ARGV[2] or redis.call('HGET', key, 'phase') ~= 'pending'
  or tonumber(redis.call('HGET', key, 'pendingExpiry') or '0') <= now then return false end
redis.call('HSET', key, 'phase', 'processing', 'claim', ARGV[3])
return 1
`;
const completeScript = currentBinding + `
local expiry = tonumber(ARGV[6])
if redis.call('HGET', key, 'pending') ~= ARGV[2] or redis.call('HGET', key, 'phase') ~= 'processing'
  or redis.call('HGET', key, 'claim') ~= ARGV[3]
  or tonumber(redis.call('HGET', key, 'pendingExpiry') or '0') <= now
  or expiry <= now or expiry > now + 3610 or expiry > bindingExpiry then return false end
redis.call('HSET', key, 'sid', ARGV[4], 'payload', ARGV[5], 'sessionExpiry', expiry)
redis.call('HDEL', key, 'pending', 'pendingExpiry', 'phase', 'claim')
return 1
`;
const readScript = currentBinding + `
if redis.call('HGET', key, 'sid') ~= ARGV[2]
  or tonumber(redis.call('HGET', key, 'sessionExpiry') or '0') <= now then return false end
return redis.call('HGET', key, 'payload')
`;
const revokeScript = currentBinding + `
redis.call('HSET', key, 'generation', ARGV[2])
redis.call('HDEL', key, 'sid', 'payload', 'sessionExpiry', 'pending', 'pendingExpiry', 'phase', 'claim')
return 1
`;

function validSession(value: PrivateBrowserSession): boolean {
  const now = nowSeconds();
  return typeof value.accessToken === "string" && value.accessToken.length > 0 && value.accessToken.length <= 16_384
    && !/[\r\n\0]/.test(value.accessToken) && isCanonicalCompanyId(value.companyId)
    && typeof value.subject === "string" && value.subject.length > 0 && value.subject.length <= 200
    && Number.isSafeInteger(value.expiresAt) && value.expiresAt > now && value.expiresAt <= now + 3600;
}

export function browserBindingCookieName(settings: BrowserOidcSettings): string {
  return settings.localHttp ? "aioffice_browser_binding" : "__Host-aioffice_browser_binding";
}
export function browserSessionCookieName(settings: BrowserOidcSettings): string {
  return settings.localHttp ? "aioffice_browser_session" : "__Host-aioffice_browser_session";
}

// This private coordinator never grants company/role authority. complete() is
// called only AFTER signed code exchange and fresh scoped Core API validation.
export function createBrowserSessionCoordinator(evaluate: RedisSessionEval, settings: BrowserOidcSettings) {
  const context = JSON.stringify(["aioffice-browser-session-v1", settings.issuer, settings.clientId, settings.redirectUri]);
  const namespace = digest(context);
  const encryptionKey = Buffer.from(hkdfSync("sha256", Buffer.from(settings.transactionKey, "base64url"),
    Buffer.alloc(0), Buffer.from(context), 32));
  const key = (id: string) => `aioffice:browser:${namespace}:{${digest(id)}}`;
  async function run(script: string, id: string, args: string[]) {
    try { return await evaluate(script, key(id), args); } catch { throw unavailable(); }
  }
  function aad(id: string, sid: string) { return Buffer.from(JSON.stringify([context, digest(id), digest(sid)])); }
  function seal(id: string, sid: string, session: PrivateBrowserSession): string {
    const iv = randomBytes(12);
    const cipher = createCipheriv("aes-256-gcm", encryptionKey, iv);
    cipher.setAAD(aad(id, sid));
    const ciphertext = Buffer.concat([cipher.update(JSON.stringify(session), "utf8"), cipher.final()]);
    return ["v1", iv.toString("base64url"), ciphertext.toString("base64url"), cipher.getAuthTag().toString("base64url")].join(".");
  }
  function open(id: string, sid: string, payload: unknown): PrivateBrowserSession | null {
    try {
      if (typeof payload !== "string" || payload.length > 30_000) return null;
      const pieces = payload.split(".");
      if (pieces.length !== 4 || pieces[0] !== "v1"
        || pieces.slice(1).some((piece) => !/^[A-Za-z0-9_-]+$/.test(piece)
          || Buffer.from(piece, "base64url").toString("base64url") !== piece)) return null;
      const [iv, ciphertext, tag] = pieces.slice(1).map((piece) => Buffer.from(piece, "base64url"));
      if (iv.length !== 12 || tag.length !== 16) return null;
      const decipher = createDecipheriv("aes-256-gcm", encryptionKey, iv);
      decipher.setAAD(aad(id, sid)); decipher.setAuthTag(tag);
      const value = JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(Buffer.concat([decipher.update(ciphertext), decipher.final()])));
      if (!value || typeof value !== "object" || Array.isArray(value)
        || Object.keys(value).sort().join(",") !== "accessToken,companyId,expiresAt,subject" || !validSession(value)) return null;
      return Object.freeze(value);
    } catch { return null; }
  }
  return Object.freeze({
    async register(): Promise<string> {
      // Caller cannot re-register an old binding after expiry/revocation.
      const id = randomHandle(), generation = randomHandle();
      if (await run(registerScript, id, [digest(generation)]) !== 1) throw unavailable();
      return `${id}.${generation}`;
    },
    async begin(binding: string, transaction: OidcTransaction): Promise<string | null> {
      const parsed = parseBinding(binding);
      if (!parsed || !validTransaction(transaction)) return null;
      const generation = randomHandle();
      const result = await run(beginScript, parsed.id, [digest(parsed.generation), digest(generation), digest(transaction.state), String(transaction.expiresAt)]);
      // New login supersedes pending callbacks but preserves the active SID.
      return result === 1 ? `${parsed.id}.${generation}` : null;
    },
    async claim(binding: string, transaction: OidcTransaction): Promise<string | null> {
      const parsed = parseBinding(binding);
      if (!parsed || !validTransaction(transaction)) return null;
      const claim = randomHandle();
      const result = await run(claimScript, parsed.id, [digest(parsed.generation), digest(transaction.state), digest(claim)]);
      return result === 1 ? claim : null;
    },
    async complete(binding: string, transaction: OidcTransaction, claim: string, session: PrivateBrowserSession): Promise<string | null> {
      const parsed = parseBinding(binding);
      if (!parsed || !validTransaction(transaction) || !isHandle(claim) || !validSession(session)
        || session.companyId !== transaction.companyId) return null;
      const sid = randomHandle();
      const payload = seal(parsed.id, sid, session);
      const result = await run(completeScript, parsed.id, [digest(parsed.generation), digest(transaction.state), digest(claim),
        digest(sid), payload, String(session.expiresAt)]);
      return result === 1 ? sid : null;
    },
    async read(binding: string, sid: string): Promise<PrivateBrowserSession | null> {
      const parsed = parseBinding(binding);
      if (!parsed || !isHandle(sid)) return null;
      const result = await run(readScript, parsed.id, [digest(parsed.generation), digest(sid)]);
      return open(parsed.id, sid, result);
    },
    async revoke(binding: string): Promise<string | null> {
      const parsed = parseBinding(binding);
      if (!parsed) return null;
      const generation = randomHandle();
      const result = await run(revokeScript, parsed.id, [digest(parsed.generation), digest(generation)]);
      return result === 1 ? `${parsed.id}.${generation}` : null;
    },
  });
}

export function readBrowserSessionRedisUrl(settings: BrowserOidcSettings,
  environment: (name: string) => string | undefined = (name) => process.env[name]): string {
  const value = environment("AIOFFICE_BROWSER_SESSION_REDIS_URL");
  try {
    if (!value || value.length > 4096 || /[\s\\?#]/.test(value)) throw unavailable();
    const url = new URL(value);
    if (!url.hostname || !/^(?:\/(?:[0-9]|1[0-5]))?$/.test(url.pathname)) throw unavailable();
    if (url.protocol !== "rediss:") {
      const loopback = ["localhost", "127.0.0.1", "[::1]"].includes(url.hostname);
      if (!settings.localHttp || url.protocol !== "redis:"
        || (!loopback && !(url.hostname === "redis" && url.port === "6379"))) throw unavailable();
    }
    return value;
  } catch { throw unavailable(); }
}

export function createBrowserSessionRedis(settings: BrowserOidcSettings,
  environment?: (name: string) => string | undefined) {
  const url = readBrowserSessionRedisUrl(settings, environment);
  const client = createClient({ url, socket: { connectTimeout: 2000, reconnectStrategy: false },
    commandOptions: { timeout: 2000 }, commandsQueueMaxLength: 256, disableOfflineQueue: true });
  // Errors can contain private endpoint/credentials; callers get a generic
  // failure. No token/key/session data is logged by this authentication store.
  client.on("error", () => {});
  let connecting: Promise<unknown> | null = null;
  let closed = false;
  async function connectWithDeadline() {
    // connectTimeout covers TCP/TLS establishment, not an unresponsive Redis
    // authentication/HELLO handshake. Bound and destroy the whole handshake.
    let timer: ReturnType<typeof setTimeout> | undefined;
    try {
      await Promise.race([client.connect(), new Promise<never>((_resolve, reject) => {
        timer = setTimeout(() => { client.destroy(); reject(unavailable()); }, 2000);
      })]);
    } finally { clearTimeout(timer); }
  }
  const evaluate: RedisSessionEval = async (script, key, args) => {
    try {
      if (closed) throw unavailable();
      if (!client.isReady) {
        connecting ??= connectWithDeadline().finally(() => { connecting = null; });
        await connecting;
      }
      return await client.eval(script, { keys: [key], arguments: args });
    } catch { throw unavailable(); }
  };
  return Object.freeze({ evaluate, close() { closed = true; client.destroy(); } });
}
