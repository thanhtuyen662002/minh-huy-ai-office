import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { expect, it, vi } from "vitest";
import { CompanyMemberPanel } from "./company-member-panel";
const company = "22222222-2222-2222-2222-222222222222", user = "33333333-3333-3333-3333-333333333333", owner = "11111111-1111-1111-1111-111111111111";
type Options = { lostReply?: boolean; lostCoreReply?: boolean; status?: number; code?: string; self?: boolean; active?: boolean; invalidResult?: boolean; noVersion?: boolean };
function fixture(options: Options = {}) {
  const state = { current: true, authorized: true, administrator: false, version: 7, lose: !!(options.lostReply || options.lostCoreReply) };
  const receipts = new Map<string, object>();
  const validate = vi.fn(async () => state.current && state.authorized), unauthorized = vi.fn();
  const request = vi.fn(async (_generation: number, url: string, init?: RequestInit) => {
    if (url.includes("/administrator?")) {
      const input = JSON.parse(init!.body as string);
      if (options.status) return Response.json({ code: options.code ?? "PRIVATE diagnostic" }, { status: options.status });
      if (!receipts.has(input.operationId)) {
        state.administrator = input.isAdministrator; state.version++;
        receipts.set(input.operationId, { companyId: company, userId: user, operationId: input.operationId, membershipVersion: String(state.version), isAdministrator: input.isAdministrator });
      }
      if (state.lose) {
        state.lose = false;
        if (options.lostCoreReply) return Response.json({ error: "The operation result could not be confirmed." }, { status: 503 });
        throw new Error("Lost committed reply");
      }
      return Response.json(options.invalidResult ? { ...receipts.get(input.operationId), companyId: owner } : receipts.get(input.operationId));
    }
    return Response.json({ companyId: company, items: [{ userId: user, displayName: "PRIVATE MEMBER", userActive: true,
      membershipActive: options.active ?? true, roles: state.administrator ? ["admin", "viewer"] : ["viewer"],
      ...(options.noVersion ? {} : { membershipVersion: String(state.version) }) }], offset: 0, limit: 25, hasMore: false });
  });
  render(<CompanyMemberPanel companyId={company} userId={options.self ? user : owner} generation={1} isCurrent={() => state.current} validate={validate} request={request} onUnauthorized={unauthorized} />);
  return { state, validate, request, unauthorized, receipts };
}
const bodies = (request: ReturnType<typeof fixture>["request"]) => request.mock.calls.filter(([, url]) => url.includes("/administrator?")).map(([, , init]) => JSON.parse(init!.body as string));

