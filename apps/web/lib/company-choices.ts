import { isCanonicalCompanyId } from "./company-scope";

export type CompanyChoice = Readonly<{ companyId: string; companyName: string }>;
const own = (value: object, key: PropertyKey) => Object.getOwnPropertyDescriptor(value, key)?.value;

export function parseCompanyChoices(value: unknown): readonly CompanyChoice[] | null {
  try {
    if (!value || typeof value !== "object" || Array.isArray(value) || Object.keys(value).join(",") !== "items") return null;
    const items = own(value, "items");
    if (!Array.isArray(items) || items.length > 100) return null;
    const result: CompanyChoice[] = [], seen = new Set<string>();
    for (let index = 0; index < items.length; index++) {
      const item = own(items, String(index));
      if (!item || typeof item !== "object" || Array.isArray(item)
        || Object.keys(item).sort().join(",") !== "companyId,companyName") return null;
      const companyId = own(item, "companyId"), companyName = own(item, "companyName");
      if (!isCanonicalCompanyId(companyId) || typeof companyName !== "string" || !companyName
        || companyName.length > 200 || companyName.trim() !== companyName || /[\u0000-\u001f\u007f-\u009f]/.test(companyName)
        || seen.has(companyId.toLowerCase())) return null;
      seen.add(companyId.toLowerCase()); result.push(Object.freeze({ companyId, companyName }));
    }
    return Object.freeze(result);
  } catch { return null; }
}
