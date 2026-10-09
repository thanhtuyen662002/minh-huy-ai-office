import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { LocalAiWorkspace } from "./local-ai-workspace";
import { webcrypto } from "node:crypto";
import { ownedSubmissionFixture } from "./task-submission-test-fixture";

const companyId = "22222222-2222-2222-2222-222222222222";
const sourceId = "33333333-3333-3333-3333-333333333333";
const taskId = "44444444-4444-4444-4444-444444444444";
const source = { id: sourceId, logicalName: "fixture.erp", kind: "SqlServer", environment: "Test", purpose: "Fixture only", allowRead: true, allowWrite: false, maxConcurrency: 1, isEnabled: true };
const context = (userId: string) => ({ tenantId: "fixture-tenant", companyId, userId, roles: ["member"] });
const checkpoint = { answer: "PRIVATE-USER-A-ANSWER", provider: "fixture", model: "fixture", evidence: "fixture evidence", usage: { inputTokens: 1, outputTokens: 1, totalTokens: 2 }, erpEvidence: { databaseName: "PRIVATE-USER-A-DATABASE", tableCount: 1, sampledTableCount: 1, topTables: [] } };

beforeEach(() => { vi.stubGlobal("BroadcastChannel", undefined); vi.stubGlobal("crypto", webcrypto); });
afterEach(() => vi.unstubAllGlobals());

it.each(["logout", "source-expiry", "submit-expiry", "poll-expiry"] as const)("clears user A's question, answer and ERP evidence after %s before user B signs in", async (boundary) => {
  let sourcesExpired = false;
  let taskSubmissionExpired = false;
  let taskPollingExpired = false;
  let activeUser: string | null = "user-a";
  const submissions = ownedSubmissionFixture(companyId, taskId);
  vi.stubGlobal("fetch", vi.fn(async (input: string, init?: RequestInit) => {
    if (input.startsWith("/api/local/session?")) return activeUser ? Response.json(context(activeUser)) : Response.json({}, { status: 401 });
    if (input.startsWith("/api/local/data-sources?")) return sourcesExpired ? Response.json({}, { status: 401 }) : Response.json([source]);
    if (input === "/api/local/session/logout") { activeUser = null; return Response.json({ ok: true }); }
    if (input === "/api/local/session/login") { activeUser = "user-b"; return Response.json({ ok: true, context: context(activeUser) }); }
    if (input.startsWith(`/api/local/tasks/${taskId}?`)) return taskPollingExpired ? Response.json({}, { status: 401 }) : Response.json({ taskId, taskStatus: 2, stepStatus: 2, dispatchState: 2, attempt: 1, resultPayloadJson: JSON.stringify(checkpoint), failureReason: null });
    if (input.startsWith("/api/local/tasks/intents")) {
      if (taskSubmissionExpired && input.includes("/submit?")) return Response.json({}, { status: 401 });
      return submissions(input, init);
    }
    throw new Error(`Unexpected fixture request: ${input}`);
  }));

  const { container } = render(<LocalAiWorkspace companyId={companyId} companyName="Fixture company" />);
  await waitFor(() => expect((screen.getByRole("combobox") as HTMLSelectElement).value).toBe(sourceId));
  const question = container.querySelector<HTMLTextAreaElement>('textarea[name="question"]')!;
  fireEvent.change(question, { target: { value: "PRIVATE-USER-A-QUESTION" } });
  fireEvent.submit(question.closest("form")!);
  await screen.findByText(checkpoint.answer, {}, { timeout: 4000 });
  expect(screen.getByText("PRIVATE-USER-A-DATABASE")).toBeTruthy();

  if (boundary === "logout") {
    fireEvent.click(container.querySelector("header button")!);
  } else if (boundary === "source-expiry") {
    sourcesExpired = true;
    fireEvent.click(container.querySelector("nav button:nth-child(2)")!);
    fireEvent.click(container.querySelector("section button")!);
  } else {
    taskSubmissionExpired = boundary === "submit-expiry";
    taskPollingExpired = boundary === "poll-expiry";
    fireEvent.change(question, { target: { value: "SECOND-PRIVATE-USER-A-QUESTION" } });
    fireEvent.submit(question.closest("form")!);
  }
  await waitFor(() => expect(container.querySelector('input[name="username"]')).toBeTruthy(), { timeout: 4000 });
  sourcesExpired = false;
  fireEvent.change(container.querySelector('input[name="username"]')!, { target: { value: "user-b" } });
  fireEvent.change(container.querySelector('input[name="password"]')!, { target: { value: "fixture-only-password" } });
  fireEvent.submit(container.querySelector('input[name="username"]')!.closest("form")!);
  await waitFor(() => expect((screen.getByRole("combobox") as HTMLSelectElement).value).toBe(sourceId));

  expect(screen.queryByText("PRIVATE-USER-A-QUESTION")).toBeNull();
  expect(screen.queryByText("SECOND-PRIVATE-USER-A-QUESTION")).toBeNull();
  expect(screen.queryByText(checkpoint.answer)).toBeNull();
  expect(screen.queryByText("PRIVATE-USER-A-DATABASE")).toBeNull();
}, 10000);

it("ignores user A's late source response after user B has signed in", async () => {
  let completeOldRequest!: (response: Response) => void;
  const oldRequest = new Promise<Response>((resolve) => { completeOldRequest = resolve; });
  let sourceCalls = 0;
  let activeUser: string | null = "user-a";
  vi.stubGlobal("fetch", vi.fn(async (input: string) => {
    if (input.startsWith("/api/local/session?")) return activeUser ? Response.json(context(activeUser)) : Response.json({}, { status: 401 });
    if (input.startsWith("/api/local/data-sources?")) {
      sourceCalls += 1;
      return sourceCalls === 1 ? oldRequest : Response.json([{ ...source, logicalName: "USER-B-SOURCE" }]);
    }
    if (input === "/api/local/session/logout") { activeUser = null; return Response.json({ ok: true }); }
    if (input === "/api/local/session/login") { activeUser = "user-b"; return Response.json({ ok: true, context: context(activeUser) }); }
    throw new Error(`Unexpected fixture request: ${input}`);
  }));
  const { container } = render(<LocalAiWorkspace companyId={companyId} companyName="Fixture company" />);
  await waitFor(() => expect(sourceCalls).toBe(1));
  fireEvent.click(container.querySelector("header button")!);
  await waitFor(() => expect(container.querySelector('input[name="username"]')).toBeTruthy());
  fireEvent.change(container.querySelector('input[name="username"]')!, { target: { value: "user-b" } });
  fireEvent.change(container.querySelector('input[name="password"]')!, { target: { value: "fixture-only-password" } });
  fireEvent.submit(container.querySelector('input[name="username"]')!.closest("form")!);
  await screen.findByRole("option", { name: "USER-B-SOURCE" });
  await act(async () => completeOldRequest(Response.json([{ ...source, logicalName: "PRIVATE-USER-A-SOURCE" }])));
  expect(screen.queryByRole("option", { name: "PRIVATE-USER-A-SOURCE" })).toBeNull();
  expect(screen.getByRole("option", { name: "USER-B-SOURCE" })).toBeTruthy();
});