it("grants/removes through exact versioned operations and reloads authoritative roles", async () => {
  const f = fixture(); await screen.findByText("PRIVATE MEMBER");
  fireEvent.click(screen.getByRole("button", { name: "Cấp quyền quản trị PRIVATE MEMBER" })); await screen.findByText("Đã cấp quyền quản trị công ty.");
  expect(screen.getByText("admin, viewer")).toBeTruthy();
  fireEvent.click(screen.getByRole("button", { name: "Gỡ quyền quản trị PRIVATE MEMBER" })); await screen.findByText("Đã gỡ quyền quản trị công ty.");
  expect(screen.getByText("viewer")).toBeTruthy();
  const operations = bodies(f.request); expect(operations.map(item => [item.expectedVersion, item.isAdministrator])).toEqual([["7", true], ["8", false]]);
  expect(operations[0].operationId).not.toBe(operations[1].operationId);
});
it.each(["browser", "core"])("lost committed %s reply retains the same operation and blocks further edits until replay", async boundary => {
  const f = fixture(boundary === "core" ? { lostCoreReply: true } : { lostReply: true }); await screen.findByText("PRIVATE MEMBER");
  fireEvent.click(screen.getByRole("button", { name: "Cấp quyền quản trị PRIVATE MEMBER" })); await screen.findByRole("alert");
  expect(screen.queryByText("PRIVATE MEMBER")).toBeNull(); expect((screen.getByRole("button", { name: "Tải lại thành viên" }) as HTMLButtonElement).disabled).toBe(true);
  fireEvent.click(screen.getByRole("button", { name: "Thử lại thao tác với PRIVATE MEMBER" })); await screen.findByText("Đã cấp quyền quản trị công ty.");
  expect(bodies(f.request)).toHaveLength(2); expect(bodies(f.request)[0]).toEqual(bodies(f.request)[1]); expect(f.receipts.size).toBe(1); expect(f.state.version).toBe(8);
  expect(f.unauthorized).not.toHaveBeenCalled();
});
it("historical lost-reply receipt reloads current roles and uses the later version for the next change", async () => {
  const f = fixture({ lostReply: true }); await screen.findByText("PRIVATE MEMBER");
  fireEvent.click(screen.getByRole("button", { name: "Cấp quyền quản trị PRIVATE MEMBER" })); await screen.findByRole("alert");
  const original = bodies(f.request)[0];
  // Another authorized actor removes the committed role before its lost reply
  // is retried. The immutable original receipt must not become current state.
  f.state.administrator = false; f.state.version++;
  fireEvent.click(screen.getByRole("button", { name: "Thử lại thao tác với PRIVATE MEMBER" }));
  await screen.findByText("Đã cấp quyền quản trị công ty.");
  expect(bodies(f.request)[1]).toEqual(original); expect(f.receipts.size).toBe(1);
  expect(f.state.version).toBe(9); expect(screen.getByText("viewer")).toBeTruthy();
  expect(screen.queryByRole("button", { name: "Gỡ quyền quản trị PRIVATE MEMBER" })).toBeNull();
  fireEvent.click(screen.getByRole("button", { name: "Cấp quyền quản trị PRIVATE MEMBER" }));
  await screen.findByText("admin, viewer");
  const next = bodies(f.request)[2]; expect(next.expectedVersion).toBe("9"); expect(next.isAdministrator).toBe(true);
  expect(next.operationId).not.toBe(original.operationId); expect(f.receipts.size).toBe(2);
});
it.each(["stale-version", "last-administrator", "self-role-change", "constructor", "PRIVATE_CODE"])("conflict %s clears rows/pending and requires reload", async code => {
  fixture({ status: 409, code }); await screen.findByText("PRIVATE MEMBER");
  fireEvent.click(screen.getByRole("button", { name: "Cấp quyền quản trị PRIVATE MEMBER" })); await screen.findByRole("alert");
  expect(screen.queryByText("PRIVATE MEMBER")).toBeNull(); expect(screen.queryByRole("button", { name: "Thử lại thao tác với PRIVATE MEMBER" })).toBeNull();
  expect(screen.getByRole("alert").textContent).not.toContain("PRIVATE_CODE");
  fireEvent.click(screen.getByRole("button", { name: "Tải lại thành viên" })); await screen.findByText("PRIVATE MEMBER");
});
it.each([400, 401, 403, 404])("definitive denial %i clears private role controls", async status => {
  const f = fixture({ status }); await screen.findByText("PRIVATE MEMBER");
  fireEvent.click(screen.getByRole("button", { name: "Cấp quyền quản trị PRIVATE MEMBER" })); await waitFor(() => expect(screen.queryByText("PRIVATE MEMBER")).toBeNull());
  expect(screen.queryByRole("button", { name: "Thử lại thao tác với PRIVATE MEMBER" })).toBeNull(); expect(f.unauthorized).toHaveBeenCalledTimes(status === 401 ? 1 : 0);
});
it.each([{ self: true }, { active: false }, { noVersion: true }])("self/inactive/versionless target %j has no role mutation", async options => {
  const f = fixture(options); await screen.findByText("PRIVATE MEMBER");
  const button = screen.getByRole("button", { name: "Cấp quyền quản trị PRIVATE MEMBER" }) as HTMLButtonElement;
  expect(button.disabled).toBe(true); fireEvent.click(button); expect(bodies(f.request)).toHaveLength(0);
});
it("fresh authority loss before the click removes rows and never proxies", async () => {
  const f = fixture(); await screen.findByText("PRIVATE MEMBER"); f.state.authorized = false;
  fireEvent.click(screen.getByRole("button", { name: "Cấp quyền quản trị PRIVATE MEMBER" })); await waitFor(() => expect(screen.queryByText("PRIVATE MEMBER")).toBeNull());
  expect(bodies(f.request)).toHaveLength(0); expect(screen.queryByRole("button", { name: "Thử lại thao tác với PRIVATE MEMBER" })).toBeNull();
});
it("foreign success keeps exact pending request for a safe retry", async () => {
  const f = fixture({ invalidResult: true }); await screen.findByText("PRIVATE MEMBER");
  fireEvent.click(screen.getByRole("button", { name: "Cấp quyền quản trị PRIVATE MEMBER" })); await screen.findByRole("alert");
  expect(screen.queryByText("Đã cấp quyền quản trị công ty.")).toBeNull();
  fireEvent.click(screen.getByRole("button", { name: "Thử lại thao tác với PRIVATE MEMBER" })); await waitFor(() => expect(bodies(f.request)).toHaveLength(2));
  expect(bodies(f.request)[0]).toEqual(bodies(f.request)[1]);
});
it("unmount cannot publish a late role receipt or reload a prior scope", async () => {
  let resolve!: (response: Response) => void; const pending = new Promise<Response>(done => { resolve = done; });
  const request = vi.fn(async (_generation: number, url: string) => url.includes("/administrator?") ? pending : Response.json({ companyId: company, items: [{ userId: user, displayName: "PRIVATE MEMBER", userActive: true, membershipActive: true, roles: ["viewer"], membershipVersion: "7" }], offset: 0, limit: 25, hasMore: false }));
  const view = render(<CompanyMemberPanel companyId={company} userId={owner} generation={1} isCurrent={() => true} validate={async () => true} request={request} onUnauthorized={() => {}} />);
  await screen.findByText("PRIVATE MEMBER"); fireEvent.click(screen.getByRole("button", { name: "Cấp quyền quản trị PRIVATE MEMBER" })); await waitFor(() => expect(request).toHaveBeenCalledTimes(2));
  view.unmount(); await act(async () => resolve(Response.json({ companyId: company, userId: user, operationId: owner, membershipVersion: "8", isAdministrator: true })));
  expect(request).toHaveBeenCalledTimes(2); expect(screen.queryByText("PRIVATE MEMBER")).toBeNull();
});
