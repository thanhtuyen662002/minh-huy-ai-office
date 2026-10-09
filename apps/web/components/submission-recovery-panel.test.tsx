import { webcrypto } from "node:crypto";
import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { SubmissionRecoveryPanel } from "./submission-recovery-panel";
const companyId = "22222222-2222-4222-8222-222222222222", userId = "66666666-6666-4666-8666-666666666666";
const operationId = "11111111-1111-4111-8111-111111111111", dataSourceId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
const question = "Tồn kho 😀 �", inputFingerprint = "6354CD8F9D07F489B9F1B213CE89B4FE5EF4016DFC44329AE740B0C39BDE0656";
const accepted = { companyId, operationId, dataSourceId, inputFingerprint, taskId: "33333333-3333-4333-8333-333333333333",
  stepId: "44444444-4444-4444-8444-444444444444", messageId: "55555555-5555-4555-8555-555555555555", status: 0, dispatchState: 0, createdAtUtc: "2026-10-09T08:00:00Z" };
const intent = { companyId, operationId, dataSourceId, question, inputFingerprint, state: 0 as const,
  createdAtUtc: "2026-10-09T08:00:00Z", expiresAtUtc: "2026-10-10T08:00:00Z", accepted: null };
const page = { companyId, items: [intent], offset: 0, limit: 25, hasMore: false };
const json = (value: unknown) => Response.json(value);
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(done => { resolve = done; }); return { promise, resolve }; }
function fixture(reply: (url: string, init?: RequestInit) => Response | Promise<Response> | undefined = () => undefined) {
  const state = { current: true }, validate = vi.fn(async () => state.current), onUnauthorized = vi.fn(), onResume = vi.fn(() => true);
  const request = vi.fn(async (_generation: number, url: string, init?: RequestInit) => reply(url, init) ?? json(url.includes(`${operationId}?`) ? intent : page));
  const props = { companyId, userId, generation: 1, isCurrent: () => state.current, validate, request, onUnauthorized, onResume };
  return { ...render(<SubmissionRecoveryPanel {...props} />), props, state, validate, request, onUnauthorized, onResume };
}
beforeEach(() => vi.stubGlobal("crypto", webcrypto));
afterEach(() => { vi.unstubAllGlobals(); vi.restoreAllMocks(); });

