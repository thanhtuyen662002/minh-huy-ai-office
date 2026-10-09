import { webcrypto } from "node:crypto";
import { act, renderHook } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { submissionFingerprint, type SubmissionInput, type SubmissionIntent } from "../lib/task-submission-intent";
import { readSubmissionPayload, useTaskSubmission } from "./use-task-submission";

const companyId = "22222222-2222-4222-8222-222222222222", dataSourceId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
const question = "Tồn kho 😀 �", operationId = "11111111-1111-4111-8111-111111111111";
const original = { operationId, dataSourceId, question };
const fingerprint = "6354CD8F9D07F489B9F1B213CE89B4FE5EF4016DFC44329AE740B0C39BDE0656";
function intent(input: SubmissionInput = original, state: 0 | 1 | 2 = 0): SubmissionIntent {
  return { companyId, ...input, inputFingerprint: fingerprint, state, createdAtUtc: "2026-10-09T08:00:00Z",
    expiresAtUtc: "2026-10-10T08:00:00Z", accepted: state === 1 ? receipt(input) : null };
}
function receipt(input: SubmissionInput = original) {
  return { companyId, operationId: input.operationId, dataSourceId: input.dataSourceId, inputFingerprint: fingerprint,
    taskId: "33333333-3333-4333-8333-333333333333", stepId: "44444444-4444-4444-8444-444444444444",
    messageId: "55555555-5555-4555-8555-555555555555", status: 0, dispatchState: 0, createdAtUtc: "2026-10-09T08:00:00Z" };
}
const accepted = receipt;
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(done => { resolve = done; }); return { promise, resolve }; }
type Reply = (url: string, init?: RequestInit) => Response | Promise<Response> | undefined;
function fixture(reply: Reply = () => undefined) {
  const generation = { current: 1 }, state = { ready: true };
  const unauthorized = vi.fn(), validate = vi.fn(async (value: number) => value === generation.current);
  let captured = original;
  const request = vi.fn(async (_generation: number, url: string, init?: RequestInit) => {
    const override = reply(url, init); if (override !== undefined) return override;
    if (init?.method === "POST" && !url.includes("/submit?")) captured = JSON.parse(init.body as string);
    return Response.json(url.includes("/submit?") ? accepted(captured) : intent(captured), { status: url.includes("/submit?") ? 202 : 200 });
  });
  const view = renderHook(() => useTaskSubmission({ companyId, generation, isCurrent: value => value === generation.current,
    ready: () => state.ready, validate, request, onUnauthorized: unauthorized }));
  return { ...view, generation, state, request, validate, unauthorized };
}
type View = ReturnType<typeof fixture>;
function begin(view: View) {
  let attempt!: NonNullable<ReturnType<View["result"]["current"]["begin"]>>;
  act(() => { attempt = view.result.current.begin(dataSourceId, question)!; }); expect(attempt).toBeTruthy(); return attempt;
}
async function send(view: View, attempt = begin(view)) { await act(async () => { await view.result.current.send(attempt); }); }
beforeEach(() => vi.stubGlobal("crypto", webcrypto));
afterEach(() => { vi.unstubAllGlobals(); vi.restoreAllMocks(); });

