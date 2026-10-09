export type AdministratorInput = { operationId: string; expectedVersion: string; isAdministrator: boolean };
export type AdministratorResult = { companyId: string; userId: string; isAdministrator: boolean; membershipVersion: string; operationId: string };
const guid = (value: unknown): value is string => typeof value === "string" && value !== "00000000-0000-0000-0000-000000000000"
  && /^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/.test(value);
const version = (value: unknown): value is string => typeof value === "string" && /^[1-9][0-9]{0,18}$/.test(value) && BigInt(value) <= 9223372036854775807n;
const keys = (value: object, names: string[]) => Object.keys(value).length === names.length && Object.keys(value).every(key => names.includes(key));

/** Decode before comparing property names: escaped duplicate keys are duplicates too. */
export function parseAdministratorJson(source: string): unknown {
  const value: unknown = JSON.parse(source);
  let depth = 0; const seen = new Set<string>();
  for (let index = 0; index < source.length; index++) {
    const character = source[index];
    if (character === "{" || character === "[") { depth++; continue; }
    if (character === "}" || character === "]") { depth--; continue; }
    if (character !== '"') continue;
    const start = index++;
    for (; index < source.length; index++) {
      if (source[index] === "\\") { index++; continue; }
      if (source[index] === '"') break;
    }
    let next = index + 1; while (/\s/.test(source[next] ?? "") && next < source.length) next++;
    if (depth !== 1 || source[next] !== ":") continue;
    const key: string = JSON.parse(source.slice(start, index + 1));
    if (seen.has(key)) throw new Error("Duplicate administrator field");
    seen.add(key);
  }
  return value;
}

export function isAdministratorInput(value: unknown): value is AdministratorInput {
  if (!value || typeof value !== "object" || Array.isArray(value) || !keys(value, ["operationId", "expectedVersion", "isAdministrator"])) return false;
  const input = value as AdministratorInput;
  return guid(input.operationId) && version(input.expectedVersion) && typeof input.isAdministrator === "boolean";
}
export function parseAdministratorResult(value: unknown): AdministratorResult | null {
  if (!value || typeof value !== "object" || Array.isArray(value) || !keys(value, ["companyId", "userId", "isAdministrator", "membershipVersion", "operationId"])) return null;
  const result = value as AdministratorResult;
  return guid(result.companyId) && guid(result.userId) && guid(result.operationId) && version(result.membershipVersion) && typeof result.isAdministrator === "boolean"
    ? { ...result } : null;
}
