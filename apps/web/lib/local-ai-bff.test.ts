import { describe, expect, it } from "vitest";
import { isCanonicalCompanyId } from "./local-ai-bff";

describe("local AI BFF company selector", () => {
  it("accepts the seeded platform company Guid", () => {
    expect(isCanonicalCompanyId("22222222-2222-2222-2222-222222222222")).toBe(true);
  });

  it("rejects empty, non-canonical and Guid.Empty selectors", () => {
    expect(isCanonicalCompanyId("")).toBe(false);
    expect(isCanonicalCompanyId(" 22222222-2222-2222-2222-222222222222")).toBe(false);
    expect(isCanonicalCompanyId("not-a-guid")).toBe(false);
    expect(isCanonicalCompanyId("00000000-0000-0000-0000-000000000000")).toBe(false);
  });
});