it("recovers an owner page after remount and selects through fresh GET without any execution", async () => {
  let view = fixture(); await screen.findByText(question); expect(view.onResume).not.toHaveBeenCalled();
  fireEvent.click(screen.getByRole("button", { name: `Lấy yêu cầu đã lưu ${question}` }));
  await waitFor(() => expect(view.onResume).toHaveBeenCalledWith(intent));
  expect(view.validate).toHaveBeenCalledTimes(4);
  expect(view.request.mock.calls.every(([, , init]) => !init?.method || init.method === "GET")).toBe(true);
  expect(view.request.mock.calls.every(([, , init]) => init?.cache === "no-store")).toBe(true);
  view.unmount(); view = fixture(); await screen.findByText(question); expect(view.onResume).not.toHaveBeenCalled();
});
it.each([1, 2, 3])("shows persisted state%i without offering a replay", async state => {
  const value = state === 3 ? { companyId, operationId, state, dataSourceId: null, question: null, inputFingerprint: null, createdAtUtc: null, expiresAtUtc: null, accepted: null }
    : { ...intent, state, accepted: state === 1 ? accepted : null };
  const view = fixture(() => json({ ...page, items: [value] }));
  await screen.findByText(state === 3 ? "Thông tin yêu cầu chưa có" : question);
  expect(screen.queryByRole("button", { name: /Lấy yêu cầu đã lưu/ })).toBeNull(); expect(view.onResume).not.toHaveBeenCalled();
});
it("paginates25 owners and verifies the requested offset", async () => {
  const first = { ...page, hasMore: true, items: Array.from({ length: 25 }, (_, index) => ({ ...intent, operationId: `11111111-1111-4111-8111-${String(index).padStart(12, "0")}` })) };
  const view = fixture(url => json(url.includes("offset=25") ? { ...page, offset: 25 } : first));
  await screen.findByText("Trang 1"); fireEvent.click(screen.getByRole("button", { name: "Trang yêu cầu sau" }));
  await screen.findByText("Trang 2"); expect(view.request.mock.calls[1][1]).toContain("offset=25&limit=25");
  fireEvent.click(screen.getByRole("button", { name: "Trang yêu cầu trước" })); await screen.findByText("Trang 1");
});
it("shows an empty owner page without automatically preparing any request", async () => {
  const view = fixture(() => json({ ...page, items: [] })); await screen.findByText(/Chưa có yêu cầu đã lưu/);
  expect(view.request).toHaveBeenCalledOnce(); expect(view.onResume).not.toHaveBeenCalled();
});
it.each(["company", "fingerprint", "ttl", "offset", "limit", "duplicate", "unknown-field"])("rejects malformed private page %s", async variant => {
  const body = variant === "company" ? { ...page, companyId: userId } : variant === "fingerprint" ? { ...page, items: [{ ...intent, question: "PRIVATE_TAMPERED" }] }
    : variant === "ttl" ? { ...page, items: [{ ...intent, expiresAtUtc: "2026-10-11T08:00:00Z" }] }
      : variant === "offset" ? { ...page, offset: 25 } : variant === "limit" ? { ...page, limit: 24 }
        : variant === "duplicate" ? { ...page, items: [intent, intent] } : { ...page, diagnostic: "PRIVATE_DIAGNOSTIC" };
  fixture(() => json(body)); await screen.findByRole("alert"); expect(screen.queryByText(question)).toBeNull(); expect(screen.queryByText("PRIVATE_TAMPERED")).toBeNull();
});
it.each([401, 403, 503])("clears private data on HTTP%i and never paints an upstream diagnostic", async status => {
  let fail = false; const view = fixture(() => fail ? jsonError(status) : undefined); await screen.findByText(question);
  fail = true; fireEvent.click(screen.getByRole("button", { name: "Tải lại yêu cầu" }));
  if (status === 401) await waitFor(() => expect(view.onUnauthorized).toHaveBeenCalled()); else await screen.findByRole("alert");
  expect(screen.queryByText(question)).toBeNull(); expect(screen.queryByText("PRIVATE_DIAGNOSTIC")).toBeNull(); expect(view.onResume).not.toHaveBeenCalled();
});
function jsonError(status: number) { return Response.json({ diagnostic: "PRIVATE_DIAGNOSTIC" }, { status }); }
it("keeps another unresolved client request and offers a useful action", async () => {
  const view = fixture(); view.onResume.mockReturnValue(false); await screen.findByText(question);
  fireEvent.click(screen.getByRole("button", { name: `Lấy yêu cầu đã lưu ${question}` })); await screen.findByText(/Bạn đang giữ một yêu cầu khác/);
});
it("fresh detail404 does not resume a stale cached intent", async () => {
  const view = fixture(url => url.includes(`${operationId}?`) ? new Response(null, { status: 404 }) : undefined);
  await screen.findByText(question); fireEvent.click(screen.getByRole("button", { name: `Lấy yêu cầu đã lưu ${question}` }));
  await screen.findByText(/Chưa tìm thấy yêu cầu/); expect(view.onResume).not.toHaveBeenCalled();
});
it("does not select a changed private detail even when its GUID is well formed", async () => {
  const view = fixture(url => url.includes(`${operationId}?`) ? json({ ...intent, operationId: userId }) : undefined);
  await screen.findByText(question); fireEvent.click(screen.getByRole("button", { name: `Lấy yêu cầu đã lưu ${question}` }));
  await screen.findByRole("alert"); expect(view.onResume).not.toHaveBeenCalled();
});
it.each(["user", "company", "generation", "unmount"])("fences late private headers across %s replacement", async boundary => {
  const held = deferred<Response>(); const view = fixture(() => held.promise);
  await waitFor(() => expect(view.request).toHaveBeenCalledOnce());
  if (boundary === "unmount") view.unmount(); else {
    const replacement = { ...view.props, userId: boundary === "user" ? dataSourceId : userId,
      companyId: boundary === "company" ? dataSourceId : companyId, generation: boundary === "generation" ? 2 : 1,
      request: vi.fn(async () => new Promise<Response>(() => {})) };
    view.rerender(<SubmissionRecoveryPanel {...replacement} />);
  }
  await act(async () => held.resolve(json(page))); expect(screen.queryByText(question)).toBeNull(); expect(view.onResume).not.toHaveBeenCalled();
});
it("aborts private body reads on unmount and never stores questions in browser storage", async () => {
  let canceled = false; const local = vi.spyOn(Storage.prototype, "setItem");
  const view = fixture(() => new Response(new ReadableStream({ cancel() { canceled = true; } }), { headers: { "Content-Type": "application/json" } }));
  await waitFor(() => expect(view.request).toHaveBeenCalledOnce()); await act(async () => Promise.resolve()); view.unmount();
  await waitFor(() => expect(canceled).toBe(true)); expect(local).not.toHaveBeenCalled();
});
it("checks fresh authority after asynchronous hash verification before revealing a page", async () => {
  const view = fixture(); view.validate.mockImplementation(async () => view.validate.mock.calls.length < 2);
  await waitFor(() => expect(view.validate).toHaveBeenCalledTimes(2)); expect(screen.queryByText(question)).toBeNull();
});
it("checks fresh authority again before selecting private detail", async () => {
  const view = fixture(); await screen.findByText(question); view.validate.mockImplementation(async () => view.validate.mock.calls.length < 4);
  fireEvent.click(screen.getByRole("button", { name: `Lấy yêu cầu đã lưu ${question}` }));
  await waitFor(() => expect(view.validate).toHaveBeenCalledTimes(4)); expect(view.onResume).not.toHaveBeenCalled(); expect(screen.queryByText(question)).toBeNull();
});
it("prevents selecting saved requests while the composer is working", async () => {
  const view = fixture(); view.rerender(<SubmissionRecoveryPanel {...view.props} disabled />); await screen.findByText(question);
  fireEvent.click(screen.getByRole("button", { name: `Lấy yêu cầu đã lưu ${question}` })); expect(view.request).toHaveBeenCalledOnce(); expect(view.onResume).not.toHaveBeenCalled();
});
