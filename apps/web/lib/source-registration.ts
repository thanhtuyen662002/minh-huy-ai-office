export type RegistrationOption = Readonly<{ bindingId: string; label: string; versionToken: string }>;
export type RegistrationPage = Readonly<{ items: readonly RegistrationOption[]; offset: number; limit: number; hasMore: boolean }>;
export type RegistrationInput = {
  bindingId: string; bindingVersion: string; operationId: string;
  logicalName: string; environment: string; purpose: string; maxConcurrency: number;
};
const fields = ["bindingId", "bindingVersion", "operationId", "logicalName", "environment", "purpose", "maxConcurrency"];
const guid = (value: unknown): value is string => typeof value === "string"
  && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value)
  && value !== "00000000-0000-0000-0000-000000000000";
const token = (value: unknown): value is string => typeof value === "string"
  && /^[1-9][0-9]{0,18}$/.test(value) && BigInt(value) <= 9223372036854775807n;
const text = (value: unknown, max: number): value is string => typeof value === "string" && value.trim().length > 0 && value.trim().length <= max;
export function isRegistrationInput(value: unknown): value is RegistrationInput {
  if (!value || typeof value !== "object" || Array.isArray(value)) return false;
  const row = value as Record<string, unknown>;
  return Object.keys(row).length === fields.length && fields.every(key => Object.hasOwn(row, key))
    && guid(row.bindingId) && token(row.bindingVersion) && guid(row.operationId)
    && text(row.logicalName, 200) && text(row.environment, 50) && text(row.purpose, 200)
    && typeof row.maxConcurrency === "number" && Number.isInteger(row.maxConcurrency)
    && row.maxConcurrency >= 1 && row.maxConcurrency <= 1024;
}
export function parseRegistrationPage(value: unknown): RegistrationPage | null {
  if (!value || typeof value !== "object" || Array.isArray(value)) return null;
  const page = value as Record<string, unknown>;
  if (!Array.isArray(page.items) || page.items.length > 100 || !Number.isInteger(page.offset)
    || typeof page.offset !== "number" || page.offset < 0 || page.offset > 1000
    || !Number.isInteger(page.limit) || typeof page.limit !== "number" || page.limit < 1 || page.limit > 100
    || page.items.length > page.limit || typeof page.hasMore !== "boolean") return null;
  const items: RegistrationOption[] = [];
  for (const item of page.items) {
    if (!item || typeof item !== "object" || !guid(item.bindingId) || !token(item.versionToken)
      || !text(item.label, 128) || /secretref:\/\//i.test(item.label) || items.some(row => row.bindingId === item.bindingId)) return null;
    items.push({ bindingId: item.bindingId, label: item.label.trim(), versionToken: item.versionToken });
  }
  return { items, offset: page.offset, limit: page.limit, hasMore: page.hasMore };
}
