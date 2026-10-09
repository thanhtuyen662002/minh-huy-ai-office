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
