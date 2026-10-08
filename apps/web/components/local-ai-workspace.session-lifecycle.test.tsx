import { StrictMode, useLayoutEffect } from "react";
import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { LocalAiWorkspace } from "./local-ai-workspace";

// Synthetic server/context fixtures, not a browser cookie or OIDC acceptance test.
const companyA = "22222222-2222-2222-2222-222222222222";
const companyB = "66666666-6666-6666-6666-666666666666";
const sourceId = "33333333-3333-3333-3333-333333333333";
const taskId = "44444444-4444-4444-4444-444444444444";
const context = (companyId: string, userId = "user-a", roles = ["member"]) => ({ tenantId: "tenant-" + companyId, companyId, userId, roles });
const sources = (name: string) => [{ id: sourceId, logicalName: name, kind: "SqlServer", environment: "Test", purpose: "Synthetic", allowRead: true, allowWrite: false, maxConcurrency: 1, isEnabled: true }];
const checkpoint = { answer: "PRIVATE-A-ANSWER", provider: "fixture", model: "fixture", evidence: "fixture", usage: { inputTokens: 0, outputTokens: 0, totalTokens: 0 }, erpEvidence: { databaseName: "PRIVATE-A-DATABASE", tableCount: 0, sampledTableCount: 0, topTables: [] } };
const snapshot = () => Response.json({ taskId, taskStatus: 2, stepStatus: 2, dispatchState: 2, attempt: 1, resultPayloadJson: JSON.stringify(checkpoint), failureReason: null });
function deferred() {
  let resolve!: (value: Response) => void;
  const promise = new Promise<Response>((done) => { resolve = done; });
  return { promise, resolve };
}
function fixture(override: (url: string, init?: RequestInit) => Promise<Response> | Response | undefined = () => undefined) {
  const fetcher = vi.fn(async (url: string, init?: RequestInit) => {
    const custom = override(url, init);
    if (custom !== undefined) return custom;
    const company = new URL(url, "http://fixture.invalid").searchParams.get("companyId") ?? companyA;
    if (url.startsWith("/api/local/session?")) return Response.json(context(company));
    if (url.startsWith("/api/local/data-sources?")) return Response.json(sources(company === companyA ? "SOURCE-A" : "SOURCE-B"));
    if (url.includes("/connection-test?")) return Response.json({ code: "success" });
    if (url === "/api/local/session/logout") return Response.json({ ok: true });
    if (url === "/api/local/session/login") return Response.json({ ok: true });
    if (url.startsWith(`/api/local/tasks/${taskId}?`)) return snapshot();
    if (url.startsWith("/api/local/tasks?")) return Response.json({ taskId }, { status: 202 });
    throw new Error("Unexpected synthetic route");
  });
  vi.stubGlobal("fetch", fetcher);
  return fetcher;
}
const workspace = (companyId = companyA) => <LocalAiWorkspace companyId={companyId} companyName="Fixture" />;
const ready = (name = "SOURCE-A") => screen.findByRole("option", { name });
function submit(container: HTMLElement) {
  const question = container.querySelector('textarea[name="question"]')!;
  fireEvent.change(question, { target: { value: "PRIVATE-A-QUESTION" } });
  fireEvent.submit(question.closest("form")!);
}
async function answered(container: HTMLElement) {
  await ready(); submit(container);
  await screen.findByText(checkpoint.answer, {}, { timeout: 4000 });
}
const privateGone = () => {
  expect(screen.queryByText("PRIVATE-A-QUESTION")).toBeNull();
  expect(screen.queryByText(checkpoint.answer)).toBeNull();
  expect(screen.queryByText("PRIVATE-A-DATABASE")).toBeNull();
};
// Node's native BroadcastChannel dispatches Node Events, incompatible with
// jsdom Events. Use the browser storage fallback here; test channel delivery
// explicitly with the transport fixture below rather than mixing runtimes.
beforeEach(() => vi.stubGlobal("BroadcastChannel", undefined));
afterEach(() => { vi.useRealTimers(); vi.unstubAllGlobals(); vi.restoreAllMocks(); });

