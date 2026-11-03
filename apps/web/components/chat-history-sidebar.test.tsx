import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { expect, it, vi } from "vitest";
import { ChatHistorySidebar } from "./chat-history-sidebar";
const company = "22222222-2222-2222-2222-222222222222", user = "11111111-1111-1111-1111-111111111111";
const task = (n: number, status = 6) => ({ taskId: `33333333-3333-3333-3333-${String(n).padStart(12, "0")}`, status,
  createdAtUtc: "2026-10-09T00:00:00Z", updatedAtUtc: "2026-10-09T00:00:01Z", summary: `Câu hỏi ${n}`, metadataUnavailable: false });
const answer = { kind: "answer", answer: "Trả lời 1", evidence: "ai-agent-read-only-erp-queries", provider: "p", model: "m", inputTokens: 1, outputTokens: 1, totalTokens: 2 };

function fixture(pages: Record<string, unknown>, extra: Partial<Parameters<typeof ChatHistorySidebar>[0]> = {}) {
  const onOpen = vi.fn(), onNew = vi.fn();
  const request = vi.fn(async (_g: number, url: string, _init?: RequestInit) => {
    if (url.includes("/history?")) { const id = url.split("/")[4]; const n = Number(id.slice(-12));
      return Response.json({ companyId: company, task: { ...task(n), ...(n === 2 ? { createdAtUtc: "2026-10-09T01:00:00Z", updatedAtUtc: "2026-10-09T01:00:01Z" } : {}) }, result: answer, resultUnavailable: false }); }
    const offset = new URL(url, "http://x.invalid").searchParams.get("offset")!;
    return Response.json(pages[offset]);
  });
  render(<ChatHistorySidebar companyId={company} userId={user} generation={1} isCurrent={() => true} validate={async () => true}
    request={request} onUnauthorized={vi.fn()} activeConversationId={null} refreshKey={0} onOpen={onOpen} onNew={onNew} {...extra} />);
  return { request, onOpen, onNew };
}

it("lists past questions and opens one with read-only requests", async () => {
  const { request, onOpen, onNew } = fixture({ "0": { companyId: company, items: [task(1), task(2, 5)], offset: 0, limit: 25, hasMore: false } });
  fireEvent.click(await screen.findByRole("button", { name: /Câu hỏi 1/ }));
  await waitFor(() => expect(onOpen).toHaveBeenCalledOnce());
  expect(onOpen.mock.calls[0][0]).toBe(task(1).taskId); expect(onOpen.mock.calls[0][1][0].result.answer).toBe("Trả lời 1");
  expect(screen.getByText(/Thất bại/)).toBeTruthy();
  fireEvent.click(screen.getByRole("button", { name: "Cuộc trò chuyện mới" })); expect(onNew).toHaveBeenCalledOnce();
  expect(request.mock.calls.every(([, , init]) => !init?.method || init.method === "GET")).toBe(true);
  expect(screen.queryByRole("button", { name: "Xem thêm" })).toBeNull();
});

it("loads more history on demand", async () => {
  const first = Array.from({ length: 25 }, (_, i) => task(i + 1));
  fixture({ "0": { companyId: company, items: first, offset: 0, limit: 25, hasMore: true },
    "25": { companyId: company, items: [task(26)], offset: 25, limit: 25, hasMore: false } });
  fireEvent.click(await screen.findByRole("button", { name: "Xem thêm" }));
  await screen.findByRole("button", { name: /Câu hỏi 26/ });
  expect(screen.getByRole("button", { name: /Câu hỏi 1 / })).toBeTruthy();
  expect(screen.queryByRole("button", { name: "Xem thêm" })).toBeNull();
});

it("marks the open conversation", async () => {
  fixture({ "0": { companyId: company, items: [task(1)], offset: 0, limit: 25, hasMore: false } }, { activeConversationId: task(1).taskId });
  expect((await screen.findByRole("button", { name: /Câu hỏi 1/ })).getAttribute("aria-current")).toBe("true");
});

it("groups questions remembered as one conversation and opens them together, oldest first", async () => {
  const later = { ...task(2), createdAtUtc: "2026-10-09T01:00:00Z", updatedAtUtc: "2026-10-09T01:00:01Z" };
  window.localStorage.setItem(`aioffice-chat-conversations:${company}:${user}`, JSON.stringify({ [later.taskId]: task(1).taskId }));
  const { request, onOpen } = fixture({ "0": { companyId: company, items: [later, task(1)], offset: 0, limit: 25, hasMore: false } });
  const item = await screen.findByRole("button", { name: /Câu hỏi 1/ });
  expect(screen.queryByRole("button", { name: /Câu hỏi 2/ })).toBeNull(); expect(item.textContent).toContain("2 câu hỏi");
  fireEvent.click(item); await waitFor(() => expect(onOpen).toHaveBeenCalledOnce());
  expect(request.mock.calls.filter(([, url]) => url.includes("/history?")).map(([, url]) => url.split("/")[4]))
    .toEqual([task(1).taskId, later.taskId]);
  window.localStorage.clear();
});
