export const COMPANY_SELECTOR_HEADER = "X-AIOffice-Company-Id";

export function isCanonicalCompanyId(value: unknown): value is string {
  return typeof value === "string" && value.trim() === value
    && value !== "00000000-0000-0000-0000-000000000000"
    && /^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/.test(value);
}