it("captures immutable input and acquires synchronously before any await or rerender", async () => {
  const view = fixture(); const attempt = begin(view);
  let second: unknown;
  act(() => { second = view.result.current.begin(companyId, "Changed form"); }); expect(second).toBeNull();
  expect(view.request).not.toHaveBeenCalled(); expect(Object.isFrozen(attempt.input)).toBe(true);
  await act(async () => { const first = view.result.current.send(attempt); const repeated = view.result.current.send(attempt); expect(await repeated).toBeNull(); await first; });
  expect(view.request).toHaveBeenCalledTimes(2);
  const prepare = JSON.parse(view.request.mock.calls[0][2]!.body as string);
  expect(prepare).toEqual({ ...original, operationId: attempt.input.operationId });
  expect(view.result.current.pending).toMatchObject({ phase: "accepted", input: prepare });
  expect(JSON.parse(view.request.mock.calls[1][2]!.body as string)).toEqual({ inputFingerprint: fingerprint });
});
it("keeps the exact operation after an unknown prepare reply and deliberately retries identical bytes", async () => {
  let fail = true; const view = fixture((_url, init) => { if (init?.method === "POST" && fail) { fail = false; throw new Error("private network diagnostic"); } return undefined; });
  const attempt = begin(view); await send(view, attempt);
  expect(view.result.current.pending).toMatchObject({ phase: "unknown", prepared: false, input: attempt.input });
  expect(view.result.current.pending?.notice).not.toContain("private");
  act(() => { expect(view.result.current.begin(dataSourceId, "replacement")).toBeNull(); });
  let retry!: typeof attempt; act(() => { retry = view.result.current.retry()!; }); await send(view, retry);
  expect(view.request.mock.calls[0][2]!.body).toBe(view.request.mock.calls[1][2]!.body);
  expect(view.result.current.pending?.receipt?.operationId).toBe(attempt.input.operationId);
});
it("keeps a prepared operation after an unknown202 and retries only its fingerprint", async () => {
  let fail = true; const view = fixture((url) => { if (url.includes("/submit?") && fail) { fail = false; return Response.json({ error: "unavailable" }, { status: 503 }); } });
  const attempt = begin(view); await send(view, attempt);
  expect(view.result.current.pending).toMatchObject({ phase: "unknown", prepared: true, input: attempt.input });
  let retry!: typeof attempt; act(() => { retry = view.result.current.retry()!; }); await send(view, retry);
  expect(view.request.mock.calls.filter(([, url]) => !url.includes("/submit?"))).toHaveLength(1);
  expect(view.request.mock.calls[1].slice(1)).toEqual(view.request.mock.calls[2].slice(1));
  expect(view.result.current.pending?.phase).toBe("accepted");
});
it("reconciles unknown outcomes through GET without submitting, including404 without a false success", async () => {
  let recovered = false, op = original;
  const view = fixture((url, init) => {
    if (init?.method === "POST") { op = JSON.parse(init.body as string); return Response.json({}, { status: 503 }); }
    return recovered ? Response.json({ ...intent(op, 1), accepted: accepted(op) }) : new Response(null, { status: 404 });
  });
  await send(view); await act(async () => view.result.current.reconcile());
  expect(view.result.current.pending?.phase).toBe("unknown");
  recovered = true; await act(async () => view.result.current.reconcile());
  expect(view.result.current.pending?.phase).toBe("accepted");
  expect(view.request.mock.calls.map(([, , init]) => init?.method ?? "GET")).toEqual(["POST", "GET", "GET"]);
});
it.each([0, 1, 2] as const)("resumes persisted state%i without automatic execution", async state => {
  const view = fixture(); const value = { ...intent(original, state), accepted: state === 1 ? accepted() : null };
  act(() => { expect(view.result.current.resume(value)).toBe(true); });
  expect(view.request).not.toHaveBeenCalled();
  expect(view.result.current.pending?.phase).toBe(state === 0 ? "prepared" : state === 1 ? "accepted" : "expired");
  if (state === 0) { let attempt!: ReturnType<typeof begin>; act(() => { attempt = view.result.current.retry()!; }); await send(view, attempt); expect(view.request).toHaveBeenCalledTimes(1); }
  else act(() => { expect(view.result.current.retry()).toBeNull(); });
});
it("cannot replace an unresolved operation through resume; explicit new operation preserves server history", async () => {
  const view = fixture(); act(() => { expect(view.result.current.resume(intent())).toBe(true); });
  const other = { ...intent(), operationId: crypto.randomUUID() };
  act(() => { expect(view.result.current.resume(other)).toBe(false); expect(view.result.current.newOperation()).toBe(true); expect(view.result.current.resume(other)).toBe(true); });
  expect(view.request).not.toHaveBeenCalled(); expect(view.result.current.pending?.input.operationId).toBe(other.operationId);
});
it.each(["intent-expired", "intent-unavailable", "operation-conflict", "intent-limit"])("keeps an honest bounded %s outcome", async code => {
  const view = fixture(() => Response.json({ code, diagnostic: "PRIVATE" }, { status: 409 })); await send(view);
  expect(view.result.current.pending?.phase).toBe(code === "intent-expired" ? "expired" : code === "intent-limit" ? "unknown" : "unavailable");
  expect(view.result.current.pending?.notice).not.toContain("PRIVATE");
  if (code !== "intent-limit") act(() => { expect(view.result.current.retry()).toBeNull(); });
});
it("retains the operation on403 and clears through the session owner on401", async () => {
  let status = 403; const view = fixture(() => Response.json({}, { status })); await send(view);
  expect(view.result.current.pending?.phase).toBe("denied"); expect(view.unauthorized).not.toHaveBeenCalled();
  status = 401; let attempt!: ReturnType<typeof begin>; act(() => { attempt = view.result.current.retry()!; }); await send(view, attempt);
  expect(view.unauthorized).toHaveBeenCalledOnce(); act(() => view.result.current.reset()); expect(view.result.current.pending).toBeNull();
});
it.each(["taskId", "operationId", "dataSourceId", "companyId", "inputFingerprint", "createdAtUtc", "status", "extra"])("does not release a malformed or mismatched202 %s receipt", async field => {
  let input = original;
  const view = fixture((url, init) => {
    if (!url.includes("/submit?")) { input = JSON.parse(init!.body as string); return Response.json(intent(input)); }
    return Response.json({ ...accepted(input), [field]: field === "status" ? 7 : field === "taskId" ? "bad" : "mismatched" }, { status: 202 });
  });
  await send(view); expect(view.result.current.pending?.phase).toBe("unknown"); expect(view.result.current.pending?.receipt).toBeNull();
});
it("checks a resumed fingerprint before anyPOST", async () => {
  const view = fixture(() => Response.json({ ...accepted(), taskId: companyId }, { status: 202 }));
  // Accepted resumes cannot execute. A malicious hash in a prepared resume also cannot execute.
  act(() => { view.result.current.resume({ ...intent(), inputFingerprint: "A".repeat(64) }); });
  let attempt!: ReturnType<typeof begin>; act(() => { attempt = view.result.current.retry()!; }); await send(view, attempt);
  expect(view.request).not.toHaveBeenCalled(); expect(view.result.current.pending?.phase).toBe("unknown");
});
it.each(["taskId", "stepId", "messageId", "createdAtUtc"])("preserves the original accepted %s across a lost prepare reply", async field => {
  let input = original;
  const view = fixture((url, init) => {
    if (!url.includes("/submit?")) { input = JSON.parse(init!.body as string); return Response.json(intent(input, 1)); }
    return Response.json({ ...accepted(input), [field]: field === "createdAtUtc" ? "2026-10-09T08:00:01Z" : companyId }, { status: 202 });
  });
  await send(view); expect(view.result.current.pending?.phase).toBe("unknown");
  expect(view.result.current.pending?.receipt).toMatchObject({ [field]: accepted()[field as keyof ReturnType<typeof accepted>] });
});
it("does not release a private receipt when final fresh authority validation fails", async () => {
  const view = fixture(); view.validate.mockImplementation(async () => view.request.mock.calls.length < 2);
  await send(view); expect(view.result.current.pending?.phase).not.toBe("accepted"); expect(view.result.current.pending?.receipt).toBeNull();
});
it.each(["reset", "generation", "unmount"])("fences successful late headers after %s", async boundary => {
  const held = deferred<Response>(), entered = deferred<void>();
  const view = fixture(() => { entered.resolve(); return held.promise; });
  const attempt = begin(view); let sending!: Promise<unknown>;
  act(() => { sending = view.result.current.send(attempt); }); await act(async () => entered.promise);
  act(() => { if (boundary === "reset") view.result.current.reset(); else if (boundary === "generation") view.generation.current++; else view.unmount(); });
  await act(async () => { held.resolve(Response.json(intent(attempt.input))); await sending; });
  expect(view.request).toHaveBeenCalledTimes(1); expect(view.result.current.pending?.phase).not.toBe("accepted");
  if (boundary === "reset") expect(view.result.current.pending).toBeNull();
});
it("old finally cannot unlock or overwrite a newer operation", async () => {
  const held = deferred<Response>(), entered = deferred<void>(); let first = true;
  const view = fixture(() => { if (first) { first = false; entered.resolve(); return held.promise; } });
  const old = begin(view); let sending!: Promise<unknown>; act(() => { sending = view.result.current.send(old); }); await act(async () => entered.promise);
  act(() => view.result.current.reset()); const next = begin(view);
  await act(async () => { held.resolve(Response.json(intent(old.input))); await sending; });
  expect(view.result.current.working).toBe(true); expect(view.result.current.pending?.input).toBe(next.input);
  act(() => { expect(view.result.current.begin(dataSourceId, question)).toBeNull(); });
  await send(view, next); expect(view.result.current.pending?.receipt?.operationId).toBe(next.input.operationId);
});
it("aborts a private late body and leaves no persisted browser input", async () => {
  const entered = deferred<void>(); let canceled = false;
  const view = fixture(() => { entered.resolve(); return new Response(new ReadableStream({ cancel() { canceled = true; } }), { headers: { "Content-Type": "application/json" } }); });
  const local = vi.spyOn(Storage.prototype, "setItem"), attempt = begin(view); let sending!: Promise<unknown>;
  act(() => { sending = view.result.current.send(attempt); }); await act(async () => entered.promise); await act(async () => Promise.resolve());
  act(() => view.result.current.reset()); await act(async () => sending);
  expect(canceled).toBe(true); expect(view.result.current.pending).toBeNull(); expect(local).not.toHaveBeenCalled();
});
it("fresh final validation also fences reconciliation after hashing", async () => {
  const view = fixture(() => Response.json({ ...intent(original, 1), accepted: accepted() }));
  act(() => view.result.current.resume(intent()));
  view.validate.mockImplementation(async () => view.validate.mock.calls.length < 3);
  await act(async () => view.result.current.reconcile()); expect(view.result.current.pending?.phase).toBe("prepared");
});
it("refuses invalid scalar input and closed sessions synchronously", async () => {
  const view = fixture(); act(() => { expect(view.result.current.begin(dataSourceId, "bad\ud800")).toBeNull(); view.state.ready = false; expect(view.result.current.begin(dataSourceId, question)).toBeNull(); });
  expect(view.request).not.toHaveBeenCalled(); expect(await submissionFingerprint(original)).toBe(fingerprint);
});
it.each([
  new Response('{"state":0,"\\u0073tate":1}', { headers: { "Content-Type": "application/json" } }),
  new Response(new Uint8Array([0xc3, 0x28]), { headers: { "Content-Type": "application/json" } }),
  new Response("x".repeat(65537), { headers: { "Content-Type": "application/json" } }),
])("bounds and validates actual streamed payloads", async response => {
  await expect(readSubmissionPayload(response, new AbortController().signal)).rejects.toThrow();
});
