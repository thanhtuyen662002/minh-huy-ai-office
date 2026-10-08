import { expect, it } from "vitest";
import { isMemberAccessInput, parseMemberAccessResult, parseMemberPage } from "./company-members";
const member = { userId: "33333333-3333-3333-3333-333333333333", displayName: "Owner", userActive: true, membershipActive: true, roles: ["admin"] };
const page = { companyId: "22222222-2222-2222-2222-222222222222", items: [member], offset: 0, limit: 25, hasMore: false };
it("accepts a bounded scoped page", () => expect(parseMemberPage(page)).toEqual(page));
it.each([
  { ...page, limit: 101 }, { ...page, offset: -1 }, { ...page, hasMore: true }, { ...page, companyId: "" },
  { ...page, items: [{ ...member, subject: "PRIVATE" }] }, { ...page, items: [member, member] },
  { ...page, items: [{ ...member, roles: ["admin", "admin"] }] }, { ...page, items: [{ ...member, roles: Array.from({ length: 257 }, (_, i) => "role" + i) }] },
  { ...page, items: [{ ...member, userActive: "true" }] }, { ...page, items: [{ ...member, displayName: "x".repeat(201) }] },
])("rejects malformed or overlarge member data %j", invalid => expect(parseMemberPage(invalid)).toBeNull());

const input = { operationId: "44444444-4444-4444-4444-444444444444", expectedVersion: "1", isActive: false };
const result = { companyId: page.companyId, userId: member.userId, operationId: input.operationId, membershipActive: false, membershipVersion: "2" };
it("accepts a directory version up to the exact SQL bigint limit", () => {
  expect(parseMemberPage({ ...page, items: [{ ...member, membershipVersion: "9223372036854775807" }] })).toBeTruthy();
  expect(isMemberAccessInput({ ...input, expectedVersion: "9223372036854775807" })).toBe(true);
  expect(parseMemberAccessResult(result)).toEqual(result);
});
it.each(["0", "01", "-1", "1.0", "9223372036854775808", "9".repeat(100), 1, null, undefined])("rejects noncanonical or out-of-range version %j everywhere", version => {
  expect(parseMemberPage({ ...page, items: [{ ...member, membershipVersion: version }] })).toBeNull();
  expect(isMemberAccessInput({ ...input, expectedVersion: version })).toBe(false);
  expect(parseMemberAccessResult({ ...result, membershipVersion: version })).toBeNull();
});
it.each([{ ...input, companyId: page.companyId }, { ...input, roles: ["admin"] }, { ...input, operationId: "00000000-0000-0000-0000-000000000000" }, { ...input, isActive: "false" }, null, []])("rejects authority injection or invalid mutation input %j", value => expect(isMemberAccessInput(value)).toBe(false));
it.each([{ ...result, tenantId: "PRIVATE" }, { ...result, userId: "" }, { ...result, membershipActive: 0 }, { ...result, operationId: "" }, []])("rejects unsafe mutation replies %j", value => expect(parseMemberAccessResult(value)).toBeNull());
