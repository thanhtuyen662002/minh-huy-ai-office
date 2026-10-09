import { isCanonicalCompanyId as guid } from "./company-scope";

export type SubmissionInput = Readonly<{ operationId: string; dataSourceId: string; question: string }>;
export type SubmissionReceipt = Readonly<{ companyId: string; operationId: string; dataSourceId: string; inputFingerprint: string;
  taskId: string; stepId: string; messageId: string; status: number; dispatchState: number; createdAtUtc: string }>;
export type SubmissionIntent = Readonly<{ companyId: string; operationId: string; state: 0 | 1 | 2 | 3; dataSourceId: string | null;
  question: string | null; inputFingerprint: string | null; createdAtUtc: string | null; expiresAtUtc: string | null; accepted: SubmissionReceipt | null }>;
export type SubmissionPage = Readonly<{ companyId: string; items: readonly SubmissionIntent[]; offset: number; limit: number; hasMore: boolean }>;

function object(value: unknown, fields: readonly string[]): value is Record<string, unknown> {
  if (!value || typeof value !== "object" || Array.isArray(value)) return false;
  const keys = Reflect.ownKeys(value);
  return keys.length === fields.length && keys.every(key => typeof key === "string" && fields.includes(key)
    && "value" in Object.getOwnPropertyDescriptor(value, key)!);
}
/** Match Core's Char.IsWhiteSpace/String.Trim, preserving scalar U+FEFF content. */
export const trimSubmissionQuestion = (value: string): string => value.replace(/^\p{White_Space}+|\p{White_Space}+$/gu, "");
export function submissionQuestion(value: unknown): value is string {
  if (typeof value !== "string" || !value || trimSubmissionQuestion(value) !== value || value.length > 4000) return false;
  for (let index = 0; index < value.length; index++) {
    const code = value.charCodeAt(index);
    if (code >= 0xd800 && code <= 0xdbff) {
      const low = value.charCodeAt(++index); if (!(low >= 0xdc00 && low <= 0xdfff)) return false;
    } else if (code >= 0xdc00 && code <= 0xdfff || code < 32 || code >= 127 && code <= 159) return false;
  }
  return true;
}
export const submissionFingerprintIsCanonical = (value: unknown): value is string => typeof value === "string" && /^[0-9A-F]{64}$/.test(value);
const sameGuid = (left: string, right: string) => left.toLowerCase() === right.toLowerCase();
const integer = (value: unknown): value is number => typeof value === "number" && Number.isSafeInteger(value);
function date(value: unknown): value is string {
  if (typeof value !== "string" || value.length > 40) return false;
  const match = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.\d{1,7})?(Z|([+-])(\d{2}):(\d{2}))$/.exec(value);
  if (!match) return false;
  const [year, month, day, hour, minute, second] = match.slice(1, 7).map(Number);
  return year > 0 && month >= 1 && month <= 12 && day >= 1 && day <= new Date(Date.UTC(year, month, 0)).getUTCDate()
    && hour <= 23 && minute <= 59 && second <= 59 && (match[7] === "Z" || Number(match[9]) <= 14
      && Number(match[10]) <= 59 && (Number(match[9]) < 14 || Number(match[10]) === 0)) && Number.isFinite(Date.parse(value));
}

/** JSON syntax plus decoded duplicate-key and depth checks at every object. */
export function parseSubmissionJson(source: string): unknown {
  const stack: (Set<string> | null)[] = [];
  for (let index = 0; index < source.length; index++) {
    const character = source[index];
    if (character === "{" || character === "[") {
      stack.push(character === "{" ? new Set() : null);
      if (stack.length > 6) throw new Error("Submission JSON is too deep");
      continue;
    }
    if (character === "}" || character === "]") { stack.pop(); continue; }
    if (character !== '"') continue;
    const start = index++;
    for (; index < source.length; index++) {
      if (source[index] === "\\") { index++; continue; }
      if (source[index] === '"') break;
    }
    let next = index + 1; while (next < source.length && /[ \t\r\n]/.test(source[next])) next++;
    const keys = stack.at(-1);
    if (!keys || source[next] !== ":") continue;
    const key: string = JSON.parse(source.slice(start, index + 1));
    if (keys.has(key)) throw new Error("Duplicate submission field");
    keys.add(key);
  }
  return JSON.parse(source);
}

export function parseSubmissionInput(value: unknown): SubmissionInput | null {
  if (!object(value, ["operationId", "dataSourceId", "question"]) || !guid(value.operationId)
    || !guid(value.dataSourceId) || !submissionQuestion(value.question)) return null;
  return Object.freeze({ operationId: value.operationId.toLowerCase(), dataSourceId: value.dataSourceId.toLowerCase(), question: value.question });
}

