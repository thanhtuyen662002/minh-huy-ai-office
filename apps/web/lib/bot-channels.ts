export type BotChannel = "zalo" | "whatsapp";
export type BotGroup = { externalId: string; displayName: string };
export type BotCustomerGroup = BotGroup & { customerName: string };
export type BotStatus = { loginState: "logged_out" | "awaiting_qr" | "logged_in" | "error"; groups: BotGroup[];
  lastSummaryAt: string | null; lastError: string | null; receivedAt: string };
export type BotChannelView = { channel: BotChannel; enabled: boolean; dryRun: boolean; summarySchedule: string[];
  timeZone: "Asia/Ho_Chi_Minh"; techGroupExternalId: string | null; customerGroups: BotCustomerGroup[]; configVersion: string; status: BotStatus | null };
export type BotAiView = { baseUrl: string; model: string; apiKeyConfigured: boolean; version: string };
export type BotAdminView = { enabled: true; channels: BotChannelView[]; ai: BotAiView };
export type BotQr = { contentType: "image/png"; base64: string; expiresAt: string };
export type BotAudit = { id: string; channel: BotChannel | null; actorUserId: string; field: string; before: string | null; after: string | null; timestamp: string };
export const botObject = (v: unknown): v is Record<string, unknown> => !!v && typeof v === "object" && !Array.isArray(v);
const text = (v: unknown, max: number, empty = false): v is string => typeof v === "string" && (empty || !!v.trim()) && v.length <= max && !/[\u0000-\u001f\u007f]/.test(v);
const date = (v: unknown): v is string => typeof v === "string" && v.length <= 40 && Number.isFinite(Date.parse(v));
export const botVersion = (v: unknown): v is string => typeof v === "string" && /^(0|[1-9][0-9]{0,18})$/.test(v) && BigInt(v) <= 9223372036854775807n;
export const botChannel = (v: unknown): v is BotChannel => v === "zalo" || v === "whatsapp";
export const botSchedule = (v: unknown): v is string[] => Array.isArray(v) && v.length <= 1440 && v.every(x => typeof x === "string" && /^([01][0-9]|2[0-3]):[0-5][0-9]$/.test(x));
const nullable = (v: unknown, max: number) => v === null || text(v, max);
const keys = (v: Record<string, unknown>, allowed: string[]) => Object.keys(v).every(key => allowed.includes(key));
export function botUrl(v: unknown): v is string {
  if (!text(v, 1000)) return false;
  try { const url = new URL(v); return ["https:", "http:"].includes(url.protocol) && !url.username && !url.password && !url.search && !url.hash; } catch { return false; }
}
function group(v: unknown): BotGroup | null {
  return botObject(v) && text(v.externalId, 200) && text(v.displayName, 200) ? { externalId: v.externalId, displayName: v.displayName } : null;
}
function customer(v: unknown): BotCustomerGroup | null {
  const g = group(v); return g && botObject(v) && text(v.customerName, 200) ? { ...g, customerName: v.customerName } : null;
}
function list<T>(v: unknown, max: number, parse: (v: unknown) => T | null): T[] | null {
  if (!Array.isArray(v) || v.length > max) return null;
  const parsed = v.map(parse); return parsed.some(x => x === null) ? null : parsed as T[];
}
export function parseBotChannel(v: unknown): BotChannelView | null {
  if (!botObject(v) || !botChannel(v.channel) || typeof v.enabled !== "boolean" || typeof v.dryRun !== "boolean" || !botSchedule(v.summarySchedule)
    || v.timeZone !== "Asia/Ho_Chi_Minh" || !nullable(v.techGroupExternalId, 200) || !botVersion(v.configVersion)) return null;
  const customerGroups = list(v.customerGroups, 100, customer);
  if (!customerGroups || new Set(customerGroups.map(g => g.externalId)).size !== customerGroups.length
    || customerGroups.some(g => g.externalId === v.techGroupExternalId)) return null;
  let status: BotStatus | null = null;
  if (v.status !== null) {
    const s = v.status;
    if (!botObject(s) || !["logged_out", "awaiting_qr", "logged_in", "error"].includes(String(s.loginState)) || !date(s.receivedAt)
      || s.lastSummaryAt !== null && !date(s.lastSummaryAt) || !nullable(s.lastError, 500)) return null;
    const groups = list(s.groups, 500, group); if (!groups || new Set(groups.map(g => g.externalId)).size !== groups.length) return null;
    status = { loginState: s.loginState as BotStatus["loginState"], groups, receivedAt: s.receivedAt, lastSummaryAt: s.lastSummaryAt as string | null, lastError: s.lastError as string | null };
  }
  return { channel: v.channel, enabled: v.enabled, dryRun: v.dryRun, summarySchedule: v.summarySchedule, timeZone: v.timeZone,
    techGroupExternalId: v.techGroupExternalId as string | null, customerGroups, configVersion: v.configVersion, status };
}
export function parseBotAi(v: unknown): BotAiView | null {
  return botObject(v) && botUrl(v.baseUrl) && text(v.model, 200, true) && typeof v.apiKeyConfigured === "boolean" && botVersion(v.version)
    ? { baseUrl: v.baseUrl, model: v.model, apiKeyConfigured: v.apiKeyConfigured, version: v.version } : null;
}
export function parseBotAdmin(v: unknown): BotAdminView | null {
  if (!botObject(v) || v.enabled !== true) return null;
  const channels = list(v.channels, 2, parseBotChannel), ai = parseBotAi(v.ai);
  return channels?.length === 2 && new Set(channels.map(c => c.channel)).size === 2 && ai ? { enabled: true, channels, ai } : null;
}
export function parseBotQr(v: unknown): BotQr | null {
  return botObject(v) && v.contentType === "image/png" && typeof v.base64 === "string" && v.base64.length <= 87384
    && /^iVBORw0KGgo[A-Za-z0-9+/]*={0,2}$/.test(v.base64) && date(v.expiresAt) && Date.parse(v.expiresAt) > Date.now()
    && Date.parse(v.expiresAt) <= Date.now() + 125000 ? { contentType: v.contentType, base64: v.base64, expiresAt: v.expiresAt } : null;
}
export function parseBotCommand(v: unknown) {
  return botObject(v) && text(v.id, 36) && ["summarize-now", "relogin"].includes(String(v.kind)) && date(v.createdAt) && date(v.expiresAt)
    ? { id: v.id, kind: String(v.kind), createdAt: v.createdAt, expiresAt: v.expiresAt } : null;
}
export function parseBotAudit(v: unknown): BotAudit[] | null {
  return list(v, 100, raw => botObject(raw) && text(raw.id, 36) && (raw.channel === null || botChannel(raw.channel)) && text(raw.actorUserId, 36)
    && text(raw.field, 64) && nullable(raw.before, 4000) && nullable(raw.after, 4000) && date(raw.timestamp)
    ? { id: raw.id, channel: raw.channel, actorUserId: raw.actorUserId, field: raw.field, before: raw.before as string | null, after: raw.after as string | null, timestamp: raw.timestamp } : null);
}
export function validBotMutation(v: unknown, path: string): boolean {
  if (!botObject(v)) return false;
  if (path.endsWith("/commands")) return keys(v, ["kind"]) && ["summarize-now", "relogin"].includes(String(v.kind));
  if (path === "ai") return keys(v, ["expectedVersion", "baseUrl", "model", "apiKey", "clearApiKey"]) && botVersion(v.expectedVersion)
    && botUrl(v.baseUrl) && text(v.model, 200) && (v.apiKey === undefined || v.apiKey === null || text(v.apiKey, 4096))
    && (v.clearApiKey === undefined || typeof v.clearApiKey === "boolean") && !(v.clearApiKey && v.apiKey != null);
  return botChannel(path) && keys(v, ["expectedVersion", "enabled", "dryRun", "summarySchedule", "techGroupExternalId", "customerGroups"])
    && botVersion(v.expectedVersion) && typeof v.enabled === "boolean" && typeof v.dryRun === "boolean" && botSchedule(v.summarySchedule)
    && nullable(v.techGroupExternalId, 200) && list(v.customerGroups, 100, customer) !== null;
}
