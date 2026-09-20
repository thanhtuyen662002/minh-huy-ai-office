import { cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { demoDataSources } from "../lib/data-source-registry";
import { DataSourceRegistry } from "./data-source-registry";

afterEach(cleanup);

function renderRegistry() {
  render(<DataSourceRegistry companyId="internal" companyName="Minh Huy" sources={demoDataSources} />);
}

describe("DataSourceRegistry interactions", () => {
  it("edits metadata without asking for or exposing the stored secret", () => {
    renderRegistry();
    fireEvent.click(screen.getByRole("button", { name: /Sửa metadata company\.erp\.production/ }));
    const form = screen.getByRole("form", { name: /Sửa metadata · company\.erp\.production/ });
    expect(within(form).queryByLabelText(/bí mật/i)).toBeNull();
    const purpose = within(form).getByLabelText("Mục đích") as HTMLInputElement;
    fireEvent.change(purpose, { target: { value: "ERP vận hành đã cập nhật" } });
    fireEvent.submit(form);
    expect(screen.getByText("ERP vận hành đã cập nhật")).toBeTruthy();
    expect(screen.getByRole("status").textContent).toMatch(/không thay đổi/i);
    expect(document.body.textContent).not.toMatch(/password|connection string|secret reference/i);
  });

  it("requires an explicit secret rotation and never renders the replacement value after submit", () => {
    renderRegistry();
    fireEvent.click(screen.getByRole("button", { name: /Đổi thông tin bí mật company\.erp\.production/ }));
    const form = screen.getByRole("form", { name: /Đổi thông tin bí mật · company\.erp\.production/ });
    fireEvent.submit(form);
    expect(within(form).getByRole("alert").textContent).toMatch(/nhập thông tin bí mật mới/i);
    const secret = within(form).getByLabelText("Thông tin bí mật mới") as HTMLInputElement;
    expect(secret.type).toBe("password");
    fireEvent.change(secret, { target: { value: "replacement-only-in-form" } });
    fireEvent.submit(form);
    expect(screen.getByRole("status").textContent).toMatch(/không được hiển thị lại/i);
    expect(document.body.textContent).not.toContain("replacement-only-in-form");
  });

  it("creates company-scoped metadata without collecting a secret", () => {
    renderRegistry();
    fireEvent.click(screen.getByRole("button", { name: "Thêm nguồn dữ liệu" }));
    const form = screen.getByRole("form", { name: "Thêm nguồn dữ liệu" });
    fireEvent.change(within(form).getByLabelText("Tên logic"), { target: { value: "company.analytics.readonly" } });
    fireEvent.change(within(form).getByLabelText("Mục đích"), { target: { value: "Phân tích nội bộ" } });
    expect(within(form).queryByLabelText(/bí mật/i)).toBeNull();
    fireEvent.submit(form);
    expect(screen.getByRole("heading", { level: 3, name: "company.analytics.readonly" })).toBeTruthy();
    expect(screen.getByText("Phân tích nội bộ")).toBeTruthy();
    expect(screen.getByRole("status").textContent).toMatch(/thao tác đổi thông tin bí mật/i);
  });

  it("fails closed without company scope", () => {
    render(<DataSourceRegistry companyId="" companyName="Minh Huy" sources={demoDataSources} />);
    expect(screen.getByRole("alert")).toBeTruthy();
    expect(screen.queryByRole("heading", { name: /company\.erp\.production/ })).toBeNull();
  });
});
