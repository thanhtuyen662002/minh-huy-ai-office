import { webcrypto } from "node:crypto";
import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { LocalAiWorkspace } from "./local-ai-workspace";
import { ownedSubmissionFixture } from "./task-submission-test-fixture";

const companyId = "22222222-2222-4222-8222-222222222222", sourceId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", otherSource = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
const taskId = "33333333-3333-4333-8333-333333333333", userId = "66666666-6666-4666-8666-666666666666";
const question = "Tồn kho 😀 �", answer = "PRIVATE_SAVED_ANSWER";
const source = { id: sourceId, logicalName: "Owned source", kind: "SqlServer", environment: "Test", purpose: "Fixture", allowRead: true, allowWrite: false, maxConcurrency: 1, isEnabled: true };
const checkpoint = { answer, provider: "fixture", model: "fixture", evidence: "fixture", usage: { inputTokens: 1, outputTokens: 1, totalTokens: 2 }, erpEvidence: { databaseName: "OWNED_FIXTURE", tableCount: 1, sampledTableCount: 1, topTables: [] } };
function backend(fault: "prepare" | "submit" | "none" = "none") {
  const submission = ownedSubmissionFixture(companyId, taskId), state = { fault, active: true };
  const fetcher = vi.fn(async (url: string, init?: RequestInit) => {
    if (url.startsWith("/api/local/session?")) return state.active ? Response.json({ tenantId: "fixture", companyId, userId, roles: ["viewer"] }) : Response.json({}, { status: 401 });
    if (url === "/api/local/session/logout") { state.active = false; return Response.json({ ok: true }); }
    if (url.startsWith("/api/local/data-sources?")) return Response.json([source, { ...source, id: otherSource, logicalName: "Other source" }]);
    const result = await submission(url, init);
    if (result) {
      if (init?.method === "POST" && state.fault === (url.includes("/submit?") ? "submit" : "prepare")) {
        state.fault = "none"; return Response.json({ error: "unavailable" }, { status: 503 });
      }
      return result;
    }
    if (url.startsWith(`/api/local/tasks/${taskId}?`)) return Response.json({ taskId, taskStatus: 2, stepStatus: 2, dispatchState: 2, attempt: 1, resultPayloadJson: JSON.stringify(checkpoint), failureReason: null });
    if (url.startsWith("/api/local/tasks?")) return Response.json({ companyId, items: [], offset: 0, limit: 25, hasMore: false });
    throw new Error("Unexpected fixture route");
  });
  vi.stubGlobal("fetch", fetcher);
  return { state, fetcher, posts: () => fetcher.mock.calls.filter(([url, init]) => url.startsWith("/api/local/tasks/intents") && init?.method === "POST") };
}
async function open() {
  const view = render(<LocalAiWorkspace companyId={companyId} companyName="Owned fixture" />);
  await screen.findByRole("option", { name: "Owned source" }); return view;
}
function compose(container: HTMLElement, value = question) {
  const textarea = container.querySelector("textarea")!;
  fireEvent.change(textarea, { target: { value } }); fireEvent.submit(textarea.closest("form")!); return textarea.closest("form")!;
}
beforeEach(() => { vi.stubGlobal("crypto", webcrypto); vi.stubGlobal("BroadcastChannel", undefined); });
afterEach(() => { vi.unstubAllGlobals(); vi.restoreAllMocks(); });

