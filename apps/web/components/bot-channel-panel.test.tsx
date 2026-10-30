import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { expect, it, vi } from "vitest";
import { BotChannelPanel, useBotChannelsAvailable, type BotSessionProps } from "./bot-channel-panel";
import { botCompany, botUser, botView, botPng } from "../lib/bot-channel-test-fixture";

type Override = (url: string, init?: RequestInit) => Response | Promise<Response> | undefined;
function fixture(override: Override = () => undefined) {
  const active = { current: true }, onUnauthorized = vi.fn();
  let view = structuredClone(botView);
  const request = vi.fn(async (_generation: number, url: string, init?: RequestInit) => {
    const custom = override(url, init); if (custom) return custom;
    if (url.includes("/qr?")) return Response.json({ contentType: "image/png", base64: botPng, expiresAt: new Date(Date.now() + 60000).toISOString() });
    if (url.includes("/commands?")) return Response.json({ id: crypto.randomUUID(), kind: JSON.parse(String(init?.body)).kind, createdAt: new Date().toISOString(), expiresAt: new Date(Date.now() + 600000).toISOString() });
    if (url.includes("/ai/test?")) return Response.json({ success: true, code: "connected" });
    if (url.includes("/audit?")) return Response.json([{ id: crypto.randomUUID(), channel: null, actorUserId: botUser, field: "ai.apiKey", before: "[redacted]", after: "[redacted]", timestamp: new Date().toISOString() }]);
    if (init?.method === "PUT") {
      const body = JSON.parse(String(init.body));
      if (url.includes("/ai?")) { view = { ...view, ai: { baseUrl: body.baseUrl, model: body.model, apiKeyConfigured: !body.clearApiKey, version: "2" } }; return Response.json(view.ai); }
      const channel = url.includes("/zalo?") ? "zalo" : "whatsapp";
      view = { ...view, channels: view.channels.map(c => c.channel === channel ? { ...c, ...body, configVersion: "2" } : c) }; return Response.json(view.channels.find(c => c.channel === channel));
    }
    return Response.json(view);
  });
  const props: BotSessionProps = { companyId: botCompany, userId: botUser, generation: 1, isCurrent: () => active.current,
    validate: vi.fn(async () => active.current), request, onUnauthorized };
  return { ...render(<BotChannelPanel {...props} />), props, request, active, onUnauthorized };
}
it("shows both channel cards, Vietnamese statuses, schedules and group choices", async () => {
  const f = fixture(); const zalo = await screen.findByRole("article", { name: "Kênh Zalo" });
  expect(await screen.findByRole("article", { name: "Kênh WhatsApp" })).toBeTruthy();
  expect(within(zalo).getByText("Đã đăng nhập")).toBeTruthy(); expect(screen.getByText("Đã cấu hình khóa API. Để trống để giữ khóa hiện tại.")).toBeTruthy();
  expect((screen.getByLabelText("Khóa API mới") as HTMLInputElement).value).toBe(""); expect((screen.getByLabelText("Khóa API mới") as HTMLInputElement).type).toBe("password");
  expect(within(zalo).getByRole("option", { name: "Kỹ thuật" })).toBeTruthy();
  expect(f.request.mock.calls.every(([, url]) => url.includes(`companyId=${botCompany}`))).toBe(true);
});
it("normalizes schedule and saves selected reported groups with customer names", async () => {
  const f = fixture(); const zalo = await screen.findByRole("article", { name: "Kênh Zalo" });
  fireEvent.click(within(zalo).getByLabelText("Bật kênh Zalo")); fireEvent.change(within(zalo).getByLabelText("Lịch tóm tắt Zalo (HH:mm)"), { target: { value: "17:30, 08:00, 08:00" } });
  fireEvent.click(within(zalo).getByLabelText("Khách B")); fireEvent.change(within(zalo).getByLabelText("Tên khách hàng cho Khách B"), { target: { value: "Công ty B" } });
  fireEvent.click(within(zalo).getByRole("button", { name: "Lưu kênh Zalo" })); await screen.findByText("Đã lưu cấu hình kênh.");
  const [, , init] = f.request.mock.calls.find(([, url, init]) => url.includes("/zalo?") && init?.method === "PUT")!;
  const body = JSON.parse(String(init?.body)); expect(body.summarySchedule).toEqual(["08:00", "17:30"]); expect(body.expectedVersion).toBe("1"); expect(body.enabled).toBe(true);
  expect(body.customerGroups).toContainEqual({ externalId: "second", displayName: "Khách B", customerName: "Công ty B" });
});
it("rejects invalid schedule before calling the API", async () => {
  const f = fixture(); await screen.findByRole("article", { name: "Kênh Zalo" });
  fireEvent.change(screen.getByLabelText("Lịch tóm tắt Zalo (HH:mm)"), { target: { value: "25:00" } }); fireEvent.click(screen.getByRole("button", { name: "Lưu kênh Zalo" }));
  expect(await screen.findByRole("alert")).toBeTruthy(); expect(f.request.mock.calls.some(([, , init]) => init?.method)).toBe(false);
});
it("never selects the technical destination as a customer source", async () => {
  fixture(); const zalo = await screen.findByRole("article", { name: "Kênh Zalo" });
  fireEvent.change(within(zalo).getByLabelText("Nhóm kỹ thuật Zalo"), { target: { value: "customer" } });
  expect(within(zalo).queryByRole("checkbox", { name: "Khách A" })).toBeNull(); expect(within(zalo).queryByLabelText("Tên khách hàng cho Khách A")).toBeNull();
});
it("key writes clear the password field immediately and blank saves preserve existing key", async () => {
  const f = fixture(); await screen.findByLabelText("Khóa API mới");
  fireEvent.change(screen.getByLabelText("Khóa API mới"), { target: { value: "PRIVATE-NEW-KEY" } }); fireEvent.click(screen.getByRole("button", { name: "Lưu thiết lập AI" }));
  expect((screen.getByLabelText("Khóa API mới") as HTMLInputElement).value).toBe(""); await screen.findByText("Đã lưu thiết lập AI.");
  fireEvent.click(screen.getByRole("button", { name: "Lưu thiết lập AI" })); await waitFor(() => expect(f.request.mock.calls.filter(([, , init]) => init?.method === "PUT")).toHaveLength(2));
  const calls = f.request.mock.calls.filter(([, , init]) => init?.method === "PUT"); expect(JSON.parse(String(calls[0][2]?.body)).apiKey).toBe("PRIVATE-NEW-KEY");
  expect(JSON.parse(String(calls[1][2]?.body))).not.toHaveProperty("apiKey"); expect(document.body.textContent).not.toContain("PRIVATE-NEW-KEY");
});
it("supports explicit key clearing, connection testing and redacted audit listing", async () => {
  const f = fixture(); await screen.findByLabelText("Khóa API mới");
  fireEvent.click(screen.getByLabelText("Xóa khóa API đã lưu")); fireEvent.click(screen.getByRole("button", { name: "Lưu thiết lập AI" }));
  await screen.findByText("Đã lưu thiết lập AI."); expect(screen.getByText("Chưa cấu hình khóa API. Để trống để giữ khóa hiện tại.")).toBeTruthy();
  expect(JSON.parse(String(f.request.mock.calls.find(([, , init]) => init?.method === "PUT")![2]?.body)).clearApiKey).toBe(true);
  fireEvent.click(screen.getByRole("button", { name: "Kiểm tra kết nối" })); await screen.findByText("Kết nối AI thành công.");
  fireEvent.click(screen.getByRole("button", { name: "Xem lịch sử thay đổi" })); await screen.findByText(/ai.apiKey/);
});
it("queues commands and shows only fresh QR after relogin", async () => {
  const f = fixture(); await screen.findByRole("article", { name: "Kênh Zalo" });
  fireEvent.click(screen.getByRole("button", { name: "Tóm tắt ngay Zalo" })); await screen.findByText("Đã xếp lệnh tóm tắt; chờ bot nhận lệnh.");
  fireEvent.click(screen.getByRole("button", { name: "Đăng nhập lại Zalo" })); const image = await screen.findByRole("img", { name: "Mã QR đăng nhập Zalo" });
  expect(image.getAttribute("src")).toBe(`data:image/png;base64,${botPng}`);
  expect(f.request.mock.calls.filter(([, url]) => url.includes("/commands?")).map(([, , init]) => JSON.parse(String(init?.body)).kind)).toEqual(["summarize-now", "relogin"]);
});
it("explains waiting for the bot when QR is absent", async () => {
  fixture(url => url.includes("/qr?") ? new Response(null, { status: 404 }) : undefined); await screen.findByRole("article", { name: "Kênh Zalo" });
  fireEvent.click(screen.getByRole("button", { name: "Đăng nhập lại Zalo" })); await screen.findByText("Chờ bot gửi mã QR mới, sau đó chọn Xem mã QR."); expect(screen.queryByRole("img")).toBeNull();
});
it("removes a displayed QR when its short lifetime ends", async () => {
  fixture(); await screen.findByRole("article", { name: "Kênh Zalo" }); vi.useFakeTimers();
  try {
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Xem mã QR Zalo" })); });
    expect(screen.getByRole("img", { name: "Mã QR đăng nhập Zalo" })).toBeTruthy();
    await act(async () => { vi.advanceTimersByTime(60001); }); expect(screen.queryByRole("img")).toBeNull();
  } finally { vi.useRealTimers(); }
});
it("clears private UI when admin access is revoked", async () => {
  let revoked = false; fixture(() => revoked ? new Response(null, { status: 403 }) : undefined);
  await screen.findByRole("article", { name: "Kênh Zalo" }); revoked = true; fireEvent.click(screen.getByRole("button", { name: "Tải lại cấu hình" }));
  await screen.findByText("Bạn không còn quyền quản trị công ty này."); expect(screen.queryByRole("article")).toBeNull(); expect(screen.queryByLabelText("Khóa API mới")).toBeNull();
});
it("never paints a late response after the session generation changes", async () => {
  let release!: (r: Response) => void; const pending = new Promise<Response>(resolve => { release = resolve; });
  const f = fixture(() => pending); await waitFor(() => expect(f.request).toHaveBeenCalled()); f.active.current = false;
  await act(async () => { release(Response.json(botView)); }); expect(screen.queryByRole("article")).toBeNull();
});
function Navigation({ props, admin }: { props: BotSessionProps; admin: boolean }) {
  return useBotChannelsAvailable(props, admin) ? <button>Kênh &amp; Bot</button> : null;
}
it("navigation remains hidden for non-admins and feature-off APIs", async () => {
  const request = vi.fn(async () => new Response(null, { status: 404 }));
  const props: BotSessionProps = { companyId: botCompany, userId: botUser, generation: 1, isCurrent: () => true, validate: async () => true, request, onUnauthorized: vi.fn() };
  const v = render(<Navigation props={props} admin={false} />); expect(request).not.toHaveBeenCalled(); expect(screen.queryByRole("button")).toBeNull();
  v.rerender(<Navigation props={props} admin />); await waitFor(() => expect(request).toHaveBeenCalled()); expect(screen.queryByRole("button")).toBeNull();
});
it("navigation appears only after an enabled admin response", async () => {
  const props: BotSessionProps = { companyId: botCompany, userId: botUser, generation: 1, isCurrent: () => true, validate: async () => true,
    request: vi.fn(async () => Response.json(botView)), onUnauthorized: vi.fn() };
  render(<Navigation props={props} admin />); expect(await screen.findByRole("button", { name: "Kênh & Bot" })).toBeTruthy();
});
it("shows each result beside the control that was used, red when it failed", async () => {
  fixture(url => url.includes("/ai/test?") ? Response.json({ success: false, code: "host_not_allowed" }) : undefined);
  const zalo = await screen.findByRole("article", { name: "Kênh Zalo" });
  fireEvent.click(screen.getByRole("button", { name: "Kiểm tra kết nối" }));
  const alert = await screen.findByRole("alert");
  expect(alert.textContent).toContain("chưa được hệ thống cho phép");
  expect(screen.getByRole("form", { name: "Thiết lập AI" }).contains(alert)).toBe(true);
  fireEvent.click(within(zalo).getByRole("button", { name: "Tóm tắt ngay Zalo" }));
  const done = await screen.findByText("Đã xếp lệnh tóm tắt; chờ bot nhận lệnh.");
  expect(zalo.parentElement!.contains(done)).toBe(true);
  expect(screen.queryByRole("alert")).toBeNull();
});