it("removes draft and private controls synchronously while logout is pending", async () => {
  const pending = deferred();
  const fetcher = fixture((url) => url === "/api/local/session/logout" ? pending.promise : undefined);
  const { container } = render(workspace());
  await ready();
  const draft = container.querySelector("textarea")!;
  fireEvent.change(draft, { target: { value: "PRIVATE-DRAFT" } });
  const oldForm = draft.closest("form")!;
  fireEvent.click(container.querySelector("nav button:nth-child(2)")!);
  const refresh = container.querySelector("section button")!;
  const connection = container.querySelector("article button")!;
  fireEvent.click(container.querySelector("header button")!);
  const calls = fetcher.mock.calls.length;
  expect(container.querySelector("textarea,header,nav,input,select")).toBeNull();
  fireEvent.click(refresh); fireEvent.click(connection); fireEvent.submit(oldForm);
  await act(async () => { await Promise.resolve(); });
  expect(fetcher.mock.calls.length).toBe(calls);
  await act(async () => pending.resolve(Response.json({ ok: true })));
  expect(container.querySelector('input[name="username"]')).toBeTruthy();
});

it.each(["http", "network"])("keeps private data cleared and reports %s logout failure", async (kind) => {
  fixture((url) => url === "/api/local/session/logout" ? kind === "http" ? Response.json({}, { status: 503 }) : Promise.reject(new Error("offline")) : undefined);
  const { container } = render(workspace());
  await answered(container);
  fireEvent.click(container.querySelector("header button")!);
  await screen.findByRole("alert");
  privateGone();
  expect(container.querySelector("textarea")).toBeNull();
});

it("waits for cookie-mutating logout to settle before restoring a different company", async () => {
  const pending = deferred();
  const fetcher = fixture((url) => url === "/api/local/session/logout" ? pending.promise : undefined);
  const { container, rerender } = render(workspace());
  await ready();
  fireEvent.click(container.querySelector("header button")!);
  rerender(workspace(companyB));
  await act(async () => { await Promise.resolve(); });
  expect(fetcher.mock.calls.some(([url]) => url.includes(companyB))).toBe(false);
  expect(container.querySelector("input,textarea,select")).toBeNull();
  await act(async () => pending.resolve(Response.json({ ok: true })));
  await ready("SOURCE-B");
  expect(screen.queryByRole("option", { name: "SOURCE-A" })).toBeNull();
});

it("invalidates chat and ERP evidence on a connection-test 401", async () => {
  fixture((url) => url.includes("/connection-test?") ? Response.json({}, { status: 401 }) : undefined);
  const { container } = render(workspace());
  await answered(container);
  fireEvent.click(container.querySelector("nav button:nth-child(2)")!);
  fireEvent.click(container.querySelector("article button")!);
  await waitFor(() => expect(container.querySelector('input[name="username"]')).toBeTruthy());
  privateGone();
});

it.each(["read_only_unqualified", "__proto__"])("shows bounded credential guidance for connection code %s", async (code) => {
  fixture((url) => url.includes("/connection-test?")
    ? Response.json({ code, succeeded: false, message: "PRIVATE-PASSWORD-ENDPOINT" }, { status: 400 }) : undefined);
  const { container } = render(workspace());
  await ready();
  fireEvent.click(container.querySelector("nav button:nth-child(2)")!);
  fireEvent.click(container.querySelector("article button")!);
  await screen.findByText(code === "read_only_unqualified"
    ? "Cần tài khoản ERP chỉ đọc và quyền xem metadata phù hợp. Liên hệ quản trị viên để cấu hình."
    : "Kết nối thất bại");
  expect(screen.queryByText("PRIVATE-PASSWORD-ENDPOINT")).toBeNull();
  expect(screen.queryByText("Kết nối tốt")).toBeNull();
});

it("cancels polling timers and aborts outstanding requests on unmount under StrictMode", async () => {
  const fetcher = fixture();
  const { container, unmount } = render(<StrictMode>{workspace()}</StrictMode>);
  await ready();
  vi.useFakeTimers();
  submit(container);
  await act(async () => { for (let i = 0; i < 20; i++) await Promise.resolve(); });
  expect(fetcher.mock.calls.some(([url]) => url.startsWith("/api/local/tasks?"))).toBe(true);
  unmount();
  const count = fetcher.mock.calls.length;
  await act(async () => { await vi.advanceTimersByTimeAsync(15000); });
  expect(fetcher.mock.calls.length).toBe(count);
  expect(vi.getTimerCount()).toBe(0);
});