export async function submissionFingerprint(input: SubmissionInput): Promise<string> {
  if (!parseSubmissionInput(input)) throw new Error("Invalid submission input");
  const encoder = new TextEncoder();
  const prefix = encoder.encode("aioffice-task-intent-v1\0" + input.dataSourceId.toLowerCase().replaceAll("-", ""));
  const question = encoder.encode(input.question);
  const bytes = new Uint8Array(prefix.length + 8 + question.length); bytes.set(prefix);
  const view = new DataView(bytes.buffer); view.setUint32(prefix.length, 3, true); view.setUint32(prefix.length + 4, question.length, true);
  bytes.set(question, prefix.length + 8);
  const digest = new Uint8Array(await crypto.subtle.digest("SHA-256", bytes));
  return Array.from(digest, byte => byte.toString(16).padStart(2, "0")).join("").toUpperCase();
}

export function parseSubmissionReceipt(value: unknown): SubmissionReceipt | null {
  if (!object(value, ["companyId", "operationId", "dataSourceId", "inputFingerprint", "taskId", "stepId", "messageId", "status", "dispatchState", "createdAtUtc"])
    || !guid(value.companyId) || !guid(value.operationId) || !guid(value.dataSourceId) || !guid(value.taskId) || !guid(value.stepId) || !guid(value.messageId)
    || !submissionFingerprintIsCanonical(value.inputFingerprint) || !integer(value.status) || value.status < 0 || value.status > 6
    || !integer(value.dispatchState) || value.dispatchState < 0 || value.dispatchState > 3 || !date(value.createdAtUtc)) return null;
  return Object.freeze({ ...value }) as SubmissionReceipt;
}

export function receiptMatches(receipt: SubmissionReceipt, companyId: string, input: SubmissionInput, fingerprint: string,
  previous?: SubmissionReceipt | null): boolean {
  return sameGuid(receipt.companyId, companyId) && sameGuid(receipt.operationId, input.operationId)
    && sameGuid(receipt.dataSourceId, input.dataSourceId) && receipt.inputFingerprint === fingerprint
    && (!previous || sameGuid(receipt.taskId, previous.taskId) && sameGuid(receipt.stepId, previous.stepId)
      && sameGuid(receipt.messageId, previous.messageId) && receipt.createdAtUtc === previous.createdAtUtc);
}

export function parseSubmissionIntent(value: unknown): SubmissionIntent | null {
  if (!object(value, ["companyId", "operationId", "state", "dataSourceId", "question", "inputFingerprint", "createdAtUtc", "expiresAtUtc", "accepted"])
    || !guid(value.companyId) || !guid(value.operationId) || !integer(value.state) || value.state < 0 || value.state > 3) return null;
  if (value.state === 3) {
    if ([value.dataSourceId, value.question, value.inputFingerprint, value.createdAtUtc, value.expiresAtUtc, value.accepted].some(item => item !== null)) return null;
  } else {
    if (!guid(value.dataSourceId) || !submissionQuestion(value.question) || !submissionFingerprintIsCanonical(value.inputFingerprint)
      || !date(value.createdAtUtc) || !date(value.expiresAtUtc) || Date.parse(value.expiresAtUtc) - Date.parse(value.createdAtUtc) !== 86400000) return null;
    const accepted = value.accepted === null ? null : parseSubmissionReceipt(value.accepted);
    if (value.state === 1 ? !accepted || !receiptMatches(accepted, value.companyId,
      { operationId: value.operationId, dataSourceId: value.dataSourceId, question: value.question }, value.inputFingerprint) : value.accepted !== null) return null;
    return Object.freeze({ ...value, accepted }) as SubmissionIntent;
  }
  return Object.freeze({ ...value }) as SubmissionIntent;
}

export async function verifiedSubmissionIntent(value: unknown): Promise<SubmissionIntent | null> {
  const intent = parseSubmissionIntent(value);
  if (!intent || intent.state === 3) return intent;
  return await submissionFingerprint({ operationId: intent.operationId, dataSourceId: intent.dataSourceId!, question: intent.question! }) === intent.inputFingerprint ? intent : null;
}

export async function verifiedSubmissionPage(value: unknown): Promise<SubmissionPage | null> {
  if (!object(value, ["companyId", "items", "offset", "limit", "hasMore"]) || !guid(value.companyId)
    || !integer(value.offset) || value.offset < 0 || value.offset > 10000 || !integer(value.limit) || value.limit < 1 || value.limit > 25
    || !Array.isArray(value.items) || value.items.length > value.limit || typeof value.hasMore !== "boolean"
    || value.hasMore && value.items.length !== value.limit) return null;
  const items = await Promise.all(value.items.map(verifiedSubmissionIntent));
  if (items.some(item => !item || !sameGuid(item.companyId, value.companyId as string))
    || new Set(items.map(item => item!.operationId.toLowerCase())).size !== items.length) return null;
  return Object.freeze({ companyId: value.companyId, offset: value.offset, limit: value.limit, hasMore: value.hasMore,
    items: Object.freeze(items as SubmissionIntent[]) });
}
