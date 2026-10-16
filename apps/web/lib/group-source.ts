import { isCanonicalCompanyId } from "./company-scope";

export type GroupScope = Readonly<{ tenantId: string; companyId: string; sourceBindingId: string }>;
export type GroupSource = Readonly<{ source: GroupScope; displayName: string; provider: string; version: number }>;
export type GroupSources = Readonly<{ companyId: string; items: readonly GroupSource[]; hasMore: boolean }>;
export type GroupMessageHead = Readonly<{ messageId: string; revision: number; lastChangedSequence: number;
  kind: 1 | 2 | 3 | 4; occurredAtUtc: string; isHistoricalBackfill: boolean }>;
export type GroupMessages = Readonly<{ source: GroupScope; items: readonly GroupMessageHead[];
  nextBeforeSequence: number | null; hasCoverageGap: boolean }>;
export type GroupMessage = Readonly<{ source: GroupScope; messageId: string; externalMessageId: string;
  revision: number; committedSequence: number; kind: 1 | 2 | 3 | 4; senderId: string; replyToMessageId: string | null;
  occurredAtUtc: string; text: string | null; isHistoricalBackfill: boolean; hasCoverageGap: boolean }>;

function object(value: unknown, fields: readonly string[]): value is Record<string, unknown> {
  if (!value || typeof value !== "object" || Array.isArray(value)) return false;
  const keys = Reflect.ownKeys(value);
  return keys.length === fields.length && keys.every(key => typeof key === "string" && fields.includes(key)
    && "value" in Object.getOwnPropertyDescriptor(value, key)!);
}
const guid = (value: unknown): value is string => isCanonicalCompanyId(value) && value === value.toLowerCase();
const positive = (value: unknown): value is number => typeof value === "number" && Number.isSafeInteger(value) && value > 0;
// Opaque provider IDs and source text retain spaces, FEFF and scalar replacement
// characters. JSON/React escape controls; never trim or repair original content.
function scalar(value: unknown, maximum: number, allowEmpty = false): value is string {
  if (typeof value !== "string" || value.length > maximum || !allowEmpty && value.length === 0) return false;
  for (let index = 0; index < value.length; index++) {
    const code = value.charCodeAt(index);
    if (code >= 0xd800 && code <= 0xdbff) {
      const low = value.charCodeAt(++index); if (!(low >= 0xdc00 && low <= 0xdfff)) return false;
    } else if (code >= 0xdc00 && code <= 0xdfff) return false;
  }
  return true;
}
function date(value: unknown): value is string {
  if (typeof value !== "string" || value.length > 40) return false;
  const match = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.\d{1,7})?(Z|([+-])(\d{2}):(\d{2}))$/.exec(value);
  if (!match) return false;
  const [year, month, day, hour, minute, second] = match.slice(1, 7).map(Number);
  const days = new Date(0); days.setUTCFullYear(year, month, 0);
  return year > 0 && month >= 1 && month <= 12 && day >= 1 && day <= days.getUTCDate()
    && hour <= 23 && minute <= 59 && second <= 59 && (match[7] === "Z" || Number(match[9]) <= 14
      && Number(match[10]) <= 59 && (Number(match[9]) < 14 || Number(match[10]) === 0)) && Number.isFinite(Date.parse(value));
}
const kind = (value: unknown): value is 1 | 2 | 3 | 4 => positive(value) && value <= 4;
function scope(value: unknown, companyId: string, sourceId?: string): GroupScope | null {
  if (!object(value, ["tenantId", "companyId", "sourceBindingId"]) || !guid(value.tenantId)
    || value.companyId !== companyId || !guid(value.sourceBindingId) || sourceId && value.sourceBindingId !== sourceId) return null;
  return Object.freeze({ ...value }) as GroupScope;
}
export function parseGroupSources(value: unknown, companyId: string, limit: number): GroupSources | null {
  if (!object(value, ["companyId", "items", "hasMore"]) || value.companyId !== companyId || typeof value.hasMore !== "boolean"
    || !Array.isArray(value.items) || value.items.length > limit || value.hasMore && value.items.length !== limit) return null;
  const items: GroupSource[] = [];
  for (const item of value.items) {
    if (!object(item, ["source", "displayName", "provider", "version"]) || !scalar(item.displayName, 200, true)
      || typeof item.provider !== "string" || !/^[a-z0-9-]{1,64}$/.test(item.provider) || !positive(item.version)) return null;
    const source = scope(item.source, companyId);
    if (!source || items.length && source.tenantId !== items[0].source.tenantId
      || items.some(item => item.source.sourceBindingId === source.sourceBindingId)) return null;
    items.push(Object.freeze({ source, displayName: item.displayName, provider: item.provider, version: item.version }));
  }
  return Object.freeze({ companyId, items: Object.freeze(items), hasMore: value.hasMore });
}
export function parseGroupMessages(value: unknown, companyId: string, sourceId: string, limit: number, before?: number): GroupMessages | null {
  if (!object(value, ["source", "items", "nextBeforeSequence", "hasCoverageGap"]) || typeof value.hasCoverageGap !== "boolean"
    || !Array.isArray(value.items) || value.items.length > limit) return null;
  const source = scope(value.source, companyId, sourceId); if (!source) return null;
  const items: GroupMessageHead[] = [];
  for (const item of value.items) {
    if (!object(item, ["messageId", "revision", "lastChangedSequence", "kind", "occurredAtUtc", "isHistoricalBackfill"])
      || !guid(item.messageId) || !positive(item.revision) || !positive(item.lastChangedSequence) || !kind(item.kind)
      || !date(item.occurredAtUtc) || typeof item.isHistoricalBackfill !== "boolean"
      || before !== undefined && item.lastChangedSequence >= before
      || items.length && item.lastChangedSequence >= items.at(-1)!.lastChangedSequence
      || items.some(existing => existing.messageId === item.messageId)) return null;
    items.push(Object.freeze({ ...item }) as GroupMessageHead);
  }
  if (value.nextBeforeSequence !== null && (!positive(value.nextBeforeSequence) || items.length !== limit
    || value.nextBeforeSequence !== items.at(-1)?.lastChangedSequence)) return null;
  return Object.freeze({ source, items: Object.freeze(items), nextBeforeSequence: value.nextBeforeSequence, hasCoverageGap: value.hasCoverageGap }) as GroupMessages;
}
export function parseGroupMessage(value: unknown, companyId: string, sourceId: string, messageId: string): GroupMessage | null {
  if (!object(value, ["source", "messageId", "externalMessageId", "revision", "committedSequence", "kind", "senderId", "replyToMessageId",
    "occurredAtUtc", "text", "isHistoricalBackfill", "hasCoverageGap"]) || value.messageId !== messageId
    || !scalar(value.externalMessageId, 256) || !scalar(value.senderId, 256) || !positive(value.revision) || !positive(value.committedSequence)
    || !kind(value.kind) || !date(value.occurredAtUtc) || typeof value.isHistoricalBackfill !== "boolean" || typeof value.hasCoverageGap !== "boolean"
    || value.replyToMessageId !== null && !scalar(value.replyToMessageId, 256)
    || (value.kind === 4 ? value.text !== null : !scalar(value.text, 8000, true))) return null;
  const source = scope(value.source, companyId, sourceId); if (!source) return null;
  return Object.freeze({ ...value, source }) as GroupMessage;
}
