import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { expect, it, vi } from "vitest";
import { TaskHistoryPanel } from "./task-history-panel";
const company = "22222222-2222-2222-2222-222222222222", user = "11111111-1111-1111-1111-111111111111", taskId = "33333333-3333-3333-3333-333333333333";
const task = { taskId, status: 6, createdAtUtc: "2026-10-09T00:00:00Z", updatedAtUtc: "2026-10-09T00:00:01Z", summary: "PRIVATE_QUESTION_A", metadataUnavailable: false };
const saved = { kind: "answer", answer: "PRIVATE_SAVED_ANSWER 😀 �", evidence: "ai-provider-reasoning-after-bounded-read-only-erp-catalog", provider: "fixture", model: "fixture-v1", inputTokens: 2, outputTokens: 3, totalTokens: 5 };
const detail = { companyId: company, task, result: saved, resultUnavailable: false };
const page = { companyId: company, items: [task], offset: 0, limit: 25, hasMore: false };
function fixture(override: (url: string, init?: RequestInit) => Response | Promise<Response> | undefined = () => undefined) {
  const state = { current: true }; const unauthorized = vi.fn(), validate = vi.fn(async () => state.current);
  const request = vi.fn(async (_generation: number, url: string, init?: RequestInit) => override(url, init)
    ?? Response.json(url.includes("/history?") ? detail : page));
  const props = { companyId: company, userId: user, generation: 1, isCurrent: () => state.current, validate, request, onUnauthorized: unauthorized };
  const view = render(<TaskHistoryPanel {...props} />); return { ...view, props, state, request, validate, unauthorized };
}
it("recovers saved results by owner reads after remount and never sends a mutation", async () => {
  let view = fixture(); await screen.findByText(task.summary);
  fireEvent.click(screen.getByRole("button", { name: "Xem công việc PRIVATE_QUESTION_A" })); await screen.findByText(saved.answer);
  fireEvent.click(screen.getByRole("button", { name: "Cập nhật trạng thái" })); await screen.findByText(saved.answer);
  expect(view.request.mock.calls.every(([, , init]) => !init?.method || init.method === "GET")).toBe(true);
  expect(view.request.mock.calls.every(([, , init]) => init?.cache === "no-store")).toBe(true); view.unmount();
  view = fixture(); await screen.findByText(task.summary); fireEvent.click(screen.getByRole("button", { name: "Xem công việc PRIVATE_QUESTION_A" })); await screen.findByText(saved.answer);
});
it.each([0, 1, 2, 3, 4, 5])("shows durable unfinished/failed status %i without forging an answer", async status => {
  fixture(url => Response.json(url.includes("/history?") ? { ...detail, task: { ...task, status }, result: null } : { ...page, items: [{ ...task, status }] }));
  await screen.findByText(task.summary); fireEvent.click(screen.getByRole("button", { name: "Xem công việc PRIVATE_QUESTION_A" }));
  await screen.findByRole("article", { name: "Chi tiết công việc" }); expect(screen.queryByText(saved.answer)).toBeNull();
  expect(screen.getByText(status === 5 ? /Công việc không hoàn thành/ : /Công việc chưa hoàn thành/)).toBeTruthy();
});
it("keeps completed status and gives an actionable unavailable result", async () => {
  fixture(url => url.includes("/history?") ? Response.json({ ...detail, result: null, resultUnavailable: true }) : undefined);
  await screen.findByText(task.summary); fireEvent.click(screen.getByRole("button", { name: "Xem công việc PRIVATE_QUESTION_A" }));
  await screen.findByText(/Công việc đã hoàn thành nhưng chưa đọc được/); expect(screen.queryByText(saved.answer)).toBeNull();
});
it("empty archive offers the existing assistant workflow", async () => {
  fixture(() => Response.json({ ...page, items: [] })); await screen.findByText(/Chưa có công việc trong trang này/);
});
it("aborts and fences an old detail when a newer list refresh completes first", async () => {
  let finish!: (response: Response) => void, oldSignal: AbortSignal | null | undefined;
  const pending = new Promise<Response>(resolve => { finish = resolve; });
  const { request } = fixture((url, init) => { if (url.includes("/history?")) { oldSignal = init?.signal; return pending; } return undefined; });
  await screen.findByText(task.summary); fireEvent.click(screen.getByRole("button", { name: "Xem công việc PRIVATE_QUESTION_A" }));
  await waitFor(() => expect(request.mock.calls.some(([, url]) => url.includes("/history?"))).toBe(true));
  fireEvent.click(screen.getByRole("button", { name: "Tải lại công việc" })); await screen.findByText(task.summary);
  expect(oldSignal?.aborted).toBe(true); await act(async () => finish(Response.json(detail))); expect(screen.queryByText(saved.answer)).toBeNull();
});
it.each([401, 403, 404, 503])("clears saved detail on denial/failure %i and performs no rerun", async status => {
  let fail = false; const { request, unauthorized } = fixture(url => fail && url.includes("/history?") ? Response.json({ private: "PRIVATE_DIAGNOSTIC" }, { status }) : undefined);
  await screen.findByText(task.summary); fireEvent.click(screen.getByRole("button", { name: "Xem công việc PRIVATE_QUESTION_A" })); await screen.findByText(saved.answer);
  fail = true; fireEvent.click(screen.getByRole("button", { name: "Cập nhật trạng thái" }));
  if (status === 401) await waitFor(() => expect(unauthorized).toHaveBeenCalled()); else await screen.findByRole("alert");
  expect(screen.queryByText(saved.answer)).toBeNull(); expect(screen.queryByText("PRIVATE_DIAGNOSTIC")).toBeNull();
  expect(request.mock.calls.every(([, , init]) => !init?.method || init.method === "GET")).toBe(true);
});
it("cannot paint a late private result after company/user/generation replacement", async () => {
  let finish!: (response: Response) => void; const pending = new Promise<Response>(resolve => { finish = resolve; });
  const view = fixture(url => url.includes("/history?") ? pending : undefined);
  await screen.findByText(task.summary); fireEvent.click(screen.getByRole("button", { name: "Xem công việc PRIVATE_QUESTION_A" }));
  await waitFor(() => expect(view.request.mock.calls.some(([, url]) => url.includes("/history?"))).toBe(true));
  const foreign = "66666666-6666-6666-6666-666666666666";
  const nextRequest = vi.fn(async () => Response.json({ ...page, companyId: foreign, items: [] }));
  view.rerender(<TaskHistoryPanel {...view.props} companyId={foreign} userId="77777777-7777-7777-7777-777777777777" generation={2} request={nextRequest} />);
  await screen.findByText(/Chưa có công việc/); await act(async () => finish(Response.json(detail)));
  expect(screen.queryByText(task.summary)).toBeNull(); expect(screen.queryByText(saved.answer)).toBeNull();
});
it("does not publish a reply after final fresh authority fails", async () => {
  const validate = vi.fn().mockResolvedValueOnce(true).mockResolvedValueOnce(false);
  render(<TaskHistoryPanel companyId={company} userId={user} generation={1} isCurrent={() => true}
    validate={validate} request={async () => Response.json(page)} onUnauthorized={vi.fn()} />);
  await waitFor(() => expect(validate).toHaveBeenCalledTimes(2)); expect(screen.queryByText(task.summary)).toBeNull();
});
it("aborts pending archive transport on unmount", async () => {
  let signal: AbortSignal | null | undefined;
  const view = fixture((_url, init) => { signal = init?.signal; return new Promise<Response>(() => undefined); });
  await waitFor(() => expect(signal).toBeTruthy()); view.unmount(); expect(signal?.aborted).toBe(true);
});
