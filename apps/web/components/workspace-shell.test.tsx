import { fireEvent, render, screen } from "@testing-library/react";
import { expect, it, vi } from "vitest";
import { WorkspaceShell } from "./workspace-shell";

const shell = (extra: Partial<Parameters<typeof WorkspaceShell>[0]> = {}) => <WorkspaceShell section="tong-quan" onSection={vi.fn()}
  companyId="a2222222-2222-4222-8222-222222222222" companyName="Minh Huy" companies={[]} companyDisabled={false} onSelectCompany={vi.fn()}
  roleLabel="Quản trị viên" accountLabel="Tài khoản doanh nghiệp" onSignOut={vi.fn()} {...extra}>nội dung</WorkspaceShell>;

it("searches tickets from the header and focuses the box with Ctrl K", () => {
  const onSearch = vi.fn();
  render(shell({ onSearch, userName: "Oscar", counts: { "cong-viec": "9" }, bot: { label: "Bot Zalo đang chạy", running: true } }));
  const box = screen.getByRole("searchbox", { name: "Tìm phiếu theo khách hàng" });
  fireEvent.keyDown(window, { key: "k", ctrlKey: true });
  expect(document.activeElement).toBe(box);
  fireEvent.change(box, { target: { value: "  Hoà Phát  " } });
  fireEvent.submit(box.closest("form")!);
  expect(onSearch).toHaveBeenCalledWith("Hoà Phát");
  expect(screen.getByText("Bot Zalo đang chạy")).toBeTruthy();
  expect(screen.getByText("Oscar")).toBeTruthy();
  // The open-ticket count is decoration; the menu entry keeps its plain name.
  expect(screen.getByRole("button", { name: "Công việc" }).textContent).toContain("9");
});
it("shows the company name and no search box without a ticket search", () => {
  render(shell());
  expect(screen.queryByRole("search")).toBeNull();
  expect(screen.getByRole("heading", { name: "Minh Huy" })).toBeTruthy();
  expect(screen.getByText("Tài khoản doanh nghiệp")).toBeTruthy();
});
