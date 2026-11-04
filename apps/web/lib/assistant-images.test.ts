import { expect, it } from "vitest";
import { imageToken, questionText, splitQuestion } from "./assistant-images";
import { parseTaskHistoryPage } from "./task-history";

const a = "6f1c0c8e-1d2a-4b3c-8d4e-5f6a7b8c9d0e", b = "7f1c0c8e-1d2a-4b3c-8d4e-5f6a7b8c9d0e";

it("splits a question into its words and picture ids", () => {
  expect(splitQuestion(`Mã lỗi này là gì? ${imageToken(a)} ${imageToken(b.toUpperCase())} ${imageToken(a)}`))
    .toEqual({ text: "Mã lỗi này là gì?", imageIds: [a, b] });
  expect(splitQuestion(imageToken(a))).toEqual({ text: "", imageIds: [a] });
  expect(questionText(imageToken(a))).toBe("Ảnh đính kèm");
  expect(splitQuestion("Câu dài [img:6f1c0c8e-1d2a…").text).toBe("Câu dài");
});

it("reads history items with and without picture ids", () => {
  const base = { taskId: a, status: 6, createdAtUtc: "2026-10-09T00:00:00Z", updatedAtUtc: "2026-10-09T00:00:01Z", summary: "Ảnh đính kèm", metadataUnavailable: false };
  const page = (item: object) => ({ companyId: b, items: [item], offset: 0, limit: 25, hasMore: false });
  expect(parseTaskHistoryPage(page({ ...base, imageIds: [b.toUpperCase()] }))?.items[0].imageIds).toEqual([b]);
  expect(parseTaskHistoryPage(page({ ...base, imageIds: null }))?.items[0].imageIds).toEqual([]);
  expect(parseTaskHistoryPage(page(base))?.items[0].imageIds).toBeUndefined();
  expect(parseTaskHistoryPage(page({ ...base, imageIds: ["nope"] }))).toBeNull();
});