it.each(["context", "sources", "task"])("ignores late %s responses from a previous company", async (kind) => {
  const pending = deferred(); let intercepted = false;
  fixture((url) => {
    const prefix = kind === "context" ? "/api/local/session?" : kind === "sources" ? "/api/local/data-sources?" : `/api/local/tasks/${taskId}?`;
    if (url.startsWith(prefix) && url.includes(companyA)) { intercepted = true; return pending.promise; }
  });
  const { container, rerender } = render(workspace());
  if (kind === "task") { await ready(); submit(container); }
  await waitFor(() => expect(intercepted).toBe(true), { timeout: 4000 });
  rerender(workspace(companyB)); await ready("SOURCE-B");
  await act(async () => pending.resolve(kind === "context" ? Response.json(context(companyA)) : kind === "sources" ? Response.json(sources("PRIVATE-A-SOURCE")) : snapshot()));
  privateGone();
  expect(screen.queryByRole("option", { name: "PRIVATE-A-SOURCE" })).toBeNull();
  expect(screen.getByRole("option", { name: "SOURCE-B" })).toBeTruthy();
});

it.each(["user", "tenant", "roles"])("discards old chat when authoritative %s changes before a source refresh", async (field) => {
  let switched = false;
  fixture((url) => {
    if (!switched) return;
    if (url.startsWith("/api/local/session?")) return Response.json({ ...context(companyA), [field === "user" ? "userId" : field === "tenant" ? "tenantId" : "roles"]: field === "roles" ? ["reader"] : "changed" });
    if (url.startsWith("/api/local/data-sources?")) return Response.json(sources("SOURCE-CHANGED"));
  });
  const { container } = render(workspace()); await answered(container);
  switched = true;
  fireEvent.click(container.querySelector("nav button:nth-child(2)")!);
  fireEvent.click(container.querySelector("section button")!);
  await ready("SOURCE-CHANGED"); privateGone();
  expect((container.querySelector("textarea") as HTMLTextAreaElement).value).toBe("");
});

it.each(["storage", "focus", "visibility"])("revalidates server authority on %s without trusting notification identity", async (trigger) => {
  let switched = false;
  fixture((url) => {
    if (switched && url.startsWith("/api/local/session?")) return Response.json(context(companyA, "user-b", ["reader"]));
    if (switched && url.startsWith("/api/local/data-sources?")) return Response.json(sources("SERVER-B"));
  });
  const { container } = render(workspace()); await answered(container); switched = true;
  if (trigger === "storage") fireEvent(window, new StorageEvent("storage", { key: "ai-office-local-session", newValue: JSON.stringify({ version: 1, id: "fixture-notification", userId: "forged", roles: ["admin"] }) }));
  else if (trigger === "focus") fireEvent(window, new Event("focus"));
  else fireEvent(document, new Event("visibilitychange"));
  await ready("SERVER-B"); privateGone();
  expect(container.textContent).not.toContain("admin");
  expect(container.textContent).toContain("reader");
});

it("uses BroadcastChannel as invalidation only and releases its listener on unmount", async () => {
  let listener: ((event: { data: unknown }) => void) | null = null;
  const closed = vi.fn(); const sent = vi.fn();
  vi.stubGlobal("BroadcastChannel", class {
    set onmessage(value: typeof listener) { listener = value; }
    postMessage = sent;
    close = closed;
  });
  let switched = false;
  const fetcher = fixture((url) => switched && url.startsWith("/api/local/session?") ? Response.json(context(companyA, "server-user-b", ["reader"])) : undefined);
  const { container, unmount } = render(workspace()); await answered(container);
  switched = true;
  await act(async () => listener!({ data: { version: 1, id: "bc-fixture", context: context(companyA, "forged", ["admin"]) } }));
  await ready(); privateGone();
  expect(container.textContent).toContain("reader");
  expect(container.textContent).not.toContain("admin");
  fireEvent.click(container.querySelector("header button")!);
  await waitFor(() => expect(sent).toHaveBeenCalledTimes(2));
  expect(sent.mock.calls.every(([payload]) => Object.keys(payload).sort().join() === "id,version")).toBe(true);
  unmount(); const count = fetcher.mock.calls.length;
  await act(async () => listener!({ data: { version: 1, id: "after-unmount" } }));
  expect(fetcher.mock.calls.length).toBe(count);
  expect(closed).toHaveBeenCalledTimes(1);
});

