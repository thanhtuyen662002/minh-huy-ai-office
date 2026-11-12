/** AI spend (Kết nối → Chi phí AI): selling prices per model, billed customers and the monthly report. Amounts are VND. */

export type AiPrice = { model: string; inputPerMillion: number; cachedInputPerMillion: number; outputPerMillion: number; effectiveFromUtc: string };
export type AiAccount = { id: string; name: string; monthlyBudget: number | null; questionsPerMinute: number | null; version: string; sourceIds: string[] };
export type AiUsageSource = { id: string; logicalName: string };
export type AiUsageSettings = { prices: AiPrice[]; accounts: AiAccount[]; sources: AiUsageSource[]; userQuestionsPerMinute: number };
export type AiUsageTotals = {
  questions: number; calls: number; inputTokens: number; cachedInputTokens: number; outputTokens: number; cost: number; unpricedCalls: number;
};
export type AiUsageReport = {
  month: string; currency: string; totals: AiUsageTotals;
  accounts: { accountId: string | null; name: string; monthlyBudget: number | null; totals: AiUsageTotals }[];
  users: { userId: string | null; name: string; totals: AiUsageTotals }[];
  models: { providerId: string; model: string; totals: AiUsageTotals }[];
  days: { date: string; totals: AiUsageTotals }[];
};
export type AiPriceSave = { model: string; inputPerMillion: number; cachedInputPerMillion: number; outputPerMillion: number };
export type AiAccountSave = { name: string; monthlyBudget: number | null; questionsPerMinute: number | null; sourceIds: string[]; version: string | null };

const guid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const record = (value: unknown): value is Record<string, unknown> => !!value && typeof value === "object" && !Array.isArray(value);
const amount = (value: unknown, maximum: number): value is number =>
  typeof value === "number" && Number.isFinite(value) && value >= 0 && value <= maximum;
const count = (value: unknown): value is number => typeof value === "number" && Number.isSafeInteger(value) && value >= 0;
const text = (value: unknown, maximum: number): value is string =>
  typeof value === "string" && value.trim().length > 0 && value.length <= maximum && !/[\u0000-\u001f\u007f]/.test(value);
const exactKeys = (value: Record<string, unknown>, keys: string[]) =>
  Object.keys(value).length === keys.length && keys.every(key => Object.hasOwn(value, key));
const fourDecimals = (value: number) => Math.round(value * 10000) === value * 10000;

export function isAiPriceSave(value: unknown): value is AiPriceSave {
  return record(value) && exactKeys(value, ["model", "inputPerMillion", "cachedInputPerMillion", "outputPerMillion"])
    && text(value.model, 200)
    && [value.inputPerMillion, value.cachedInputPerMillion, value.outputPerMillion].every(item => amount(item, 1e9) && fourDecimals(item as number));
}

export function isAiAccountSave(value: unknown): value is AiAccountSave {
  return record(value) && exactKeys(value, ["name", "monthlyBudget", "questionsPerMinute", "sourceIds", "version"])
    && text(value.name, 200)
    && (value.monthlyBudget === null || amount(value.monthlyBudget, 1e12))
    && (value.questionsPerMinute === null || (Number.isInteger(value.questionsPerMinute) && (value.questionsPerMinute as number) >= 1 && (value.questionsPerMinute as number) <= 10000))
    && Array.isArray(value.sourceIds) && value.sourceIds.length <= 1000 && value.sourceIds.every(id => typeof id === "string" && guid.test(id))
    && new Set(value.sourceIds).size === value.sourceIds.length
    && (value.version === null || (typeof value.version === "string" && /^[1-9][0-9]{0,18}$/.test(value.version)));
}

function totals(value: unknown): AiUsageTotals | null {
  if (!record(value)) return null;
  const { questions, calls, inputTokens, cachedInputTokens, outputTokens, cost, unpricedCalls } = value;
  return [questions, calls, inputTokens, cachedInputTokens, outputTokens, unpricedCalls].every(count) && amount(cost, 1e15)
    ? { questions, calls, inputTokens, cachedInputTokens, outputTokens, cost, unpricedCalls } as AiUsageTotals : null;
}