it.each(["\uFEFFTồn kho", "Tồn kho\uFEFF", "\uFEFF"])("composer/reload/deliberate send preserve exact Core-valid FEFF question %j", async exactQuestion => {
  const server = backend("prepare"); let view = await open();
  compose(view.container, " \u00A0" + exactQuestion + "\u3000 ");
  await screen.findByRole("button", { name: "Thử lại đúng yêu cầu" });
  const preparedBody = server.posts()[0][1]!.body;
  expect(JSON.parse(preparedBody as string).question).toBe(exactQuestion);
  view.unmount(); view = await open(); fireEvent.click(screen.getByRole("button", { name: "Công việc" }));
  const saved = await screen.findByRole("button", { name: /Lấy yêu cầu đã lưu/ });
  expect(server.posts()).toHaveLength(1); fireEvent.click(saved);
  await screen.findByRole("button", { name: "Gửi yêu cầu đã lưu" }); expect(server.posts()).toHaveLength(1);
  fireEvent.click(screen.getByRole("button", { name: "Gửi yêu cầu đã lưu" }));
  await screen.findByText(answer, {}, { timeout: 4000 }); expect(server.posts()).toHaveLength(2);
  expect(server.posts()[0][1]!.body).toBe(preparedBody);
  expect(server.posts()[1][0]).toContain(JSON.parse(preparedBody as string).operationId);
  expect(Object.keys(JSON.parse(server.posts()[1][1]!.body as string))).toEqual(["inputFingerprint"]);
});

