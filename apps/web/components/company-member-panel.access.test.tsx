import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { CompanyMemberPanel } from "./company-member-panel";
import { CompanyMember } from "../lib/company-members";
const company = "22222222-2222-2222-2222-222222222222", user = "33333333-3333-3333-3333-333333333333", owner = "11111111-1111-1111-1111-111111111111";
const member = { userId: user, displayName: "PRIVATE MEMBER", userActive: true, membershipActive: true, roles: ["viewer"], membershipVersion: "7" };
function fixture(override: (url: string, init?: RequestInit) => Response | Promise<Response> | undefined = () => undefined, row: CompanyMember = member) {
  const state = { active: row.membershipActive, version: row.membershipVersion, current: true };
  const validate = vi.fn(async () => state.current), unauthorized = vi.fn();
  const request = vi.fn(async (_generation: number, url: string, init?: RequestInit) => {
    const custom = override(url, init); if (custom !== undefined) return custom;
    if (url.includes("/access?")) {
      const input = JSON.parse(init!.body as string); state.active = input.isActive; state.version = String(Number(state.version) + 1);
      return Response.json({ companyId: company, userId: user, membershipActive: input.isActive, membershipVersion: state.version, operationId: input.operationId });
    }
    return Response.json({ companyId: company, items: [{ ...row, membershipActive: state.active, membershipVersion: state.version }], offset: 0, limit: 25, hasMore: false });
  });
  render(<CompanyMemberPanel companyId={company} userId={owner} generation={1} isCurrent={() => state.current} validate={validate} request={request} onUnauthorized={unauthorized} />);
  return { state, validate, request, unauthorized };
}
const mutationBodies = (request: ReturnType<typeof fixture>["request"]) => request.mock.calls.filter(([, url]) => url.includes("/access?")).map(([, , init]) => JSON.parse(init!.body as string));
afterEach(() => vi.unstubAllGlobals());
it("suspends and reactivates through versioned operations then reloads the authoritative status", async () => {
  const { request } = fixture(); await screen.findByText(member.displayName);
  fireEvent.click(screen.getByRole("button", { name: "Khóa quyền PRIVATE MEMBER" }));
  await screen.findByText("Đã khóa quyền truy cập thành viên."); expect(screen.getByText("Đã tắt thành viên")).toBeTruthy();
  fireEvent.click(screen.getByRole("button", { name: "Mở lại quyền PRIVATE MEMBER" })); await screen.findByText("Đã mở lại quyền truy cập thành viên.");
  const bodies = mutationBodies(request); expect(bodies.map(body => [body.expectedVersion, body.isActive])).toEqual([["7", false], ["8", true]]); expect(bodies[0].operationId).not.toBe(bodies[1].operationId);
  expect(request.mock.calls.filter(([, url]) => !url.includes("/access?")).every(([, url]) => url.includes("includeAccessVersion=true"))).toBe(true);
});
it("retains exactly the same operation after a lost reply and blocks other actions until resolved", async () => {
  let first = true; const { request } = fixture((url) => { if (url.includes("/access?") && first) { first = false; throw new Error("Lost reply"); } return undefined; });
  await screen.findByText(member.displayName); fireEvent.click(screen.getByRole("button", { name: "Khóa quyền PRIVATE MEMBER" }));
  await screen.findByRole("alert"); expect(screen.queryByText(member.displayName)).toBeNull(); expect((screen.getByRole("button", { name: "Tải lại thành viên" }) as HTMLButtonElement).disabled).toBe(true);
  fireEvent.click(screen.getByRole("button", { name: "Thử lại thao tác với PRIVATE MEMBER" })); await screen.findByText("Đã khóa quyền truy cập thành viên.");
  const bodies = mutationBodies(request); expect(bodies).toHaveLength(2); expect(bodies[0]).toEqual(bodies[1]); expect(screen.queryByRole("button", { name: "Thử lại thao tác với PRIVATE MEMBER" })).toBeNull();
});
it.each(["stale-version", "operation-conflict", "last-administrator", "self-deactivation", "inactive-user"])("clears stale private rows and permits reloading after server conflict %s", async code => {
  fixture(url => url.includes("/access?") ? Response.json({ code }, { status: 409 }) : undefined);
  await screen.findByText(member.displayName); fireEvent.click(screen.getByRole("button", { name: "Khóa quyền PRIVATE MEMBER" }));
  await screen.findByRole("alert"); expect(screen.queryByText(member.displayName)).toBeNull(); expect(screen.queryByRole("button", { name: "Thử lại thao tác với PRIVATE MEMBER" })).toBeNull();
  fireEvent.click(screen.getByRole("button", { name: "Tải lại thành viên" })); await screen.findByText(member.displayName);
});
it.each([400, 403, 404, 401])("clears private rows and pending operation on definitive denial %i", async status => {
  const { unauthorized } = fixture(url => url.includes("/access?") ? Response.json({}, { status }) : undefined);
  await screen.findByText(member.displayName); fireEvent.click(screen.getByRole("button", { name: "Khóa quyền PRIVATE MEMBER" }));
  await waitFor(() => expect(screen.queryByText(member.displayName)).toBeNull());
  expect(screen.queryByRole("button", { name: "Thử lại thao tác với PRIVATE MEMBER" })).toBeNull(); expect(unauthorized).toHaveBeenCalledTimes(status === 401 ? 1 : 0);
});
it("ignores a successful response from a prior company/session generation", async () => {
  let resolve!: (value: Response) => void; const pending = new Promise<Response>(done => { resolve = done; });
  const { state, request } = fixture(url => url.includes("/access?") ? pending : undefined);
  await screen.findByText(member.displayName); fireEvent.click(screen.getByRole("button", { name: "Khóa quyền PRIVATE MEMBER" })); await waitFor(() => expect(mutationBodies(request)).toHaveLength(1));
  const [input] = mutationBodies(request); state.current = false;
  await act(async () => resolve(Response.json({ companyId: company, userId: user, membershipActive: false, membershipVersion: "8", operationId: input.operationId })));
  expect(screen.queryByText("Đã khóa quyền truy cập thành viên.")).toBeNull(); expect(request.mock.calls.filter(([, url]) => !url.includes("/access?"))).toHaveLength(1);
});
it("treats a foreign or malformed success as an unknown result and retries the original operation", async () => {
  fixture(url => url.includes("/access?") ? Response.json({ companyId: "66666666-6666-6666-6666-666666666666", userId: user, membershipActive: false, membershipVersion: "8", operationId: "44444444-4444-4444-4444-444444444444" }) : undefined);
  await screen.findByText(member.displayName); fireEvent.click(screen.getByRole("button", { name: "Khóa quyền PRIVATE MEMBER" })); await screen.findByRole("alert");
  expect(screen.queryByText(member.displayName)).toBeNull(); expect(screen.getByRole("button", { name: "Thử lại thao tác với PRIVATE MEMBER" })).toBeTruthy();
});
it("discards retry authority when revalidation fails after a transport error", async () => {
  const { validate } = fixture(url => url.includes("/access?") ? Promise.reject(new Error("Lost reply")) : undefined);
  await screen.findByText(member.displayName); validate.mockResolvedValueOnce(true).mockResolvedValueOnce(false);
  fireEvent.click(screen.getByRole("button", { name: "Khóa quyền PRIVATE MEMBER" })); await waitFor(() => expect(screen.queryByText(member.displayName)).toBeNull());
  expect(screen.queryByRole("button", { name: "Thử lại thao tác với PRIVATE MEMBER" })).toBeNull();
});
it.each([{ ...member, userId: owner }, { ...member, userActive: false }, { ...member, membershipVersion: undefined }])("disables unsupported self/inactive/legacy actions %j", async row => {
  const { request } = fixture(undefined, row); await screen.findByText(member.displayName); const button = screen.getByRole("button", { name: "Khóa quyền PRIVATE MEMBER" });
  expect((button as HTMLButtonElement).disabled).toBe(true); fireEvent.click(button); expect(mutationBodies(request)).toHaveLength(0);
});
