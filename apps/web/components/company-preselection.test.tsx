import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { CompanyPreselection } from "./company-preselection";

const navigation = vi.hoisted(() => vi.fn());
vi.mock("../lib/browser-login-navigation", () => ({ navigateBrowserLogin: navigation }));
const first = "22222222-2222-2222-2222-222222222222", second = "33333333-3333-3333-3333-333333333333";
const choices = { items: [{ companyId: first, companyName: "Company A" }, { companyId: second, companyName: "Company B" }] };
const fetcher = vi.fn();
beforeEach(() => { navigation.mockReset(); fetcher.mockReset().mockResolvedValue(Response.json(choices)); vi.stubGlobal("fetch", fetcher); });
afterEach(() => vi.unstubAllGlobals());
it("loads only server choices, renders names as text and never mounts private workspace", async () => {
  render(<CompanyPreselection />); await screen.findByRole("button", { name: "Company A" });
  expect(fetcher.mock.calls.map(([path]) => path)).toEqual(["/api/local/session/oidc/company"]);
  expect(screen.queryByRole("combobox", { name: "Nguồn dữ liệu" })).toBeNull();
  expect(document.querySelector('textarea,input[name="password"]')).toBeNull();
  expect(screen.queryByRole("button", { name: "Đăng xuất" })).toBeNull();
});
it("settles exactly one selected mutation before navigating and carries no tenant/role/token", async () => {
  render(<CompanyPreselection />); const button = await screen.findByRole("button", { name: "Company B" });
  let resolve!: (value: Response) => void;
  fetcher.mockImplementationOnce(() => new Promise<Response>((done) => { resolve = done; }));
  fireEvent.click(button); fireEvent.click(button);
  expect(fetcher).toHaveBeenCalledTimes(2); expect(navigation).not.toHaveBeenCalled();
  expect(JSON.parse(fetcher.mock.calls[1][1].body)).toEqual({ companyId: second });
  expect(fetcher.mock.calls[1][1].signal).toBeUndefined();
  await act(async () => resolve(Response.json({ redirectTo: "/" })));
  await waitFor(() => expect(navigation).toHaveBeenCalledWith("/"));
});
it("cancel is a single server mutation with no client session reconstruction", async () => {
  render(<CompanyPreselection />); await screen.findByRole("button", { name: "Company A" });
  fetcher.mockResolvedValueOnce(Response.json({ redirectTo: "/" }));
  fireEvent.click(screen.getByRole("button", { name: "Hủy và trở về trang chủ" }));
  await waitFor(() => expect(navigation).toHaveBeenCalledWith("/"));
  expect(fetcher.mock.calls[1][1].method).toBe("DELETE"); expect(fetcher.mock.calls[1][1].body).toBeUndefined();
});
it("unknown or revoked selection reply clears choices, never retries issuance and never exposes details", async () => {
  render(<CompanyPreselection />); await screen.findByRole("button", { name: "Company A" });
  fetcher.mockRejectedValueOnce(new Error("private-upstream-data"));
  fireEvent.click(screen.getByRole("button", { name: "Company A" }));
  await screen.findByRole("alert"); expect(screen.queryByRole("button", { name: "Company A" })).toBeNull();
  expect(screen.getByRole("link", { name: "Trở về trang chủ" })).toBeTruthy();
  expect(document.body.textContent).not.toContain("private-upstream-data"); expect(navigation).not.toHaveBeenCalled();
});
it("unmount fences a late cookie-mutating response without aborting or repainting", async () => {
  const { unmount } = render(<CompanyPreselection />); await screen.findByRole("button", { name: "Company A" });
  let resolve!: (value: Response) => void;
  fetcher.mockImplementationOnce(() => new Promise<Response>((done) => { resolve = done; }));
  fireEvent.click(screen.getByRole("button", { name: "Company A" })); unmount();
  await act(async () => resolve(Response.json({ redirectTo: "/" })));
  expect(navigation).not.toHaveBeenCalled();
});
it("expired/malformed discovery cannot fall back to a configured company or old SID", async () => {
  fetcher.mockResolvedValueOnce(Response.json({ items: [{ ...choices.items[0], roles: ["admin"] }] }));
  render(<CompanyPreselection />); await screen.findByRole("alert"); expect(fetcher).toHaveBeenCalledOnce();
  expect(navigation).not.toHaveBeenCalled(); expect(screen.queryByRole("button", { name: "Company A" })).toBeNull();
});
