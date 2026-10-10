import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { GroupInboxPanel } from "./group-inbox-panel";
afterEach(() => { cleanup(); vi.useRealTimers(); });
const tenant = "11111111-1111-4111-8111-111111111111", company = "22222222-2222-4222-8222-222222222222", user = "33333333-3333-4333-8333-333333333333";
const sourceId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", messageId = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
const source = { tenantId: tenant, companyId: company, sourceBindingId: sourceId };
const sources = { companyId: company, items: [{ source, displayName: "Nhóm khách 😀 �", provider: "owned", version: 1 }], hasMore: false };
const head = { messageId, revision: 2, lastChangedSequence: 5, kind: 3, occurredAtUtc: "2026-10-10T00:00:00Z", isHistoricalBackfill: true };
const messages = { source, items: [head], nextBeforeSequence: null, hasCoverageGap: true };
const detail = { source, messageId, externalMessageId: "m ", revision: 2, committedSequence: 3, kind: 3, senderId: "sender ", replyToMessageId: null,
  occurredAtUtc: head.occurredAtUtc, text: "PRIVATE_SOURCE_TEXT 😀 �\n ", isHistoricalBackfill: true, hasCoverageGap: true };
function fixture(override: (url: string, init?: RequestInit) => Response | Promise<Response> | undefined = () => undefined) {
  const alive = { current: true }, unauthorized = vi.fn(), validate = vi.fn(async () => alive.current);
  const request = vi.fn(async (_generation: number, url: string, init?: RequestInit) => override(url, init)
    ?? Response.json(url.includes(`/${messageId}?`) ? detail : url.includes("/messages?") ? messages : sources));
  const props = { tenantId: tenant, companyId: company, userId: user, generation: 1, isCurrent: () => alive.current, validate, request, onUnauthorized: unauthorized };
  return { ...render(<GroupInboxPanel {...props} />), props, request, alive, unauthorized, validate };
}
async function open() {
  fireEvent.click(await screen.findByRole("button", { name: "Nhóm khách 😀 �" }));
  await screen.findByRole("button", { name: "Đọc tin 5" });
}
async function read() { await open(); fireEvent.click(screen.getByRole("button", { name: "Đọc tin 5" })); await screen.findByRole("article", { name: "Nội dung tin" }); }
it("opens granted metadata and current private text through reads only, preserves text and recovers after remount", async () => {
  let view = fixture(); await open(); expect(screen.queryByText(/PRIVATE_SOURCE_TEXT/)).toBeNull();
  fireEvent.click(screen.getByRole("button", { name: "Đọc tin 5" })); await screen.findByRole("article", { name: "Nội dung tin" });
  expect(screen.getByText(/PRIVATE_SOURCE_TEXT/).textContent).toBe(detail.text);
  expect(screen.getByText(/khoảng gián đoạn/)).toBeTruthy(); expect(screen.getByText(/Lịch sử bổ sung/)).toBeTruthy();
  expect(view.request.mock.calls.every(([, , init]) => (!init?.method || init.method === "GET") && init?.cache === "no-store")).toBe(true);
  view.unmount(); view = fixture(); await screen.findByRole("button", { name: "Nhóm khách 😀 �" }); expect(screen.queryByText(/PRIVATE_SOURCE_TEXT/)).toBeNull();
});
it("supports empty discovery and an empty source without inventing content", async () => {
  const view = fixture(() => Response.json({ ...sources, items: [] })); await screen.findByText(/Chưa có nguồn được cấp quyền/); view.unmount();
  fixture(url => url.includes("/messages?") ? Response.json({ ...messages, items: [] }) : undefined);
  fireEvent.click(await screen.findByRole("button", { name: "Nhóm khách 😀 �" })); await screen.findByText(/Chưa có tin đã tiếp nhận/);
});
it("fresh recall removes previous source content and retains explicit gap evidence", async () => {
  let recalled = false; fixture(url => recalled && url.includes(`/${messageId}?`) ? Response.json({ ...detail, kind: 4, revision: 3, text: null }) : undefined);
  await read(); recalled = true; fireEvent.click(screen.getByRole("button", { name: "Cập nhật tin" }));
  expect(screen.queryByText(/PRIVATE_SOURCE_TEXT/)).toBeNull(); await screen.findByText("Tin đã được thu hồi. Nội dung không còn hiển thị.");
  expect(screen.queryByText(/PRIVATE_SOURCE_TEXT/)).toBeNull(); expect(screen.getByText(/khoảng gián đoạn/)).toBeTruthy();
});
it.each([401, 403, 404, 503])( "clears all private source state on fresh refusal %i", async status => {
  let denied = false; const view = fixture(() => denied ? Response.json({ private: "PRIVATE_ERROR" }, { status }) : undefined);
  await read(); denied = true; fireEvent.click(screen.getByRole("button", { name: "Cập nhật tin" }));
  if (status === 401) await waitFor(() => expect(view.unauthorized).toHaveBeenCalled()); else await screen.findByRole("alert");
  expect(screen.queryByText(/PRIVATE_SOURCE_TEXT/)).toBeNull(); expect(screen.queryByRole("button", { name: "Nhóm khách 😀 �" })).toBeNull();
  expect(screen.queryByText("PRIVATE_ERROR")).toBeNull();
});
it("focus clears an existing private body before its fresh read and fences late body after newer discovery", async () => {
  let held = false, finish!: (value: Response) => void, signal: AbortSignal | null | undefined;
  const pending = new Promise<Response>(resolve => { finish = resolve; });
  const view = fixture((url, init) => { if (held && url.includes(`/${messageId}?`)) { signal = init?.signal; return pending; } return undefined; });
  await read(); held = true; fireEvent.focus(window); await waitFor(() => expect(signal).toBeTruthy());
  expect(screen.queryByText(/PRIVATE_SOURCE_TEXT/)).toBeNull(); fireEvent.click(screen.getByRole("button", { name: "Tải lại nguồn" }));
  await screen.findByRole("button", { name: "Nhóm khách 😀 �" }); expect(signal?.aborted).toBe(true);
  await act(async () => finish(Response.json(detail))); expect(screen.queryByText(/PRIVATE_SOURCE_TEXT/)).toBeNull(); view.unmount();
});
it("never paints a delayed detail from an old company/user/generation and aborts that transport", async () => {
  let finish!: (value: Response) => void, signal: AbortSignal | null | undefined;
  const pending = new Promise<Response>(resolve => { finish = resolve; });
  const view = fixture((url, init) => { if (url.includes(`/${messageId}?`)) { signal = init?.signal; return pending; } return undefined; });
  await open(); fireEvent.click(screen.getByRole("button", { name: "Đọc tin 5" })); await waitFor(() => expect(signal).toBeTruthy());
  const foreign = "cccccccc-cccc-4ccc-8ccc-cccccccccccc";
  view.rerender(<GroupInboxPanel {...view.props} companyId={foreign} userId={foreign} generation={2}
    request={async () => Response.json({ companyId: foreign, items: [], hasMore: false })} />);
  expect(screen.queryByRole("button", { name: "Nhóm khách 😀 �" })).toBeNull(); await screen.findByText(/Chưa có nguồn được cấp quyền/);
  expect(signal?.aborted).toBe(true); await act(async () => finish(Response.json(detail))); expect(screen.queryByText(/PRIVATE_SOURCE_TEXT/)).toBeNull();
});
it("hides already rendered private data immediately on tenant replacement and refuses a same-company foreign tenant", async () => {
  const view = fixture(); await read();
  const foreign = "cccccccc-cccc-4ccc-8ccc-cccccccccccc";
  view.rerender(<GroupInboxPanel {...view.props} tenantId={foreign} generation={2} />);
  expect(screen.queryByText(/PRIVATE_SOURCE_TEXT/)).toBeNull(); await screen.findByRole("alert");
  expect(screen.queryByRole("button", { name: "Nhóm khách 😀 �" })).toBeNull();
});
it("checks final fresh company authority before rendering any private projection", async () => {
  const validate = vi.fn().mockResolvedValueOnce(true).mockResolvedValueOnce(false);
  render(<GroupInboxPanel tenantId={tenant} companyId={company} userId={user} generation={1} isCurrent={() => true}
    validate={validate} request={async () => Response.json(sources)} onUnauthorized={vi.fn()} />);
  await waitFor(() => expect(validate).toHaveBeenCalledTimes(2)); expect(screen.queryByRole("button", { name: "Nhóm khách 😀 �" })).toBeNull();
});
it("preserves keyset cursor and offers fresh latest messages without fetching private text", async () => {
  const items = Array.from({ length: 25 }, (_, index) => ({ ...head, messageId: `30000000-0000-4000-8000-${String(index + 1).padStart(12, "0")}`, lastChangedSequence: 100 - index }));
  const view = fixture(url => url.includes("/messages?") ? Response.json(url.includes("beforeSequence=76") ? messages
    : { ...messages, items, nextBeforeSequence: 76 }) : undefined);
  fireEvent.click(await screen.findByRole("button", { name: "Nhóm khách 😀 �" })); await screen.findByRole("button", { name: "Tin trước đó" });
  fireEvent.click(screen.getByRole("button", { name: "Tin trước đó" })); await screen.findByRole("button", { name: "Đọc tin 5" });
  fireEvent.click(screen.getByRole("button", { name: "Tin mới nhất" })); await screen.findByRole("button", { name: "Đọc tin 100" });
  expect(view.request.mock.calls.map(([, url]) => url).filter(url => url.includes("/messages?"))).toEqual([
    `/api/local/group-sources/${sourceId}/messages?companyId=${company}&limit=25`,
    `/api/local/group-sources/${sourceId}/messages?companyId=${company}&limit=25&beforeSequence=76`,
    `/api/local/group-sources/${sourceId}/messages?companyId=${company}&limit=25`,
  ]); expect(screen.queryByText(/PRIVATE_SOURCE_TEXT/)).toBeNull();
});
it("aborts and discards a pending request on unmount", async () => {
  let signal: AbortSignal | null | undefined;
  const view = fixture((_url, init) => { signal = init?.signal; return new Promise<Response>(() => {}); });
  await waitFor(() => expect(signal).toBeTruthy()); view.unmount(); expect(signal?.aborted).toBe(true);
});
