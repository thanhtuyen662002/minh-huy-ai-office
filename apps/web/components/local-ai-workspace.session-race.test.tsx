import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { LocalAiWorkspace } from "./local-ai-workspace";
// The first two cases preserve the independent reviewer's exact synthetic
// reproductions. The matrix below exercises every awaited validation caller.
const company = "22222222-2222-2222-2222-222222222222";
const auth = (userId: string) => ({ tenantId: "tenant", companyId: company, userId, roles: ["member"] });
const sources = (name: string) => [{ id: "33333333-3333-3333-3333-333333333333", logicalName: name, kind: "SqlServer", environment: "Test", purpose: "synthetic", allowRead: true, allowWrite: false, maxConcurrency: 1, isEnabled: true }];
beforeEach(() => vi.stubGlobal("BroadcastChannel", undefined));
afterEach(() => { vi.unstubAllGlobals(); });
it("does not commit A sources when invalidation lands after validate resolves but before its caller resumes", async () => {
  let active = "A";
  let contextCalls = 0;
  let sourceCalls = 0;
  let notificationSent = false;
  vi.stubGlobal("fetch", vi.fn(async (url: string) => {
    if (url.startsWith("/api/local/session?")) {
      contextCalls++;
      const response = Response.json(auth(active));
      if (contextCalls === 5) {
        response.json = () => Promise.resolve(auth("A")).then(payload => {
          queueMicrotask(() => queueMicrotask(() => {
            active = "B"; notificationSent = true;
            fireEvent(window, new StorageEvent("storage", { key: "ai-office-local-session", newValue: JSON.stringify({ version: 1, id: "synthetic-reset-between-await-and-commit" }) }));
          }));
          return payload;
        });
      }
      return response;
    }
    if (url.startsWith("/api/local/data-sources?")) {
      sourceCalls++;
      if (active === "B") return new Promise<Response>(() => {});
      return Response.json(sources(sourceCalls === 1 ? "INITIAL-A" : "PRIVATE-LATE-A"));
    }
    throw new Error(`unexpected ${url}`);
  }));
  const { container } = render(<LocalAiWorkspace companyId={company} companyName="Synthetic" />);
  await screen.findByRole("option", { name: "INITIAL-A" });
  fireEvent.click(container.querySelector("nav button:nth-child(2)")!);
  fireEvent.click(container.querySelector("section button")!);
  await waitFor(() => expect(notificationSent).toBe(true));
  await waitFor(() => expect(sourceCalls).toBe(3));
  await act(async () => { await Promise.resolve(); });
  expect(screen.queryByRole("option", { name: "PRIVATE-LATE-A" })).toBeNull();
  expect(container.textContent).not.toContain("PRIVATE-LATE-A");
});
it("does not commit A sources after a concurrent authoritative focus validation accepts B", async () => {
  let active = "A"; let contextCalls = 0; let sourceCalls = 0;
  let resolveA!: (response: Response) => void;
  let resolveB!: (response: Response) => void;
  const validationA = new Promise<Response>(resolve => { resolveA = resolve; });
  const validationB = new Promise<Response>(resolve => { resolveB = resolve; });
  vi.stubGlobal("fetch", vi.fn(async (url: string) => {
    if (url.startsWith("/api/local/session?")) {
      contextCalls++;
      if (contextCalls === 5) return validationA;
      if (contextCalls === 6) return validationB;
      return Response.json(auth(active));
    }
    if (url.startsWith("/api/local/data-sources?")) {
      sourceCalls++;
      if (active === "B") return new Promise<Response>(() => {});
      return Response.json(sources(sourceCalls === 1 ? "INITIAL-A" : "PRIVATE-LATE-A"));
    }
    throw new Error(`unexpected ${url}`);
  }));
  const { container } = render(<LocalAiWorkspace companyId={company} companyName="Synthetic" />);
  await screen.findByRole("option", { name: "INITIAL-A" });
  fireEvent.click(container.querySelector("nav button:nth-child(2)")!);
  fireEvent.click(container.querySelector("section button")!);
  await waitFor(() => expect(contextCalls).toBe(5));
  fireEvent(window, new Event("focus"));
  await waitFor(() => expect(contextCalls).toBe(6));
  active = "B";
  await act(async () => { resolveA(Response.json(auth("A"))); resolveB(Response.json(auth("B"))); });
  await waitFor(() => expect(sourceCalls).toBe(3));
  expect(screen.queryByRole("option", { name: "PRIVATE-LATE-A" })).toBeNull();
  expect(container.textContent).not.toContain("PRIVATE-LATE-A");
});

