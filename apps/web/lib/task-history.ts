import { isCanonicalCompanyId } from "./company-scope";

export type TaskHistoryItem = Readonly<{ taskId: string; status: number | null; createdAtUtc: string;
  updatedAtUtc: string; summary: string | null; metadataUnavailable: boolean }>;
export type TaskHistoryPage = Readonly<{ companyId: string; items: readonly TaskHistoryItem[]; offset: number; limit: number; hasMore: boolean }>;
export type TaskHistoryResult = Readonly<{ kind: "answer" | "connection"; answer: string | null; evidence: string;
  provider: string | null; model: string | null; inputTokens: number | null; outputTokens: number | null; totalTokens: number | null }>;
export type TaskHistoryDetail = Readonly<{ companyId: string; task: TaskHistoryItem; result: TaskHistoryResult | null; resultUnavailable: boolean }>;

function object(value: unknown, fields: readonly string[]): value is Record<string, unknown> {
  if (!value || typeof value !== "object" || Array.isArray(value)) return false;
  const keys = Reflect.ownKeys(value);
  return keys.length === fields.length && keys.every(key => typeof key === "string" && fields.includes(key)
    && !!Object.getOwnPropertyDescriptor(value, key) && "value" in Object.getOwnPropertyDescriptor(value, key)!);
}
function text(value: unknown, maximum: number, lines = false): value is string {
  if (typeof value !== "string" || !value.trim() || value.length > maximum || (!lines && value.trim() !== value)) return false;
  for (let index = 0; index < value.length; index++) {
    const code = value.charCodeAt(index);
    if (code >= 0xd800 && code <= 0xdbff) {
      const next = value.charCodeAt(++index); if (!(next >= 0xdc00 && next <= 0xdfff)) return false;
    } else if (code >= 0xdc00 && code <= 0xdfff) return false;
    else if ((code < 32 || code >= 127 && code <= 159) && !(lines && [9, 10, 13].includes(code))) return false;
  }
  return true;
}
const integer = (value: unknown): value is number => typeof value === "number" && Number.isSafeInteger(value);
const date = (value: unknown): value is string => typeof value === "string" && value.length <= 40
  && /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?(Z|[+-]\d{2}:\d{2})$/.test(value) && Number.isFinite(Date.parse(value));

function item(value: unknown): TaskHistoryItem | null {
  if (!object(value, ["taskId", "status", "createdAtUtc", "updatedAtUtc", "summary", "metadataUnavailable"])
    || !isCanonicalCompanyId(value.taskId) || typeof value.metadataUnavailable !== "boolean"
    || !(value.status === null ? value.metadataUnavailable : integer(value.status) && value.status >= 0 && value.status <= 6)
    || !date(value.createdAtUtc) || !date(value.updatedAtUtc)
    || !(value.summary === null ? value.metadataUnavailable : text(value.summary, 241))) return null;
  if (!value.metadataUnavailable && Date.parse(value.updatedAtUtc) < Date.parse(value.createdAtUtc)) return null;
  return Object.freeze({ ...value }) as TaskHistoryItem;
}

export function parseTaskHistoryPage(value: unknown): TaskHistoryPage | null {
  if (!object(value, ["companyId", "items", "offset", "limit", "hasMore"]) || !isCanonicalCompanyId(value.companyId)
    || !integer(value.offset) || value.offset < 0 || value.offset > 10000 || !integer(value.limit) || value.limit < 1 || value.limit > 100
    || typeof value.hasMore !== "boolean" || !Array.isArray(value.items) || value.items.length > value.limit
    || value.hasMore && value.items.length !== value.limit) return null;
  const items = value.items.map(item);
  if (items.some(item => item === null) || new Set(items.map(item => item!.taskId.toLowerCase())).size !== items.length) return null;
  return Object.freeze({ companyId: value.companyId, offset: value.offset, limit: value.limit, hasMore: value.hasMore,
    items: Object.freeze(items as TaskHistoryItem[]) });
}

function result(value: unknown): TaskHistoryResult | null {
  if (!object(value, ["kind", "answer", "evidence", "provider", "model", "inputTokens", "outputTokens", "totalTokens"])) return null;
  if (value.kind === "connection") {
    if (value.evidence !== "read-only-connection-probe" || [value.answer, value.provider, value.model, value.inputTokens, value.outputTokens, value.totalTokens].some(item => item !== null)) return null;
  } else if (value.kind === "answer") {
    if (!text(value.answer, 64000, true) || !text(value.provider, 200) || !text(value.model, 200)
      || value.evidence !== "ai-provider-reasoning-after-bounded-read-only-erp-catalog"
      || !integer(value.inputTokens) || value.inputTokens < 0 || !integer(value.outputTokens) || value.outputTokens < 0
      || !integer(value.totalTokens) || value.totalTokens > 2147483647 || value.inputTokens + value.outputTokens !== value.totalTokens) return null;
  } else return null;
  return Object.freeze({ ...value }) as TaskHistoryResult;
}

export function parseTaskHistoryDetail(value: unknown): TaskHistoryDetail | null {
  if (!object(value, ["companyId", "task", "result", "resultUnavailable"]) || !isCanonicalCompanyId(value.companyId)
    || typeof value.resultUnavailable !== "boolean") return null;
  const task = item(value.task), projected = value.result === null ? null : result(value.result);
  if (!task || value.result !== null && !projected
    || (task.status === 6 ? value.resultUnavailable !== (projected === null) : projected !== null || value.resultUnavailable)
    || task.metadataUnavailable && projected !== null) return null;
  return Object.freeze({ companyId: value.companyId, task, result: projected, resultUnavailable: value.resultUnavailable });
}

export function taskHistoryStatus(status: number | null) {
  return status === null ? "Trạng thái chưa xác định" : ["Chờ xử lý", "Đang xử lý", "Cần bổ sung thông tin", "Cần cấp quyền", "Tạm dừng", "Thất bại", "Hoàn thành"][status];
}
