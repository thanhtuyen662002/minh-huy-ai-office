import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { LocalAiWorkspace } from "./local-ai-workspace";
const tenant = "11111111-1111-4111-8111-111111111111", company = "22222222-2222-4222-8222-222222222222", user = "33333333-3333-4333-8333-333333333333";
const source = { tenantId: tenant, companyId: company, sourceBindingId: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa" };
const page = { companyId: company, items: [{ source, displayName: "PRIVATE_GRANTED_SOURCE", provider: "owned", version: 1 }], hasMore: false };
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });
it("ordinary mobile readers can open inbox directly without preparing a task; logout cancels and discards late source data", async () => {
  let active = true, held: Promise<Response> | null = null, signal: AbortSignal | null | undefined, finish!: (response: Response) => void;
  const fetcher = vi.fn(async (url: string, init?: RequestInit) => {
    if (url === "/api/local/session/logout") { active = false; return Response.json({ ok: true }); }
    if (url.startsWith("/api/local/session?")) return active ? Response.json({ tenantId: tenant, companyId: company, userId: user, roles: ["viewer"] }) : Response.json({}, { status: 401 });
    if (url.startsWith("/api/local/data-sources?")) return Response.json([]);
    if (url.startsWith("/api/local/group-sources?")) { signal = init?.signal; return held ?? Response.json(page); }
    throw new Error("Unexpected owned fixture route");
  });
  vi.stubGlobal("fetch", fetcher); vi.stubGlobal("BroadcastChannel", undefined);
  render(<LocalAiWorkspace companyId={company} companyName="Fixture" />);
  const mobile = await screen.findByRole("button", { name: "Mở Hộp thư nguồn trên di động" });
  expect(mobile.parentElement?.className).toContain("flex-wrap"); expect(screen.queryByRole("button", { name: "Thành viên" })).toBeNull();
  fireEvent.click(mobile); await screen.findByRole("button", { name: "PRIVATE_GRANTED_SOURCE" });
  expect(fetcher.mock.calls.every(([url, init]) => !url.startsWith("/api/local/tasks") && (!init?.method || init.method === "GET"))).toBe(true);
  held = new Promise<Response>(resolve => { finish = resolve; }); const previous = signal;
  fireEvent.click(screen.getByRole("button", { name: "Tải lại nguồn" })); await waitFor(() => expect(signal).not.toBe(previous));
  const pendingSignal = signal!; fireEvent.click(screen.getByRole("button", { name: "Đăng xuất" })); await screen.findByText("Local Pilot");
  expect(pendingSignal.aborted).toBe(true); await act(async () => finish(Response.json(page)));
  expect(screen.queryByRole("button", { name: "PRIVATE_GRANTED_SOURCE" })).toBeNull(); expect(screen.queryByRole("region", { name: "Hộp thư nguồn" })).toBeNull();
});

it("source ReaderGrant refusal clears the shipping inbox while the same issued-session member remains signed in", async () => {
  const messageId = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
  const occurredAtUtc = "2026-10-10T00:00:00Z";
  let granted = true;
  const fetcher = vi.fn(async (url: string) => {
    if (url.startsWith("/api/local/session?")) return Response.json({ tenantId: tenant, companyId: company, userId: user, roles: ["viewer"] });
    if (url.startsWith("/api/local/data-sources?")) return Response.json([]);
    if (url.startsWith("/api/local/group-sources?")) return Response.json({ ...page, items: granted ? page.items : [] });
    if (url.includes(`/messages/${messageId}?`)) return granted ? Response.json({ source, messageId, externalMessageId: "m ",
      revision: 1, committedSequence: 1, kind: 1, senderId: "sender ", replyToMessageId: null, occurredAtUtc,
      text: "PRIVATE_GROUP_BODY", isHistoricalBackfill: false, hasCoverageGap: false }) : Response.json({}, { status: 403 });
    if (url.includes("/messages?")) return Response.json({ source, items: [{ messageId, revision: 1, lastChangedSequence: 1, kind: 1,
      occurredAtUtc, isHistoricalBackfill: false }], nextBeforeSequence: null, hasCoverageGap: false });
    throw new Error("Unexpected owned fixture route");
  });
  vi.stubGlobal("fetch", fetcher); vi.stubGlobal("BroadcastChannel", undefined);
  render(<LocalAiWorkspace companyId={company} companyName="Fixture" />);
  fireEvent.click(await screen.findByRole("button", { name: "Mở Hộp thư nguồn trên di động" }));
  fireEvent.click(await screen.findByRole("button", { name: "PRIVATE_GRANTED_SOURCE" }));
  fireEvent.click(await screen.findByRole("button", { name: "Đọc tin 1" }));
  await screen.findByText("PRIVATE_GROUP_BODY");
  granted = false; fireEvent.click(screen.getByRole("button", { name: "Cập nhật tin" }));
  await screen.findByRole("alert");
  expect(screen.queryByText("PRIVATE_GROUP_BODY")).toBeNull(); expect(screen.queryByText("PRIVATE_GRANTED_SOURCE")).toBeNull();
  expect(screen.getByRole("button", { name: "Đăng xuất" })).toBeTruthy();
  fireEvent.click(screen.getByRole("button", { name: "Tải lại nguồn" })); await screen.findByText(/Chưa có nguồn được cấp quyền/);
  granted = true; fireEvent.click(screen.getByRole("button", { name: "Tải lại nguồn" }));
  await screen.findByRole("button", { name: "PRIVATE_GRANTED_SOURCE" });
  expect(screen.getByRole("button", { name: "Đăng xuất" })).toBeTruthy();
});
