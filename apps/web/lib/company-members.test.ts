import { expect, it } from "vitest";
import { parseMemberPage } from "./company-members";
const member = { userId: "33333333-3333-3333-3333-333333333333", displayName: "Owner", userActive: true, membershipActive: true, roles: ["admin"] };
const page = { companyId: "22222222-2222-2222-2222-222222222222", items: [member], offset: 0, limit: 25, hasMore: false };
it("accepts a bounded scoped page", () => expect(parseMemberPage(page)).toEqual(page));
it.each([
  { ...page, limit: 101 }, { ...page, offset: -1 }, { ...page, hasMore: true }, { ...page, companyId: "" },
  { ...page, items: [{ ...member, subject: "PRIVATE" }] }, { ...page, items: [member, member] },
  { ...page, items: [{ ...member, roles: ["admin", "admin"] }] }, { ...page, items: [{ ...member, roles: Array.from({ length: 257 }, (_, i) => "role" + i) }] },
  { ...page, items: [{ ...member, userActive: "true" }] }, { ...page, items: [{ ...member, displayName: "x".repeat(201) }] },
])("rejects malformed or overlarge member data %j", invalid => expect(parseMemberPage(invalid)).toBeNull());
