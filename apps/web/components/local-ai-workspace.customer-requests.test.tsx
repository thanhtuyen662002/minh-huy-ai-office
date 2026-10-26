import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { LocalAiWorkspace } from "./local-ai-workspace";
import { requestCompany as company, requestTenant as tenant, requestUser as user, requestTicket, memberDirectory } from "../lib/customer-request-test-fixture";

afterEach(() => vi.unstubAllGlobals());
it("opens tracker for the shell-selected company on mobile and cancels private reads on logout", async () => {
  let active = true, held = false, release!: (response: Response) => void, signal: AbortSignal | null | undefined;
  const fetcher = vi.fn(async (url: string, init?: RequestInit) => {
    if (url === "/api/local/session/logout") { active = false; return Response.json({ ok: true }); }
    if (url.startsWith("/api/local/session?")) return active ? Response.json({ tenantId: tenant, companyId: company, userId: user, roles: ["viewer"] }) : Response.json({}, { status: 401 });
    if (url.startsWith("/api/local/data-sources?")) return Response.json([]);
    if (url.startsWith("/api/local/company/members?")) return Response.json(memberDirectory);
    if (url.startsWith("/api/local/customer-requests?")) {
      expect(new URL(url, "http://web.test").searchParams.get("companyId")).toBe(company);
      signal = init?.signal;
      return held ? new Promise<Response>(resolve => { release = resolve; }) : Response.json({ items: [requestTicket], offset: 0, limit: 25, hasMore: false });
    }
    throw new Error("Unexpected route");
  });
  vi.stubGlobal("fetch", fetcher); vi.stubGlobal("BroadcastChannel", undefined);
  render(<LocalAiWorkspace companyId={company} companyName="Công ty đã chọn" />);
  fireEvent.click(await screen.findByRole("button", { name: "Mở Yêu cầu khách hàng trên di động" }));
  await screen.findByRole("button", { name: "YC-0001" });
  expect(screen.getByRole("button", { name: "Yêu cầu khách hàng" }).getAttribute("aria-current")).toBe("page");
  expect(fetcher.mock.calls.every(([, init]) => !init?.method)).toBe(true);
  held = true; fireEvent.click(screen.getByRole("button", { name: "Tải lại phiếu" }));
  await waitFor(() => expect(release).toBeTruthy()); const pendingSignal = signal;
  fireEvent.click(screen.getByRole("button", { name: "Đăng xuất" })); await screen.findByText("Local Pilot");
  expect(pendingSignal?.aborted).toBe(true);
  await act(async () => release(Response.json({ items: [requestTicket], offset: 0, limit: 25, hasMore: false })));
  expect(screen.queryByRole("region", { name: "Yêu cầu khách hàng" })).toBeNull();
  expect(screen.queryByText(requestTicket.customerName)).toBeNull();
});
