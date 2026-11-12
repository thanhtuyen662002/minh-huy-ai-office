import { describe, expect, it } from "vitest";
import { currentMonth, isAiAccountSave, isAiPriceSave, parseAiUsageReport, parseAiUsageSettings } from "./ai-usage";

const id = "12345678-1234-1234-1234-123456789abc";
const totals = { questions: 2, calls: 5, inputTokens: 1000, cachedInputTokens: 200, outputTokens: 50, cost: 1234.5, unpricedCalls: 0 };

describe("AI usage contracts", () => {
  it("accepts only exact, bounded price and customer saves", () => {
    expect(isAiPriceSave({ model: "claude-sonnet-5-5", inputPerMillion: 75000, cachedInputPerMillion: 7500, outputPerMillion: 375000 })).toBe(true);
    expect(isAiPriceSave({ model: "m", inputPerMillion: -1, cachedInputPerMillion: 0, outputPerMillion: 0 })).toBe(false);
    expect(isAiPriceSave({ model: "m", inputPerMillion: 0.00001, cachedInputPerMillion: 0, outputPerMillion: 0 })).toBe(false);
    expect(isAiPriceSave({ model: "m", inputPerMillion: 1, cachedInputPerMillion: 1, outputPerMillion: 1, extra: 1 })).toBe(false);
    expect(isAiAccountSave({ name: "Mak", monthlyBudget: 500000, questionsPerMinute: 30, sourceIds: [id], version: null })).toBe(true);
    expect(isAiAccountSave({ name: "Mak", monthlyBudget: null, questionsPerMinute: null, sourceIds: [], version: "4" })).toBe(true);
    expect(isAiAccountSave({ name: "Mak", monthlyBudget: null, questionsPerMinute: 1.5, sourceIds: [], version: null })).toBe(false);
    expect(isAiAccountSave({ name: "Mak", monthlyBudget: null, questionsPerMinute: null, sourceIds: [id, id], version: null })).toBe(false);
    expect(isAiAccountSave({ name: " ", monthlyBudget: null, questionsPerMinute: null, sourceIds: [], version: null })).toBe(false);
  });

  it("parses the report and settings and rejects malformed rows", () => {
    const report = { month: "2026-11", currency: "VND", totals,
      accounts: [{ accountId: null, name: "Chưa gán khách hàng", monthlyBudget: null, totals }],
      users: [{ userId: id, name: "Tuyên", totals }], models: [{ providerId: "company-shared", model: "claude-sonnet-5-5", totals }],
      days: [{ date: "2026-11-12", totals }] };
    expect(parseAiUsageReport(report)?.accounts[0].name).toBe("Chưa gán khách hàng");
    expect(parseAiUsageReport({ ...report, days: [{ date: "12/11", totals }] })).toBeNull();
    expect(parseAiUsageReport({ ...report, totals: { ...totals, cost: -1 } })).toBeNull();
    const settings = {
      prices: [{ model: "m", inputPerMillion: 1, cachedInputPerMillion: 1, outputPerMillion: 2, effectiveFromUtc: "2026-11-12T00:00:00Z" }],
      accounts: [{ id, name: "Mak", monthlyBudget: null, questionsPerMinute: 10, version: "1", sourceIds: [id] }],
      sources: [{ id, logicalName: "Mak ERP" }], userQuestionsPerMinute: 20 };
    expect(parseAiUsageSettings(settings)?.accounts[0].sourceIds).toEqual([id]);
    expect(parseAiUsageSettings({ ...settings, sources: [{ id: "x", logicalName: "y" }] })).toBeNull();
  });

  it("names the month in Vietnam time", () => {
    expect(currentMonth(new Date("2026-10-31T17:30:00Z"))).toBe("2026-11");
    expect(currentMonth(new Date("2026-10-31T16:30:00Z"))).toBe("2026-10");
  });
});