export function parseAiUsageSettings(value: unknown): AiUsageSettings | null {
  if (!record(value) || !Array.isArray(value.prices) || !Array.isArray(value.accounts) || !Array.isArray(value.sources)
    || !count(value.userQuestionsPerMinute)) return null;
  const prices = value.prices.map(row => record(row) && text(row.model, 200) && amount(row.inputPerMillion, 1e9)
    && amount(row.cachedInputPerMillion, 1e9) && amount(row.outputPerMillion, 1e9) && typeof row.effectiveFromUtc === "string"
    ? row as AiPrice : null);
  const accounts = value.accounts.map(row => record(row) && typeof row.id === "string" && guid.test(row.id) && text(row.name, 200)
    && (row.monthlyBudget === null || amount(row.monthlyBudget, 1e12)) && (row.questionsPerMinute === null || count(row.questionsPerMinute))
    && typeof row.version === "string" && Array.isArray(row.sourceIds) && row.sourceIds.every(id => typeof id === "string" && guid.test(id))
    ? row as AiAccount : null);
  const sources = value.sources.map(row => record(row) && typeof row.id === "string" && guid.test(row.id) && text(row.logicalName, 200)
    ? { id: row.id, logicalName: row.logicalName } : null);
  if ([...prices, ...accounts, ...sources].some(row => row === null)) return null;
  return { prices: prices as AiPrice[], accounts: accounts as AiAccount[], sources: sources as AiUsageSource[],
    userQuestionsPerMinute: value.userQuestionsPerMinute };
}

export function parseAiUsageReport(value: unknown): AiUsageReport | null {
  if (!record(value) || typeof value.month !== "string" || typeof value.currency !== "string") return null;
  const total = totals(value.totals);
  const rows = (list: unknown, read: (row: Record<string, unknown>, t: AiUsageTotals) => unknown) =>
    Array.isArray(list) ? list.map(row => { const t = record(row) ? totals(row.totals) : null; return t && record(row) ? read(row, t) : null; }) : null;
  const accounts = rows(value.accounts, (row, t) => (row.accountId === null || (typeof row.accountId === "string" && guid.test(row.accountId)))
    && text(row.name, 200) && (row.monthlyBudget === null || amount(row.monthlyBudget, 1e12))
    ? { accountId: row.accountId, name: row.name, monthlyBudget: row.monthlyBudget, totals: t } : null);
  const users = rows(value.users, (row, t) => (row.userId === null || typeof row.userId === "string") && text(row.name, 400)
    ? { userId: row.userId, name: row.name, totals: t } : null);
  const models = rows(value.models, (row, t) => text(row.providerId, 100) && text(row.model, 200)
    ? { providerId: row.providerId, model: row.model, totals: t } : null);
  const days = rows(value.days, (row, t) => typeof row.date === "string" && /^\d{4}-\d{2}-\d{2}$/.test(row.date) ? { date: row.date, totals: t } : null);
  if (!total || !accounts || !users || !models || !days || [...accounts, ...users, ...models, ...days].some(row => row === null)) return null;
  return { month: value.month, currency: value.currency, totals: total, accounts, users, models, days } as AiUsageReport;
}

const money = new Intl.NumberFormat("vi-VN", { maximumFractionDigits: 0 });
const tokens = new Intl.NumberFormat("vi-VN");
export const formatMoney = (value: number) => `${money.format(value)} đ`;
export const formatTokens = (value: number) => tokens.format(value);

/** The current month in Vietnam time as YYYY-MM. */
export function currentMonth(now = new Date()) {
  const local = new Date(now.getTime() + 7 * 3600_000);
  return `${local.getUTCFullYear()}-${String(local.getUTCMonth() + 1).padStart(2, "0")}`;
}
