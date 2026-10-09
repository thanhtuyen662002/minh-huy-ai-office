import { expect, it } from "vitest";
import { parseTaskHistoryDetail, parseTaskHistoryPage } from "./task-history";
const companyId = "22222222-2222-2222-2222-222222222222", taskId = "33333333-3333-3333-3333-333333333333";
const task = { taskId, status: 6, createdAtUtc: "2026-10-09T00:00:00+00:00", updatedAtUtc: "2026-10-09T00:00:01+00:00", summary: "Tồn kho 😀 �", metadataUnavailable: false };
const page = { companyId, items: [task], offset: 0, limit: 25, hasMore: false };
const result = { kind: "answer", answer: "Kết quả 😀 �\nĐã lưu", evidence: "ai-provider-reasoning-after-bounded-read-only-erp-catalog", provider: "fixture", model: "fixture-v1", inputTokens: 2, outputTokens: 3, totalTokens: 5 };
const detail = { companyId, task, result, resultUnavailable: false };
it("accepts only bounded typed history and a known saved answer", () => {
  expect(parseTaskHistoryPage(page)).toEqual(page); expect(parseTaskHistoryDetail(detail)).toEqual(detail);
  expect(parseTaskHistoryDetail({ ...detail, result: null, resultUnavailable: true })).toBeTruthy();
});
it.each([
  { ...page, offset: 10001 }, { ...page, limit: 101 }, { ...page, ownerId: "PRIVATE" }, { ...page, items: [task, task] },
  { ...page, hasMore: true }, { ...page, items: [{ ...task, waitReason: "PRIVATE" }] },
  { ...page, items: [{ ...task, summary: "PRIVATE\ud800" }] }, { ...page, items: [{ ...task, summary: "x".repeat(242) }] },
  { ...page, items: [{ ...task, status: 99 }] }, { ...page, items: [{ ...task, status: null }] },
  { ...page, items: [{ ...task, taskId: "00000000-0000-0000-0000-000000000000" }] },
  { ...page, items: [{ ...task, updatedAtUtc: "2026-10-08T00:00:00Z" }] },
])("rejects malformed/private/overlarge page %j", value => expect(parseTaskHistoryPage(value)).toBeNull());
it("never invokes a getter in untrusted JSON-shaped data", () => {
  const getter = { ...page }; Object.defineProperty(getter, "items", { get() { throw new Error("must not run"); }, enumerable: true });
  expect(parseTaskHistoryPage(getter)).toBeNull();
});
it.each([
  { ...detail, result: { ...result, secretRef: "PRIVATE" } }, { ...detail, result: { ...result, answer: "PRIVATE\ud800" } },
  { ...detail, result: { ...result, totalTokens: 6 } }, { ...detail, result: { ...result, kind: "unknown" } },
  { ...detail, task: { ...task, status: 0 } }, { ...detail, resultUnavailable: true },
  { ...detail, result: null }, { ...detail, task: { ...task, metadataUnavailable: true } },
])("rejects unknown or inconsistent saved result %j", value => expect(parseTaskHistoryDetail(value)).toBeNull());
it("accepts explicit unknown metadata and a truthful connection-only result", () => {
  expect(parseTaskHistoryDetail({ ...detail, task: { ...task, status: null, summary: null, metadataUnavailable: true }, result: null })).toBeTruthy();
  expect(parseTaskHistoryDetail({ ...detail, result: { kind: "connection", answer: null, evidence: "read-only-connection-probe", provider: null, model: null, inputTokens: null, outputTokens: null, totalTokens: null } })).toBeTruthy();
});
