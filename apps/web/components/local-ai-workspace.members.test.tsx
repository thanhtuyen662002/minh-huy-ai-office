import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { LocalAiWorkspace } from "./local-ai-workspace";
const company = "22222222-2222-2222-2222-222222222222";
const other = "66666666-6666-6666-6666-666666666666";
const member = { userId: "33333333-3333-3333-3333-333333333333", displayName: "PRIVATE MEMBER A", userActive: false, membershipActive: false, roles: ["viewer"] };
function backend(override: (url: string) => Response | Promise<Response> | undefined = () => undefined) {
  const state = { roles: ["admin"] };
  const fetcher = vi.fn(async (url: string) => {
    const custom = override(url); if (custom !== undefined) return custom;
    const selected = new URL(url, "http://fixture.invalid").searchParams.get("companyId");
    if (url.startsWith("/api/local/session?")) return Response.json({ tenantId: "tenant", companyId: selected, userId: "owner", roles: state.roles });
    if (url.startsWith("/api/local/data-sources?")) return Response.json([]);
    if (url.startsWith("/api/local/company/members?")) return Response.json({ companyId: selected, items: selected === other ? [] : [member], offset: 0, limit: 25, hasMore: false });
    if (url === "/api/local/session/logout") return Response.json({ ok: true });
    throw new Error("Unexpected fixture route " + url);
  });
  vi.stubGlobal("fetch", fetcher); return { state, fetcher };
}
async function open() {
  const view = render(<LocalAiWorkspace companyId={company} companyName="Fixture" />);
  await waitFor(() => expect(view.container.querySelector("nav")).toBeTruthy());
  fireEvent.click(screen.getAllByRole("button", { name: "Thành viên" })[0]); return view;
}
beforeEach(() => vi.stubGlobal("BroadcastChannel", undefined));
afterEach(() => vi.unstubAllGlobals());
it("renders scoped statuses and roles after fresh authority confirmation", async () => {
  backend(); await open(); await screen.findByText(member.displayName);
  expect(screen.getByText("Đã khóa tài khoản")).toBeTruthy(); expect(screen.getByText("Đã tắt thành viên")).toBeTruthy(); expect(screen.getByText("viewer")).toBeTruthy();
});
it("keeps member navigation absent for viewers", async () => {
  const { state } = backend(); state.roles = ["viewer"];
  const view = render(<LocalAiWorkspace companyId={company} companyName="Fixture" />); await waitFor(() => expect(view.container.querySelector("nav")).toBeTruthy());
  expect(screen.queryByRole("button", { name: "Thành viên" })).toBeNull();
});
it("clears cached members while a retry fails", async () => {
  let fail = false; backend(url => fail && url.startsWith("/api/local/company/members?") ? Response.json({}, { status: 503 }) : undefined);
  await open(); await screen.findByText(member.displayName); fail = true; fireEvent.click(screen.getByRole("button", { name: "Tải lại thành viên" }));
  await screen.findByRole("alert"); expect(screen.queryByText(member.displayName)).toBeNull();
});
it("reconciles roles when the member request returns403", async () => {
  let deny = false; const { state } = backend(url => {
    if (deny && url.startsWith("/api/local/company/members?")) { state.roles = ["viewer"]; return Response.json({}, { status: 403 }); }
    return undefined;
  });
  await open(); await screen.findByText(member.displayName); deny = true; fireEvent.click(screen.getByRole("button", { name: "Tải lại thành viên" }));
  await waitFor(() => expect(screen.queryByRole("region", { name: "Thành viên công ty" })).toBeNull());
  expect(screen.queryByText(member.displayName)).toBeNull(); expect(screen.queryByRole("button", { name: "Thành viên" })).toBeNull();
});
it("drops members and signs out immediately on401", async () => {
  let expire = false; backend(url => expire && url.startsWith("/api/local/company/members?") ? Response.json({}, { status: 401 }) : undefined);
  await open(); await screen.findByText(member.displayName); expire = true; fireEvent.click(screen.getByRole("button", { name: "Tải lại thành viên" }));
  await screen.findByRole("button", { name: "Vào AI Office" }); expect(screen.queryByText(member.displayName)).toBeNull();
});
it("ignores member responses after company changes", async () => {
  let resolve!: (value: Response) => void; const pending = new Promise<Response>(done => { resolve = done; });
  backend(url => url.startsWith("/api/local/company/members?") ? pending : undefined);
  const view = await open(); await screen.findByText("Đang tải thành viên…"); view.rerender(<LocalAiWorkspace companyId={other} companyName="B" />);
  await act(async () => resolve(Response.json({ companyId: company, items: [member], offset: 0, limit: 25, hasMore: false })));
  expect(screen.queryByText(member.displayName)).toBeNull();
});
it("rejects a foreign company response without rendering members", async () => {
  backend(url => url.startsWith("/api/local/company/members?") ? Response.json({ companyId: other, items: [member], offset: 0, limit: 25, hasMore: false }) : undefined);
  await open(); await screen.findByRole("alert"); expect(screen.queryByText(member.displayName)).toBeNull();
});
it("pages with bounded offsets and clears the previous page", async () => {
  backend(url => {
    if (!url.startsWith("/api/local/company/members?")) return undefined;
    const offset = Number(new URL(url, "http://fixture.invalid").searchParams.get("offset"));
    const items = offset === 0 ? Array.from({ length: 25 }, (_, i) => ({ ...member,
      userId: `00000000-0000-0000-0000-${String(i + 1).padStart(12, "0")}`, displayName: "FIRST PAGE " + i })) : [member];
    return Response.json({ companyId: company, items, offset, limit: 25, hasMore: offset === 0 });
  });
  await open(); await screen.findByText("FIRST PAGE 0"); fireEvent.click(screen.getByRole("button", { name: "Trang sau" }));
  await screen.findByText(member.displayName); expect(screen.queryByText("FIRST PAGE 0")).toBeNull(); expect(screen.getByText("Trang 2")).toBeTruthy();
  fireEvent.click(screen.getByRole("button", { name: "Trang trước" })); await screen.findByText("FIRST PAGE 0"); expect(screen.queryByText(member.displayName)).toBeNull();
});
