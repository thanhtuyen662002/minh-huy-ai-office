import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { ErpChatClients } from "./erp-chat-clients";
import { isErpChatClientCreate, isErpChatClientUpdate, parseErpChatSecret } from "../lib/erp-chat";

const account = { id: "aaaaaaaa-1234-1234-1234-123456789abc", name: "Mak", monthlyBudget: null, questionsPerMinute: null, version: "1", sourceIds: [] };
const client = { id: "cccccccc-1234-1234-1234-123456789abc", name: "Web ERP Mak", billingAccountId: account.id, isEnabled: true, version: "4",
  createdAtUtc: "2026-11-13T00:00:00Z" };
const key = "q".repeat(43) + "=";
afterEach(() => vi.restoreAllMocks());

function setup(clients: unknown[] = [client]) {
  const sent: { path: string; method?: string; body: unknown }[] = [];
  const request = vi.fn(async (_generation: number, path: string, init?: RequestInit) => {
    if (!init?.body) return Response.json(clients);
    const body = JSON.parse(String(init.body)); sent.push({ path, method: init.method, body });
    return (body as { rotateKey?: boolean }).rotateKey === false ? Response.json({ updated: true })
      : Response.json({ client: { ...client, version: "5" }, signingKey: key });
  });
  render(<ErpChatClients companyId="22222222-2222-2222-2222-222222222222" generation={1} isCurrent={() => true} validate={async () => true}
    request={request} onUnauthorized={vi.fn()} accounts={[account]} />);
  return { sent };
}

describe("web ERP clients", () => {
  it("creates a client for a customer and shows its key once", async () => {
    const { sent } = setup([]);
    fireEvent.click(await screen.findByRole("button", { name: "Thêm web ERP" }));
    fireEvent.change(screen.getByLabelText("Tên web ERP"), { target: { value: "Web ERP Mak" } });
    fireEvent.click(screen.getByRole("button", { name: "Tạo và lấy khoá" }));
    await waitFor(() => expect(sent).toHaveLength(1));
    expect(sent[0]).toMatchObject({ method: "POST", body: { name: "Web ERP Mak", billingAccountId: account.id } });
    expect(await screen.findByText(key)).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Đã lưu, đóng" }));
    expect(screen.queryByText(key)).toBeNull();
  });

  it("replaces a key only after confirmation, and turns a client off at its version", async () => {
    const { sent } = setup();
    const confirm = vi.spyOn(window, "confirm").mockReturnValueOnce(false).mockReturnValueOnce(true);
    fireEvent.click(await screen.findByRole("button", { name: "Tạo khoá mới Web ERP Mak" }));
    expect(sent).toHaveLength(0);
    fireEvent.click(screen.getByRole("button", { name: "Tạo khoá mới Web ERP Mak" }));
    expect(await screen.findByText(key)).toBeTruthy();
    expect(sent[0].body).toEqual({ isEnabled: true, rotateKey: true, version: "4", requireScopes: false });
    fireEvent.click(screen.getByRole("button", { name: "Tắt Web ERP Mak" }));
    await waitFor(() => expect(sent).toHaveLength(2));
    expect(sent[1].body).toEqual({ isEnabled: false, rotateKey: false, version: "4", requireScopes: false });
    expect(confirm).toHaveBeenCalledTimes(2);
  });

  it("creates a client that requires data scopes, and toggles the requirement", async () => {
    const { sent } = setup([{ ...client, requireScopes: true }]);
    const relax = await screen.findByRole("button", { name: "Bỏ bắt buộc phạm vi Web ERP Mak" });
    expect(relax.getAttribute("aria-pressed")).toBe("true");
    fireEvent.click(relax);
    await waitFor(() => expect(sent).toHaveLength(1));
    expect(sent[0].body).toEqual({ isEnabled: true, rotateKey: false, version: "4", requireScopes: false });

    fireEvent.click(screen.getByRole("button", { name: "Thêm web ERP" }));
    fireEvent.change(screen.getByLabelText("Tên web ERP"), { target: { value: "Web ERP Nông dân" } });
    fireEvent.click(screen.getByRole("checkbox", { name: /Bắt buộc gửi phạm vi dữ liệu/ }));
    fireEvent.click(screen.getByRole("button", { name: "Tạo và lấy khoá" }));
    await waitFor(() => expect(sent).toHaveLength(2));
    expect(sent[1].body).toEqual({ name: "Web ERP Nông dân", billingAccountId: account.id, requireScopes: true });
  });

  it("validates bodies and the one-time key shape", () => {
    expect(isErpChatClientCreate({ name: "x", billingAccountId: account.id })).toBe(true);
    expect(isErpChatClientCreate({ name: "x", billingAccountId: account.id, requireScopes: true })).toBe(true);
    expect(isErpChatClientCreate({ name: "x", billingAccountId: account.id, requireScopes: "yes" })).toBe(false);
    expect(isErpChatClientCreate({ name: "x", billingAccountId: account.id, extra: true })).toBe(false);
    expect(isErpChatClientCreate({ name: "x", billingAccountId: "nope" })).toBe(false);
    expect(isErpChatClientUpdate({ isEnabled: true, rotateKey: false, version: "1" })).toBe(true);
    expect(isErpChatClientUpdate({ isEnabled: true, rotateKey: false, version: "1", requireScopes: false })).toBe(true);
    expect(isErpChatClientUpdate({ isEnabled: true, rotateKey: false, version: "0" })).toBe(false);
    expect(parseErpChatSecret({ client, signingKey: "short" })).toBeNull();
  });
});
