import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { CompanyMemberPanel } from "./company-member-panel";

const company = "22222222-2222-2222-2222-222222222222", owner = "11111111-1111-1111-1111-111111111111";
const invitation = { companyId: company, id: "33333333-3333-3333-3333-333333333333", email: "member@example.com", roles: ["viewer"],
  createdAtUtc: "2026-10-26T00:00:00+00:00", expiresAtUtc: "2026-11-02T00:00:00+00:00", acceptedAtUtc: null, revokedAtUtc: null, version: "1" };
function fixture(override: (url: string, init?: RequestInit) => Response | Promise<Response> | undefined = () => undefined) {
  const invitations = [invitation];
  const validate = vi.fn(async () => true), unauthorized = vi.fn();
  const request = vi.fn(async (_generation: number, url: string, init?: RequestInit) => {
    const custom = override(url, init); if (custom !== undefined) return custom;
    if (url === "/api/local/company/invitations?companyId=" + company) {
      const input = JSON.parse(init!.body as string);
      const created = { ...invitation, id: "44444444-4444-4444-4444-444444444444", email: input.email, roles: input.roles };
      invitations.push(created); return Response.json(created, { status: 201 });
    }
    if (url.includes("/revoke?")) {
      const index = invitations.findIndex(row => row.id === invitation.id); if (index >= 0) invitations.splice(index, 1);
      return Response.json({ ...invitation, revokedAtUtc: "2026-10-26T01:00:00+00:00", version: "2" });
    }
    if (url.startsWith("/api/local/company/invitations?")) return Response.json({ companyId: company, items: invitations, offset: 0, limit: 25, hasMore: false });
    return Response.json({ companyId: company, items: [], offset: 0, limit: 25, hasMore: false });
  });
  const view = render(<CompanyMemberPanel companyId={company} userId={owner} generation={1} isCurrent={() => true}
    validate={validate} request={request} onUnauthorized={unauthorized} />);
  return { request, validate, unauthorized, invitations, view };
}
afterEach(() => { vi.unstubAllGlobals(); });

const temporaryPassword = "SyntheticTemporaryPassword123!";
async function submitNewMember() {
  await screen.findByText("member@example.com");
  fireEvent.change(screen.getByLabelText("Email thành viên"), { target: { value: "new@example.com" } });
  fireEvent.click(screen.getByRole("button", { name: "Thêm thành viên" }));
  await screen.findByText("Đã tạo lời mời thành viên.");
}
const provisioned = (url: string) => url === `/api/local/company/invitations?companyId=${company}`
  ? Response.json({ ...invitation, temporaryPassword }, { status: 201 }) : undefined;

it("shows and copies the temporary password once with a warning and sign-in link", async () => {
  const writeText = vi.fn(async () => {}); vi.stubGlobal("navigator", { clipboard: { writeText } });
  fixture(provisioned); await submitNewMember();
  expect((screen.getByLabelText("Mật khẩu tạm thời") as HTMLInputElement).value).toBe(temporaryPassword);
  expect(screen.getByText(/sẽ không được hiển thị lại/)).toBeTruthy();
  expect(screen.getByLabelText("Liên kết đăng nhập để gửi cho thành viên")).toBeTruthy();
  fireEvent.click(screen.getByRole("button", { name: "Sao chép mật khẩu" }));
  await waitFor(() => expect(writeText).toHaveBeenCalledWith(temporaryPassword));
  fireEvent.click(screen.getByRole("button", { name: "Ẩn mật khẩu" }));
  expect(screen.queryByLabelText("Mật khẩu tạm thời")).toBeNull();
  expect(localStorage.length).toBe(0); expect(sessionStorage.length).toBe(0);
});

it("clears the password on reload and remount, never restoring it from the invitation list", async () => {
  const { view } = fixture(provisioned); await submitNewMember();
  fireEvent.click(screen.getByRole("button", { name: "Tải lại thành viên" }));
  await screen.findByText("member@example.com");
  expect(screen.queryByLabelText("Mật khẩu tạm thời")).toBeNull();
  view.unmount(); fixture(); await screen.findByText("member@example.com");
  expect(screen.queryByLabelText("Mật khẩu tạm thời")).toBeNull();
});

it("does not show a password for an existing account", async () => {
  fixture(); await submitNewMember(); expect(screen.queryByLabelText("Mật khẩu tạm thời")).toBeNull();
});

it("clears a displayed password when fresh authority validation fails", async () => {
  const { validate } = fixture(provisioned); await submitNewMember();
  validate.mockResolvedValue(false);
  fireEvent.click(screen.getByRole("button", { name: "Sao chép mật khẩu" }));
  await waitFor(() => expect(screen.queryByLabelText("Mật khẩu tạm thời")).toBeNull());
});

