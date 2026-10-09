import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { LocalAiWorkspace } from "./local-ai-workspace";
const company = "22222222-2222-2222-2222-222222222222", user = "11111111-1111-1111-1111-111111111111", taskId = "33333333-3333-3333-3333-333333333333";
const task = { taskId, status: 6, createdAtUtc: "2026-10-09T00:00:00Z", updatedAtUtc: "2026-10-09T00:00:01Z", summary: "PRIVATE_OWNER_QUESTION", metadataUnavailable: false };
function backend() {
  const state = { active: true, pending: null as Promise<Response> | null, signal: null as AbortSignal | null };
  const fetcher = vi.fn(async (url: string, init?: RequestInit) => {
    if (url === "/api/local/session/logout") { state.active = false; return Response.json({ ok: true }); }
    if (url.startsWith("/api/local/session?")) return state.active ? Response.json({ tenantId: "tenant", companyId: company, userId: user, roles: ["viewer"] }) : Response.json({}, { status: 401 });
    if (url.startsWith("/api/local/data-sources?")) return Response.json([]);
    if (url.startsWith("/api/local/session/companies")) return Response.json({ items: [{ companyId: company, companyName: "Fixture" }] });
    if (url.startsWith("/api/local/tasks/intents?")) return Response.json({ companyId: company, items: [], offset: 0, limit: 25, hasMore: false });
    if (url.startsWith("/api/local/tasks?")) {
      expect(init?.signal).toBeTruthy();
      state.signal = init!.signal!;
      if (state.pending) return state.pending;
      return Response.json({ companyId: company, items: [task], offset: 0, limit: 25, hasMore: false });
    }
    if (url.includes("/history?")) return Response.json({ companyId: company, task, result: null, resultUnavailable: true });
    throw new Error("Unexpected fixture route");
  });
  vi.stubGlobal("fetch", fetcher); vi.stubGlobal("BroadcastChannel", undefined); return { state, fetcher };
}
async function open() {
  const view = render(<LocalAiWorkspace companyId={company} companyName="Fixture" />);
  await waitFor(() => expect(screen.queryByRole("button", { name: "Công việc" })).toBeTruthy());
  fireEvent.click(screen.getByRole("button", { name: "Công việc" })); await screen.findByText(task.summary); return view;
}
afterEach(() => vi.unstubAllGlobals());
it("makes history available to ordinary members and restores it after workspace reload", async () => {
  const { fetcher } = backend(); let view = await open();
  expect(screen.queryByRole("button", { name: "Thành viên" })).toBeNull(); view.unmount(); view = await open();
  fireEvent.click(screen.getByRole("button", { name: `Xem công việc ${task.summary}` })); await screen.findByText(/Công việc đã hoàn thành nhưng chưa đọc được/);
  expect(fetcher.mock.calls.filter(([url]) => url.startsWith("/api/local/tasks")).every(([, init]) => !init?.method || init.method === "GET")).toBe(true);
});
it("clears the archive when logout or membership revalidation invalidates the session", async () => {
  const { state } = backend(); await open(); state.active = false;
  fireEvent.click(screen.getByRole("button", { name: "Tải lại công việc" }));
  await screen.findByText("Local Pilot"); expect(screen.queryByText(task.summary)).toBeNull(); expect(screen.queryByRole("region", { name: "Công việc của tôi" })).toBeNull();
});
it("logout removes all private archive state", async () => {
  backend(); await open(); fireEvent.click(screen.getByRole("button", { name: "Đăng xuất" }));
  await screen.findByText("Local Pilot"); expect(screen.queryByText(task.summary)).toBeNull();
});
it("the actual session hook aborts pending archive fetch and fences its late body on logout", async () => {
  const { state } = backend(); await open(); let finish!: (response: Response) => void;
  state.pending = new Promise<Response>(resolve => { finish = resolve; });
  const previous = state.signal; fireEvent.click(screen.getByRole("button", { name: "Tải lại công việc" }));
  await waitFor(() => expect(state.signal).not.toBe(previous)); const pendingSignal = state.signal!;
  fireEvent.click(screen.getByRole("button", { name: "Đăng xuất" })); await screen.findByText("Local Pilot");
  expect(pendingSignal.aborted).toBe(true);
  await act(async () => finish(Response.json({ companyId: company, items: [task], offset: 0, limit: 25, hasMore: false })));
  expect(screen.queryByText(task.summary)).toBeNull();
});
