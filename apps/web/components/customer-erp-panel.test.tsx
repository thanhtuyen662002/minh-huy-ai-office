import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { CustomerErpPanel } from "./customer-erp-panel";
import { erpMessages } from "../lib/customer-erp";

const source = { id: "12345678-1234-1234-1234-123456789abc", logicalName: "Khách A", isEnabled: true, bindingVersion: "1" };
function setup(options: { result?: string; sources?: boolean } = {}) {
  const bodies: { path: string; body: Record<string, unknown> }[] = [];
  const request = vi.fn(async (_generation: number, path: string, init?: RequestInit) => {
    if (!init?.body) return Response.json(options.sources ? [source] : []);
    bodies.push({ path, body: JSON.parse(String(init.body)) });
    return path.includes("connection-test") ? Response.json({ code: options.result ?? "ok", serverVersion: "16.0", tableCount: 12 }) : Response.json(source);
  });
  const props = { companyId: "22222222-2222-2222-2222-222222222222", generation: 1, isCurrent: vi.fn(() => true), validate: vi.fn(async () => true), request, onUnauthorized: vi.fn(), onSaved: vi.fn() };
  const view = render(<CustomerErpPanel {...props} />);
  return { ...view, props, bodies };
}
function fill() {
  for (const [label, value] of [["Tên khách hàng", "Khách A"], ["Máy chủ", "sql.example.test"], ["Tên database", "Demo"], ["Tài khoản", "reader"], ["Mật khẩu", "synthetic-only"]])
    fireEvent.change(screen.getByLabelText(label), { target: { value } });
}
describe("customer ERP setup", () => {
  it("requires a successful test, invalidates it on edits, saves structured fields and clears the password", async () => {
    const { bodies, props } = setup();
    fill();
    expect((screen.getByRole("button", { name: "Lưu" }) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(screen.getByRole("button", { name: "Kiểm tra kết nối" }));
    await screen.findByText(/SQL Server 16.0/);
    expect((screen.getByRole("button", { name: "Lưu" }) as HTMLButtonElement).disabled).toBe(false);
    fireEvent.change(screen.getByLabelText("Cổng"), { target: { value: "1444" } });
    expect((screen.getByRole("button", { name: "Lưu" }) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(screen.getByRole("button", { name: "Kiểm tra kết nối" }));
    await waitFor(() => expect((screen.getByRole("button", { name: "Lưu" }) as HTMLButtonElement).disabled).toBe(false));
    fireEvent.click(screen.getByRole("button", { name: "Lưu" }));
    await screen.findByText("Đã lưu kết nối ERP khách hàng.");
    expect((screen.getByLabelText("Mật khẩu") as HTMLInputElement).value).toBe("");
    expect(props.onSaved).toHaveBeenCalledOnce();
    expect(bodies.at(-1)?.body.operationId).toEqual(expect.any(String));
    expect(bodies.at(-1)?.body).not.toHaveProperty("connectionString");
  });
  it("splits a pasted SQL Server address with its port so the connection can be tested", async () => {
    const { bodies } = setup(); fill();
    fireEvent.change(screen.getByLabelText("Máy chủ"), { target: { value: "123.25.21.20,17456 " } });
    expect((screen.getByLabelText("Máy chủ") as HTMLInputElement).value).toBe("123.25.21.20");
    expect((screen.getByLabelText("Cổng") as HTMLInputElement).value).toBe("17456");
    fireEvent.change(screen.getByLabelText("Máy chủ"), { target: { value: "sql.example.test:1500" } });
    expect((screen.getByLabelText("Cổng") as HTMLInputElement).value).toBe("1500");
    expect((screen.getByRole("button", { name: "Kiểm tra kết nối" }) as HTMLButtonElement).disabled).toBe(false);
    fireEvent.click(screen.getByRole("button", { name: "Kiểm tra kết nối" }));
    await screen.findByText(/SQL Server 16.0/);
    expect(bodies[0].body).toMatchObject({ host: "sql.example.test", port: 1500 });
  });
  it.each(["unreachable", "login_failed", "database_not_found", "not_read_only", "missing_view_definition"])("shows safe Vietnamese message for %s and keeps save disabled", async code => {
    setup({ result: code }); fill();
    fireEvent.click(screen.getByRole("button", { name: "Kiểm tra kết nối" }));
    await screen.findByText(erpMessages[code]);
    expect((screen.getByRole("button", { name: "Lưu" }) as HTMLButtonElement).disabled).toBe(true);
    if (code === "not_read_only") expect(screen.getByLabelText("SQL tạo tài khoản chỉ đọc").textContent).toContain("USE [Demo]");
    expect(screen.queryByText("synthetic-only")).toBeNull();
  });
  it("rotates with the listed binding version and retyped credentials", async () => {
    const { bodies } = setup({ sources: true });
    fireEvent.click(await screen.findByRole("button", { name: "Đổi mật khẩu Khách A" }));
    expect((screen.getByLabelText("Mật khẩu") as HTMLInputElement).value).toBe("");
    fill(); fireEvent.click(screen.getByRole("button", { name: "Kiểm tra kết nối" }));
    await waitFor(() => expect((screen.getByRole("button", { name: "Lưu" }) as HTMLButtonElement).disabled).toBe(false));
    fireEvent.click(screen.getByRole("button", { name: "Lưu" }));
    await screen.findByText("Đã lưu kết nối ERP khách hàng.");
    expect(bodies.at(-1)?.path).toContain(`${source.id}/credentials`);
    expect(bodies.at(-1)?.body.bindingVersion).toBe("1");
  });
  it("keeps the exact operation id and frozen fields after a lost save response", async () => {
    const { props, bodies } = setup(); fill();
    fireEvent.click(screen.getByRole("button", { name: "Kiểm tra kết nối" }));
    await waitFor(() => expect((screen.getByRole("button", { name: "Lưu" }) as HTMLButtonElement).disabled).toBe(false));
    props.request.mockImplementationOnce(async (_g, path, init) => { bodies.push({ path, body: JSON.parse(String(init?.body)) }); throw Error("private failure"); });
    fireEvent.click(screen.getByRole("button", { name: "Lưu" }));
    await screen.findByRole("button", { name: "Thử lại lưu" });
    expect((screen.getByLabelText("Mật khẩu") as HTMLInputElement).disabled).toBe(false); // disabled via fieldset
    expect(screen.getByLabelText("Mật khẩu").closest("fieldset")?.disabled).toBe(true);
    fireEvent.click(screen.getByRole("button", { name: "Thử lại lưu" }));
    await screen.findByText("Đã lưu kết nối ERP khách hàng.");
    expect(bodies.at(-1)?.body).toEqual(bodies.at(-2)?.body);
  });
  it("discards a late test reply after the session generation changes", async () => {
    const { props } = setup(); fill();
    let resolve!: (response: Response) => void;
    props.request.mockImplementationOnce(() => new Promise<Response>(done => { resolve = done; }));
    fireEvent.click(screen.getByRole("button", { name: "Kiểm tra kết nối" }));
    await waitFor(() => expect(resolve).toBeDefined());
    props.isCurrent.mockReturnValue(false);
    await act(async () => resolve(Response.json({ code: "ok" })));
    expect(screen.queryByText(erpMessages.ok)).toBeNull();
  });
});
