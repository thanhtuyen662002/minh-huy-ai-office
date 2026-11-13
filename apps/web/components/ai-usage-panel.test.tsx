import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { AiUsagePanel } from "./ai-usage-panel";

const source = "12345678-1234-1234-1234-123456789abc", other = "cccccccc-1234-1234-1234-123456789abc", account = "aaaaaaaa-1234-1234-1234-123456789abc";
const totals = (cost: number, unpriced = 0) => ({ questions: 3, calls: 7, inputTokens: 12000, cachedInputTokens: 0, outputTokens: 800, cost, unpricedCalls: unpriced });
const report = { month: "2026-11", currency: "VND", totals: totals(90000, 2),
  accounts: [{ accountId: account, name: "Mak", monthlyBudget: 100000, totals: totals(90000) }],
  users: [{ userId: "bbbbbbbb-1234-1234-1234-123456789abc", name: "Tuyên", totals: totals(90000) }],
  models: [{ providerId: "company-shared", model: "gpt-unpriced", totals: totals(0, 2) }],
  days: [{ date: "2026-11-12", totals: totals(90000) }] };
const settings = { prices: [], accounts: [{ id: account, name: "Mak", monthlyBudget: 100000, questionsPerMinute: null, version: "3", sourceIds: [source] }],
  sources: [{ id: source, logicalName: "Mak ERP" }, { id: other, logicalName: "ATC" }], userQuestionsPerMinute: 20 };

function setup(reply?: (path: string) => Response | undefined) {
  const sent: { path: string; method?: string; body: unknown }[] = [];
  const request = vi.fn(async (_generation: number, path: string, init?: RequestInit) => {
    if (init?.body) {
      sent.push({ path, method: init.method, body: JSON.parse(String(init.body)) });
      return reply?.(path) ?? Response.json({});
    }
    return Response.json(path.includes("/erp-chat/") ? [] : path.includes("/settings") ? settings : report);
  });
  render(<AiUsagePanel companyId="22222222-2222-2222-2222-222222222222" generation={1} isCurrent={() => true}
    validate={async () => true} request={request} onUnauthorized={vi.fn()} />);
  return { request, sent };
}

describe("AI usage panel", () => {
  it("shows the month by customer with budget use, and warns about unpriced calls", async () => {
    setup();
    expect(await screen.findByText(/90% hạn mức/)).toBeTruthy();
    expect(screen.getByText(/2 lượt gọi AI chưa có giá/)).toBeTruthy();
    expect(screen.getByText("Tuyên")).toBeTruthy();
    expect(screen.getByText("12/11/2026")).toBeTruthy();
  });

  it("edits a customer at its version, reading Vietnamese thousand separators", async () => {
    const { sent } = setup();
    fireEvent.click(await screen.findByRole("button", { name: "Sửa Mak" }));
    fireEvent.change(screen.getByLabelText("Hạn mức tháng (đ)"), { target: { value: "2.500.000" } });
    fireEvent.change(screen.getByLabelText("Câu hỏi mỗi phút"), { target: { value: "30" } });
    fireEvent.click(screen.getByLabelText("ATC"));
    fireEvent.click(screen.getByRole("button", { name: "Lưu khách hàng" }));
    await waitFor(() => expect(sent).toHaveLength(1));
    expect(sent[0].path).toContain(`/api/local/ai-usage/accounts/${account}?`);
    expect(sent[0].method).toBe("PUT");
    expect(sent[0].body).toEqual({ name: "Mak", monthlyBudget: 2500000, questionsPerMinute: 30, sourceIds: [source, other], version: "3" });
    expect(await screen.findByText("Đã lưu khách hàng.")).toBeTruthy();
  });

  it("prefills a price for the unpriced model, defaults the cached rate and reports a conflict", async () => {
    const { sent } = setup(path => path.includes("prices") ? Response.json({ code: "conflict" }, { status: 409 }) : undefined);
    fireEvent.click(await screen.findByRole("button", { name: "Thêm giá" }));
    expect((screen.getByLabelText("Model") as HTMLInputElement).value).toBe("gpt-unpriced");
    fireEvent.change(screen.getByLabelText("Token vào"), { target: { value: "75000" } });
    fireEvent.change(screen.getByLabelText("Token ra"), { target: { value: "375000" } });
    fireEvent.click(screen.getByRole("button", { name: "Lưu giá" }));
    await waitFor(() => expect(sent).toHaveLength(1));
    expect(sent[0].body).toEqual({ model: "gpt-unpriced", inputPerMillion: 75000, cachedInputPerMillion: 75000, outputPerMillion: 375000 });
    expect((await screen.findByRole("alert")).textContent).toContain("người khác thay đổi");
  });
});
