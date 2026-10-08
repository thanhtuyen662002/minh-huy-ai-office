import { expect, it, vi } from "vitest";
import { parseCompanyChoices } from "./company-choices";

const choice = { companyId: "22222222-2222-2222-2222-222222222222", companyName: "Công ty A" };
it("accepts complete bounded choices and copies immutable snapshots", () => {
  const payload = { items: [choice] }; const result = parseCompanyChoices(payload)!;
  expect(result).toEqual([choice]); expect(Object.isFrozen(result)).toBe(true); expect(Object.isFrozen(result[0])).toBe(true);
  expect(result[0]).not.toBe(choice); expect(parseCompanyChoices({ items: [] })).toEqual([]);
});
it.each([null, [], { items: [choice], tenantId: "forged" }, { items: [choice, choice] },
  { items: [{ ...choice, companyId: "foreign" }] }, { items: [{ ...choice, roles: ["admin"] }] },
  { items: [{ ...choice, companyName: " PRIVATE" }] }, { items: [{ ...choice, companyName: "PRIVATE\n" }] },
  { items: [{ ...choice, companyName: "N".repeat(201) }] }, { items: new Array(2) },
  { items: Array.from({ length: 101 }, () => choice) }, Object.create({ items: [choice] })])(
  "refuses partial, ambiguous, malformed or authority-bearing choices %j", value => expect(parseCompanyChoices(value)).toBeNull());
it("rejects accessors without invoking their private code", () => {
  const getter = vi.fn(() => "PRIVATE"); const item = { companyId: choice.companyId };
  Object.defineProperty(item, "companyName", { enumerable: true, get: getter });
  expect(parseCompanyChoices({ items: [item] })).toBeNull(); expect(getter).not.toHaveBeenCalled();
});