it("does not copy a dismissed password after delayed authority validation", async () => {
  const writeText = vi.fn(async () => {}); vi.stubGlobal("navigator", { clipboard: { writeText } });
  const { validate } = fixture(provisioned); await submitNewMember();
  let resolve!: (valid: boolean) => void;
  validate.mockImplementationOnce(() => new Promise<boolean>(done => { resolve = done; }));
  fireEvent.click(screen.getByRole("button", { name: "Sao chép mật khẩu" }));
  fireEvent.click(screen.getByRole("button", { name: "Ẩn mật khẩu" }));
  resolve(true);
  await waitFor(() => expect(screen.queryByLabelText("Mật khẩu tạm thời")).toBeNull());
  expect(writeText).not.toHaveBeenCalled();
});

it("discards a displayed password when authority validation throws", async () => {
  const { validate } = fixture(provisioned); await submitNewMember();
  validate.mockRejectedValueOnce(new Error("Unavailable"));
  fireEvent.click(screen.getByRole("button", { name: "Sao chép mật khẩu" }));
  await waitFor(() => expect(screen.queryByLabelText("Mật khẩu tạm thời")).toBeNull());
});

it("creates a Vietnamese role invitation and reloads the pending list", async () => {
  const { request } = fixture(); await screen.findByText("member@example.com");
  fireEvent.change(screen.getByLabelText("Email thành viên"), { target: { value: "new@example.com" } });
  fireEvent.change(screen.getByLabelText("Vai trò lời mời"), { target: { value: "admin" } });
  fireEvent.click(screen.getByRole("button", { name: "Thêm thành viên" }));
  await screen.findByText("Đã tạo lời mời thành viên.");
  expect(screen.getByText("new@example.com")).toBeTruthy();
  const call = request.mock.calls.find(([, url]) => url === `/api/local/company/invitations?companyId=${company}`)!;
  expect(JSON.parse(call[2]!.body as string)).toEqual({ email: "new@example.com", roles: ["admin"] });
  expect(JSON.stringify(call)).not.toContain(owner);
});

it("lists and revokes pending invitations through company-scoped routes", async () => {
  const { request } = fixture(); await screen.findByText("member@example.com");
  fireEvent.click(screen.getByRole("button", { name: "Thu hồi lời mời member@example.com" }));
  await screen.findByText("Đã thu hồi lời mời thành viên.");
  expect(screen.queryByText("member@example.com")).toBeNull();
  expect(request.mock.calls.some(([, url]) => url === `/api/local/company/invitations/${invitation.id}/revoke?companyId=${company}`)).toBe(true);
});

it("keeps the lists on screen while a revoke is confirmed", async () => {
  let release!: () => void;
  const held = new Promise<void>(resolve => { release = resolve; });
  fixture(url => url.includes("/revoke?") ? held.then(() => Response.json({ ...invitation, revokedAtUtc: "2026-10-26T01:00:00+00:00", version: "2" })) : undefined);
  await screen.findByText("member@example.com");
  fireEvent.click(screen.getByRole("button", { name: "Thu hồi lời mời member@example.com" }));
  await waitFor(() => expect((screen.getByRole("button", { name: "Thu hồi lời mời member@example.com" }) as HTMLButtonElement).disabled).toBe(true));
  expect(screen.queryByText("Đang tải thành viên…")).toBeNull();
  expect(screen.getByText("member@example.com")).toBeTruthy();
  release(); await screen.findByText("Đã thu hồi lời mời thành viên.");
  expect(screen.queryByText("Đang tải thành viên…")).toBeNull();
});

it.each([["duplicate-invitation", "Email này đã có lời mời đang chờ."], ["existing-member", "Email này đã là thành viên công ty."]])
  ("shows bounded conflict %s", async (code, message) => {
    fixture((url) => url === `/api/local/company/invitations?companyId=${company}` ? Response.json({ code, error: "PRIVATE" }, { status: 409 }) : undefined);
    await screen.findByText("member@example.com");
    fireEvent.change(screen.getByLabelText("Email thành viên"), { target: { value: "new@example.com" } });
    fireEvent.click(screen.getByRole("button", { name: "Thêm thành viên" }));
    expect((await screen.findByRole("alert")).textContent).toBe(message);
    expect(screen.getByRole("alert").textContent).not.toContain("PRIVATE");
  });

it("copies only the application sign-in URL without invitation authority", async () => {
  const writeText = vi.fn(async () => {}); vi.stubGlobal("navigator", { clipboard: { writeText } });
  fixture(); await screen.findByText("member@example.com");
  const link = screen.getByLabelText("Liên kết đăng nhập để gửi cho thành viên") as HTMLInputElement;
  expect(link.value).toBe("http://localhost:3000/");
  expect(link.value).not.toContain(company); expect(link.value).not.toContain("member@example.com");
  fireEvent.click(screen.getByRole("button", { name: "Sao chép" }));
  await waitFor(() => expect(writeText).toHaveBeenCalledWith("http://localhost:3000/"));
});