it("drops a task result when the authoritative user changes while it is in flight", async () => {
  const pending = deferred(); let polling = false; let switched = false;
  fixture((url) => {
    if (url.startsWith(`/api/local/tasks/${taskId}?`)) { polling = true; return pending.promise; }
    if (switched && url.startsWith("/api/local/session?")) return Response.json(context(companyA, "user-b"));
    if (switched && url.startsWith("/api/local/data-sources?")) return Response.json(sources("SERVER-B"));
  });
  const { container } = render(workspace()); await ready(); submit(container);
  await waitFor(() => expect(polling).toBe(true), { timeout: 4000 });
  switched = true;
  await act(async () => pending.resolve(snapshot()));
  await ready("SERVER-B"); privateGone();
});

it("aborts a pending source fetch on unmount and ignores its eventual response", async () => {
  const pending = deferred(); let signal: AbortSignal | undefined;
  const fetcher = fixture((url, init) => {
    if (url.startsWith("/api/local/data-sources?")) { signal = init?.signal as AbortSignal; return pending.promise; }
  });
  const { unmount } = render(workspace());
  await waitFor(() => expect(signal).toBeTruthy());
  unmount(); expect(signal!.aborted).toBe(true);
  const count = fetcher.mock.calls.length;
  await act(async () => pending.resolve(Response.json(sources("LATE"))));
  expect(fetcher.mock.calls.length).toBe(count);
});

it("allows logout to cancel an accepted task while its polling timer is pending", async () => {
  const fetcher = fixture();
  const { container } = render(workspace()); await ready();
  vi.useFakeTimers(); submit(container);
  await act(async () => { for (let i = 0; i < 20; i++) await Promise.resolve(); });
  const logout = container.querySelector<HTMLButtonElement>("header button")!;
  expect(logout.disabled).toBe(false);
  fireEvent.click(logout);
  await act(async () => { await vi.advanceTimersByTimeAsync(15000); });
  expect(fetcher.mock.calls.some(([url]) => url.startsWith(`/api/local/tasks/${taskId}?`))).toBe(false);
  expect(container.querySelector('input[name="username"]')).toBeTruthy(); privateGone();
});

it("waits for a pending login before restoring a changed company and blocks duplicate login", async () => {
  const pending = deferred(); let loggedIn = false;
  const fetcher = fixture((url) => {
    if (url === "/api/local/session/login") return pending.promise.then((response) => { loggedIn = true; return response; });
    if (url.startsWith("/api/local/session?") && !loggedIn) return Response.json({}, { status: 401 });
  });
  const { container, rerender } = render(workspace());
  await waitFor(() => expect(container.querySelector('input[name="username"]')).toBeTruthy());
  fireEvent.change(container.querySelector('input[name="password"]')!, { target: { value: "synthetic-password" } });
  const form = container.querySelector("form")!;
  fireEvent.submit(form); fireEvent.submit(form);
  expect(fetcher.mock.calls.filter(([url]) => url === "/api/local/session/login")).toHaveLength(1);
  rerender(workspace(companyB));
  await act(async () => { await Promise.resolve(); });
  expect(fetcher.mock.calls.some(([url]) => url.includes(companyB))).toBe(false);
  await act(async () => pending.resolve(Response.json({ ok: true })));
  await ready("SOURCE-B");
});

it("fails closed if authority revalidation fails instead of treating sources as identity", async () => {
  let offline = false;
  fixture((url) => offline && url.startsWith("/api/local/session?") ? Promise.reject(new Error("offline")) : undefined);
  const { container } = render(workspace()); await answered(container); offline = true;
  fireEvent.click(container.querySelector("nav button:nth-child(2)")!);
  fireEvent.click(container.querySelector("section button")!);
  await waitFor(() => expect(container.querySelector('input[name="username"]')).toBeTruthy());
  privateGone();
});

it("handles rejected source requests without an unhandled background promise", async () => {
  fixture((url) => url.startsWith("/api/local/data-sources?") ? Promise.reject(new Error("offline")) : undefined);
  const { container } = render(workspace());
  await waitFor(() => expect(container.querySelector('[role="status"]')).toBeTruthy());
  expect(container.querySelector("textarea")).toBeTruthy();
});

