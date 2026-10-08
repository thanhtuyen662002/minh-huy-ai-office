export type CompanyMember = { userId: string; displayName: string; userActive: boolean; membershipActive: boolean; roles: readonly string[]; membershipVersion?: string };
export type MemberAccessInput = { operationId: string; expectedVersion: string; isActive: boolean };
export type MemberAccessResult = { companyId: string; userId: string; membershipActive: boolean; membershipVersion: string; operationId: string };
export type MemberPage = { companyId: string; items: readonly CompanyMember[]; offset: number; limit: number; hasMore: boolean };
const guid = (value: unknown): value is string => typeof value === "string" && value !== "00000000-0000-0000-0000-000000000000"
  && /^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/.test(value);
const keys = (value: object, expected: string[]) => Object.keys(value).length === expected.length && Object.keys(value).every(key => expected.includes(key));
const version = (value: unknown): value is string => typeof value === "string" && /^[1-9][0-9]{0,18}$/.test(value) && BigInt(value) <= 9223372036854775807n;

export function isMemberAccessInput(value: unknown): value is MemberAccessInput {
  if (!value || typeof value !== "object" || Array.isArray(value) || !keys(value, ["operationId", "expectedVersion", "isActive"])) return false;
  const input = value as MemberAccessInput;
  return guid(input.operationId) && version(input.expectedVersion) && typeof input.isActive === "boolean";
}
export function parseMemberAccessResult(value: unknown): MemberAccessResult | null {
  if (!value || typeof value !== "object" || Array.isArray(value) || !keys(value, ["companyId", "userId", "membershipActive", "membershipVersion", "operationId"])) return null;
  const result = value as MemberAccessResult;
  return guid(result.companyId) && guid(result.userId) && guid(result.operationId) && version(result.membershipVersion) && typeof result.membershipActive === "boolean" ? result : null;
}

export function parseMemberPage(value: unknown): MemberPage | null {
  if (!value || typeof value !== "object" || Array.isArray(value) || !keys(value, ["companyId", "items", "offset", "limit", "hasMore"])) return null;
  const page = value as MemberPage;
  if (!guid(page.companyId) || !Number.isInteger(page.offset) || page.offset < 0 || page.offset > 1000 || !Number.isInteger(page.limit)
    || page.limit < 1 || page.limit > 100 || typeof page.hasMore !== "boolean" || !Array.isArray(page.items) || page.items.length > page.limit) return null;
  const seen = new Set<string>();
  for (const member of page.items) {
    if (!member || typeof member !== "object" || Array.isArray(member)
      || !(keys(member, ["userId", "displayName", "userActive", "membershipActive", "roles"]) ||
        keys(member, ["userId", "displayName", "userActive", "membershipActive", "roles", "membershipVersion"]) && version(member.membershipVersion))
      || !guid(member.userId) || seen.has(member.userId.toLowerCase()) || typeof member.displayName !== "string" || member.displayName.length < 1
      || member.displayName.length > 200 || member.displayName.trim() !== member.displayName || typeof member.userActive !== "boolean"
      || typeof member.membershipActive !== "boolean" || !Array.isArray(member.roles) || member.roles.length > 256
      || new Set(member.roles).size !== member.roles.length || member.roles.some((role: unknown) => typeof role !== "string" || !role.length || role.length > 100 || role.trim() !== role)) return null;
    seen.add(member.userId.toLowerCase());
  }
  if (page.hasMore && page.items.length !== page.limit) return null;
  return page;
}
