import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { CompanyShell } from "../components/company-shell";
import Home from "./page";

afterEach(() => {
  cleanup();
});

describe("company-scoped product shell", () => {
  it("renders the data source registry inside an explicit active-company context", () => {
    render(<Home />);

    expect(screen.getByRole("main")).toBeTruthy();
    expect(screen.getByRole("heading", { level: 1, name: "AI Office" })).toBeTruthy();
    expect(screen.getByRole("region", { name: "Công ty đang làm việc" })).toBeTruthy();
    expect(screen.getByRole("heading", { level: 2, name: "Nguồn dữ liệu · Minh Huy" })).toBeTruthy();
    expect(screen.getByText("Nhân viên nội bộ")).toBeTruthy();
    expect(screen.getByText("company.erp.production")).toBeTruthy();
  });

  it("fails closed when tenant/company/user scope is incomplete", () => {
    render(
      <CompanyShell
        membership={{
          tenantId: "minh-huy",
          companyId: "",
          companyName: "Minh Huy",
          userId: "demo-user",
          userName: "Nhân viên nội bộ",
          roles: [],
        }}
      />,
    );

    expect(screen.getByRole("heading", { name: "Chưa có phạm vi làm việc" })).toBeTruthy();
    expect(screen.getByText(/sẽ không hiển thị dữ liệu công ty/i)).toBeTruthy();
    expect(screen.queryByRole("heading", { name: /Nguồn dữ liệu/ })).toBeNull();
  });
});