it("closes an unauthorized connection response without waiting for its body", async () => {
  fixture((url) => url.includes("/connection-test?") ? new Response(new ReadableStream(), { status: 401 }) : undefined);
  const { container } = render(workspace()); await ready();
  fireEvent.click(container.querySelector("nav button:nth-child(2)")!);
  fireEvent.click(container.querySelector("article button")!);
  await waitFor(() => expect(container.querySelector('input[name="username"]')).toBeTruthy());
});

it("notifies other tabs when a cookie mutation settles after unmount", async () => {
  const pending = deferred();
  const writes = vi.spyOn(Storage.prototype, "setItem");
  fixture((url) => url === "/api/local/session/logout" ? pending.promise : undefined);
  const { container, unmount } = render(workspace()); await ready();
  fireEvent.click(container.querySelector("header button")!);
  expect(writes).toHaveBeenCalledTimes(1);
  unmount();
  await act(async () => pending.resolve(Response.json({ ok: true })));
  expect(writes).toHaveBeenCalledTimes(2);
  expect(writes.mock.calls.every(([key, value]) => key === "ai-office-local-session" && Object.keys(JSON.parse(value)).sort().join() === "id,version")).toBe(true);
});

it("ignores malformed and duplicate storage notifications", async () => {
  const fetcher = fixture();
  render(workspace()); await ready();
  const notify = (value: string) => fireEvent(window, new StorageEvent("storage", { key: "ai-office-local-session", newValue: value }));
  const count = fetcher.mock.calls.length;
  notify("invalid json"); notify(JSON.stringify({ version: 2, id: "wrong-version", roles: ["admin"] }));
  await act(async () => { await Promise.resolve(); });
  expect(fetcher.mock.calls.length).toBe(count);
  const valid = JSON.stringify({ version: 1, id: "once" });
  notify(valid); await ready();
  const refreshed = fetcher.mock.calls.length;
  notify(valid); await act(async () => { await Promise.resolve(); });
  expect(fetcher.mock.calls.length).toBe(refreshed);
});

it("never commits old-company private controls under new company props before passive effects", async () => {
  fixture(); let privateCommitted = false;
  function Probe({ companyId }: { companyId: string }) {
    useLayoutEffect(() => {
      if (companyId === companyB) privateCommitted = document.querySelector("textarea,select,header") !== null;
    }, [companyId]);
    return workspace(companyId);
  }
  const { rerender } = render(<Probe companyId={companyA} />); await ready();
  rerender(<Probe companyId={companyB} />);
  expect(privateCommitted).toBe(false);
  await ready("SOURCE-B");
});

it("preserves same-session chat, evidence and an unsent draft when focus returns", async () => {
  const fetcher = fixture();
  const { container } = render(workspace()); await answered(container);
  fireEvent.change(container.querySelector("textarea")!, { target: { value: "SAME-SESSION-DRAFT" } });
  const count = fetcher.mock.calls.length;
  fireEvent(window, new Event("focus"));
  await waitFor(() => expect(fetcher.mock.calls.length).toBe(count + 1));
  await act(async () => { await Promise.resolve(); });
  expect(screen.getByText(checkpoint.answer)).toBeTruthy();
  expect(screen.getByText("PRIVATE-A-DATABASE")).toBeTruthy();
  expect((container.querySelector("textarea") as HTMLTextAreaElement).value).toBe("SAME-SESSION-DRAFT");
});

it("preserves same-session valid errors without discarding chat", async () => {
  let fail = false;
  fixture((url) => fail && url.startsWith("/api/local/data-sources?") ? Response.json({ error: "FIXTURE-SOURCE-ERROR" }, { status: 503 }) : undefined);
  const { container } = render(workspace()); await answered(container); fail = true;
  fireEvent.click(container.querySelector("nav button:nth-child(2)")!);
  fireEvent.click(container.querySelector("section button")!);
  await waitFor(() => expect(container.querySelector("section button")!.textContent).not.toContain("Đang tải"));
  fireEvent.click(container.querySelector("nav button:nth-child(1)")!);
  await screen.findByText("FIXTURE-SOURCE-ERROR");
  expect(screen.getByText(checkpoint.answer)).toBeTruthy();
  expect(screen.getByText("PRIVATE-A-DATABASE")).toBeTruthy();
});