const taskId = "44444444-4444-4444-4444-444444444444";
const checkpoint = { answer: "PRIVATE-LATE-A-ANSWER", provider: "fixture", model: "fixture", evidence: "synthetic", usage: { inputTokens: 0, outputTokens: 0, totalTokens: 0 }, erpEvidence: { databaseName: "PRIVATE-LATE-A-DATABASE", tableCount: 0, sampledTableCount: 0, topTables: [] } };
function deferred() {
  let resolve!: (response: Response) => void;
  const promise = new Promise<Response>((done) => { resolve = done; });
  return { promise, resolve };
}
it.each([
  "sources-pre", "sources-post", "connection-pre", "connection-post",
  "submit-pre", "submit-post", "poll-pre", "poll-post",
] as const)("fences the %s validation continuation when focus accepts B in the same microtask turn", async (boundary) => {
  const [operation, phase] = boundary.split("-");
  const target = operation === "poll" ? phase === "pre" ? 6 : 7 : phase === "pre" ? 4 : 5;
  const validationA = deferred(); const validationB = deferred(); const sourceB = deferred();
  let active = "A"; let contexts = 0; let sourceCalls = 0; let bSourcesRequested = false;
  let connections = 0; let submissions = 0; let polls = 0;
  vi.stubGlobal("fetch", vi.fn(async (url: string) => {
    if (url.startsWith("/api/local/session?")) {
      contexts++;
      if (contexts === target) return validationA.promise;
      if (contexts === target + 1) return validationB.promise;
      return Response.json(auth(active));
    }
    if (url.startsWith("/api/local/data-sources?")) {
      sourceCalls++;
      if (active === "B") { bSourcesRequested = true; return sourceB.promise; }
      return Response.json(sources(sourceCalls === 1 ? "INITIAL-A" : "PRIVATE-LATE-A"));
    }
    if (url.includes("/connection-test?")) { connections++; return Response.json({ code: "success" }); }
    if (url.startsWith(`/api/local/tasks/${taskId}?`)) {
      polls++;
      return Response.json({ taskId, taskStatus: 2, stepStatus: 2, dispatchState: 2, attempt: 1, resultPayloadJson: JSON.stringify(checkpoint), failureReason: null });
    }
    if (url.startsWith("/api/local/tasks?")) { submissions++; return Response.json({ taskId }, { status: 202 }); }
    throw new Error(`Unexpected fixture request: ${url}`);
  }));
  const { container } = render(<LocalAiWorkspace companyId={company} companyName="Synthetic" />);
  await screen.findByRole("option", { name: "INITIAL-A" });
  if (operation === "sources" || operation === "connection") {
    fireEvent.click(container.querySelector("nav button:nth-child(2)")!);
    fireEvent.click(container.querySelector(operation === "sources" ? "section button" : "article button")!);
  } else {
    const input = container.querySelector("textarea")!;
    fireEvent.change(input, { target: { value: "PRIVATE-LATE-A-QUESTION" } });
    fireEvent.submit(input.closest("form")!);
  }
  await waitFor(() => expect(contexts).toBe(target), { timeout: 4000 });
  fireEvent(window, new Event("focus"));
  await waitFor(() => expect(contexts).toBe(target + 1));
  active = "B";
  // Ordinary Response.json promises, no special body implementation: both
  // context fetches settle together, B invalidates before A's caller resumes.
  await act(async () => { validationA.resolve(Response.json(auth("A"))); validationB.resolve(Response.json(auth("B"))); });
  await waitFor(() => expect(bSourcesRequested).toBe(true));
  expect(container.textContent).not.toContain("PRIVATE-LATE-A");
  expect(container.textContent).not.toContain("Đã vào hàng đợi");
  if (operation === "connection" && phase === "pre") expect(connections).toBe(0);
  if (operation === "submit" && phase === "pre") expect(submissions).toBe(0);
  if (operation === "poll" && phase === "pre") expect(polls).toBe(0);
  await act(async () => sourceB.resolve(Response.json(sources("PUBLIC-B"))));
  await screen.findByRole("option", { name: "PUBLIC-B" });
  fireEvent.click(container.querySelector("nav button:nth-child(2)")!);
  expect(screen.queryByText("Kết nối tốt")).toBeNull();
  expect(container.textContent).not.toContain("PRIVATE-LATE-A");
}, 10000);
