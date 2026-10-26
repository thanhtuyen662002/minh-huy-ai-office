export type InvitationRole = "admin" | "viewer";
export type CompanyInvitationInput = { email: string; roles: InvitationRole[] };
export type CompanyInvitation = { companyId: string; id: string; email: string; roles: InvitationRole[];
  createdAtUtc: string; expiresAtUtc: string; acceptedAtUtc: string | null; revokedAtUtc: string | null; version: string };
export type CompanyInvitationPage = { companyId: string; items: CompanyInvitation[]; offset: number; limit: number; hasMore: boolean };
export type CreatedCompanyInvitation = CompanyInvitation & { temporaryPassword?: string };

const guid = (value: unknown): value is string => typeof value === "string" && value !== "00000000-0000-0000-0000-000000000000"
  && /^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/.test(value);
const version = (value: unknown): value is string => typeof value === "string" && /^[1-9][0-9]{0,18}$/.test(value)
  && BigInt(value) <= 9223372036854775807n;
const date = (value: unknown): value is string => typeof value === "string" && value.length <= 40
  && /^\d{4}-\d{2}-\d{2}T/.test(value) && Number.isFinite(Date.parse(value));
const exactKeys = (value: object, names: string[]) => Object.keys(value).length === names.length
  && Object.keys(value).every(key => names.includes(key));

export function parseInvitationJson(source: string): unknown {
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
    if (seen.has(key)) throw new Error("Duplicate invitation field");
    seen.add(key);
  }
  return value;
}

export function isInvitationInput(value: unknown): value is CompanyInvitationInput {
  if (!value || typeof value !== "object" || Array.isArray(value) || !exactKeys(value, ["email", "roles"])) return false;
  const input = value as CompanyInvitationInput;
  return typeof input.email === "string" && input.email.length > 2 && input.email.length <= 254
    && input.email === input.email.trim() && /^[\x21-\x7e]+@[\x21-\x7e]+$/.test(input.email)
    && Array.isArray(input.roles) && input.roles.length >= 1 && input.roles.length <= 2
    && input.roles.every(role => role === "admin" || role === "viewer") && new Set(input.roles).size === input.roles.length;
}

export function parseInvitation(value: unknown): CompanyInvitation | null {
  const names = ["companyId", "id", "email", "roles", "createdAtUtc", "expiresAtUtc", "acceptedAtUtc", "revokedAtUtc", "version"];
  if (!value || typeof value !== "object" || Array.isArray(value) || !exactKeys(value, names)) return null;
  const item = value as CompanyInvitation;
  if (!guid(item.companyId) || !guid(item.id) || !isInvitationInput({ email: item.email, roles: item.roles })
    || !date(item.createdAtUtc) || !date(item.expiresAtUtc) || item.acceptedAtUtc !== null && !date(item.acceptedAtUtc)
    || item.revokedAtUtc !== null && !date(item.revokedAtUtc) || !version(item.version)) return null;
  return { ...item, roles: [...item.roles] };
}

export function parseInvitationPage(value: unknown): CompanyInvitationPage | null {
  if (!value || typeof value !== "object" || Array.isArray(value)
    || !exactKeys(value, ["companyId", "items", "offset", "limit", "hasMore"])) return null;
  const page = value as CompanyInvitationPage;
  if (!guid(page.companyId) || !Array.isArray(page.items) || !Number.isInteger(page.offset) || page.offset < 0 || page.offset > 1000
    || !Number.isInteger(page.limit) || page.limit < 1 || page.limit > 100 || typeof page.hasMore !== "boolean") return null;
  const items = page.items.map(parseInvitation);
  return items.every((item): item is CompanyInvitation => !!item) ? { ...page, items } : null;
}

// Only the POST response may carry a credential; list/revoke remain strict.
export function parseCreatedInvitation(value: unknown): CreatedCompanyInvitation | null {
  if (!value || typeof value !== "object" || Array.isArray(value)) return null;
  const { temporaryPassword, ...invitation } = value as Record<string, unknown>;
  const parsed = parseInvitation(invitation);
  if (!parsed || temporaryPassword !== undefined && (typeof temporaryPassword !== "string"
    || temporaryPassword.length < 14 || temporaryPassword.length > 128 || !/^[\x21-\x7e]+$/.test(temporaryPassword))) return null;
  return temporaryPassword === undefined ? parsed : { ...parsed, temporaryPassword: temporaryPassword as string };
}