it("the real workspace/session hook captures one operation across synchronous duplicate submits", async () => {
  const server = backend(), view = await open(), form = compose(view.container);
  fireEvent.submit(form); await screen.findByText(answer, {}, { timeout: 4000 });
  expect(server.posts()).toHaveLength(2); expect(screen.getAllByText(question)).toHaveLength(1);
  const [prepare, submit] = server.posts(); const input = JSON.parse(prepare[1]!.body as string);
  expect(input).toMatchObject({ dataSourceId: sourceId, question }); expect(submit[0]).toContain(input.operationId);
  expect(Object.keys(JSON.parse(submit[1]!.body as string))).toEqual(["inputFingerprint"]);
});
it("unknown accepted replies retain the request and read-only reconciliation does not resend", async () => {
  const server = backend("submit"), view = await open(); compose(view.container);
  await screen.findByRole("button", { name: "Thử lại đúng yêu cầu" });
  expect((view.container.querySelector("textarea") as HTMLTextAreaElement).disabled).toBe(true);
  expect(screen.queryByText(/Đã vào hàng đợi/)).toBeNull(); expect(screen.queryByText(answer)).toBeNull();
  fireEvent.click(screen.getByRole("button", { name: "Kiểm tra trạng thái yêu cầu" }));
  await waitFor(() => expect(screen.queryByRole("button", { name: "Thử lại đúng yêu cầu" })).toBeNull());
  expect(server.posts()).toHaveLength(2); expect(screen.queryByText(answer)).toBeNull();
  expect(server.fetcher.mock.calls.filter(([url]) => url.startsWith(`/api/local/tasks/${taskId}?`))).toHaveLength(0);
});
it("deliberate unknown202 retry uses the same operation/fingerprint despite another selected source", async () => {
  const server = backend("submit"), view = await open(); compose(view.container);
  await screen.findByRole("button", { name: "Thử lại đúng yêu cầu" });
  fireEvent.change(screen.getByRole("combobox", { name: "Nguồn dữ liệu" }), { target: { value: otherSource } });
  fireEvent.click(screen.getByRole("button", { name: "Thử lại đúng yêu cầu" })); await screen.findByText(answer, {}, { timeout: 4000 });
  expect(server.posts()).toHaveLength(3);
  expect(server.posts()[1][0]).toBe(server.posts()[2][0]); expect(server.posts()[1][1]!.body).toBe(server.posts()[2][1]!.body);
  expect(JSON.parse(server.posts()[0][1]!.body as string).dataSourceId).toBe(sourceId);
});
it("lost preparation replies retry unchanged operation and exact question before execution", async () => {
  const server = backend("prepare"), view = await open(); compose(view.container);
  await screen.findByRole("button", { name: "Thử lại đúng yêu cầu" });
  expect(server.posts()).toHaveLength(1); fireEvent.click(screen.getByRole("button", { name: "Thử lại đúng yêu cầu" }));
  await screen.findByText(answer, {}, { timeout: 4000 }); expect(server.posts()).toHaveLength(3);
  expect(server.posts()[0][1]!.body).toBe(server.posts()[1][1]!.body);
});
it("reload recovers an unsent server intent by owner reads and waits for an explicit send", async () => {
  const server = backend("prepare"); let view = await open(); compose(view.container);
  await screen.findByRole("button", { name: "Thử lại đúng yêu cầu" }); view.unmount(); view = await open();
  expect(screen.queryByLabelText("Yêu cầu đang giữ")).toBeNull();
  fireEvent.click(screen.getByRole("button", { name: "Công việc" }));
  await screen.findByRole("button", { name: `Lấy yêu cầu đã lưu ${question}` }); expect(server.posts()).toHaveLength(1);
  fireEvent.click(screen.getByRole("button", { name: `Lấy yêu cầu đã lưu ${question}` }));
  await screen.findByRole("button", { name: "Gửi yêu cầu đã lưu" }); expect(server.posts()).toHaveLength(1);
  expect(screen.getByText(question)).toBeTruthy(); fireEvent.click(screen.getByRole("button", { name: "Gửi yêu cầu đã lưu" }));
  await screen.findByText(answer, {}, { timeout: 4000 }); expect(server.posts()).toHaveLength(2);
});
it("explicit new operation warns about earlier acceptance and preserves the earlier server intent", async () => {
  const server = backend("prepare"), view = await open(); compose(view.container);
  await screen.findByRole("button", { name: "Thử lại đúng yêu cầu" }); expect(screen.getByText(/tạo yêu cầu khác có thể xử lý thêm một lần/)).toBeTruthy();
  fireEvent.click(screen.getByRole("button", { name: "Tạo yêu cầu khác" }));
  server.state.fault = "prepare"; compose(view.container, "Tra cứu nhập xuất"); await screen.findByRole("button", { name: "Thử lại đúng yêu cầu" });
  const inputs = server.posts().map(([, init]) => JSON.parse(init!.body as string)); expect(inputs[0].operationId).not.toBe(inputs[1].operationId);
  fireEvent.click(screen.getByRole("button", { name: "Mở Công việc và yêu cầu đã lưu" }));
  const region = await screen.findByRole("region", { name: "Yêu cầu đã lưu" });
  await within(region).findByText(question); expect(within(region).getByText("Tra cứu nhập xuất")).toBeTruthy(); expect(server.posts()).toHaveLength(2);
});
it("logout clears an uncertain operation and late reconciliation cannot restore it", async () => {
  const server = backend("submit"), view = await open(); compose(view.container); await screen.findByRole("button", { name: "Thử lại đúng yêu cầu" });
  fireEvent.click(screen.getByRole("button", { name: "Đăng xuất" })); await screen.findByText("Local Pilot");
  expect(screen.queryByText(question)).toBeNull(); expect(screen.queryByRole("button", { name: "Thử lại đúng yêu cầu" })).toBeNull(); expect(server.posts()).toHaveLength(2);
});
it("navigation to saved requests while an unknown outcome remains never automatically retries", async () => {
  const server = backend("submit"), view = await open(); compose(view.container); await screen.findByRole("button", { name: "Thử lại đúng yêu cầu" });
  fireEvent.click(screen.getByRole("button", { name: "Mở Công việc và yêu cầu đã lưu" }));
  await screen.findByText(/Đã nhận · xem Công việc/); expect(server.posts()).toHaveLength(2);
  fireEvent.click(view.container.querySelector("nav button")!); await screen.findByRole("button", { name: "Thử lại đúng yêu cầu" }); expect(server.posts()).toHaveLength(2);
});
it("navigation to saved requests remains read-only while an existing polling loop is active", async () => {
  const server = backend(), view = await open(); compose(view.container);
  await screen.findByText(/Đã vào hàng đợi/); fireEvent.click(screen.getByRole("button", { name: "Mở Công việc và yêu cầu đã lưu" }));
  await screen.findByText(/Đã nhận · xem Công việc/); expect(server.posts()).toHaveLength(2);
  await act(async () => Promise.resolve()); view.unmount();
});
