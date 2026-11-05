import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { LocalAiWorkspace } from "./local-ai-workspace";
const tenant = "11111111-1111-4111-8111-111111111111", company = "22222222-2222-4222-8222-222222222222", user = "33333333-3333-4333-8333-333333333333";
const group = { channel: "zalo", externalId: "g-1", displayName: "PRIVATE_GROUP", customerName: "Novopharm", lastMessageAt: "2026-10-30T02:00:00Z", messageCount: 3 };
const message = (id: number, text = `PRIVATE_TEXT_${id}`) => ({ messageId: `m${id}`, senderName: "Anh Minh", text, hasImage: false, sentAt: `2026-10-30T0${id}:00:00Z` });
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

it("members read a selected group's messages oldest first and load older pages", async () => {
  const fetcher = vi.fn(async (url: string, init?: RequestInit) => { void init;
    if (url.startsWith("/api/local/session?")) return Response.json({ tenantId: tenant, companyId: company, userId: user, roles: ["viewer"] });
    if (url.startsWith("/api/local/data-sources?")) return Response.json([]);
    if (url.startsWith("/api/local/bot-messages?")) return Response.json([group]);
    if (url.startsWith("/api/local/bot-messages/zalo?") && url.includes("before=")) return Response.json({ items: [message(1)], hasMore: false });
    if (url.startsWith("/api/local/bot-messages/zalo?")) return Response.json({ items: [message(3), { ...message(2, ""), hasImage: true }], hasMore: true });
    throw new Error("Unexpected owned fixture route");
  });
  vi.stubGlobal("fetch", fetcher); vi.stubGlobal("BroadcastChannel", undefined);
  render(<LocalAiWorkspace companyId={company} companyName="Fixture" />);
  fireEvent.click(await screen.findByRole("button", { name: "Tin nhắn" }));
  fireEvent.click(await screen.findByRole("button", { name: "PRIVATE_GROUP" }));
  await screen.findByText("PRIVATE_TEXT_3"); expect(screen.getByText("Có hình ảnh")).toBeTruthy();
  expect(fetcher.mock.calls.some(([url]) => String(url).includes("group=g-1&limit=50"))).toBe(true);
  fireEvent.click(screen.getByRole("button", { name: "Xem tin cũ hơn" }));
  await screen.findByText("PRIVATE_TEXT_1");
  const texts = [...screen.getByRole("list", { name: "Tin nhắn trong nhóm" }).querySelectorAll("li")].map(li => li.textContent);
  expect(texts[0]).toContain("PRIVATE_TEXT_1"); expect(texts[2]).toContain("PRIVATE_TEXT_3");
  expect(fetcher.mock.calls.find(([url]) => String(url).includes("before="))![0]).toContain(`before=${encodeURIComponent("2026-10-30T02:00:00Z")}`);
  expect(screen.queryByRole("button", { name: "Xem tin cũ hơn" })).toBeNull();
  expect(fetcher.mock.calls.every(([, init]) => !init?.method || init.method === "GET")).toBe(true);
});

it("an unselected group shows an alert and logout discards late messages", async () => {
  let active = true, finish!: (response: Response) => void, held: Promise<Response> | null = null, signal: AbortSignal | null | undefined;
  const fetcher = vi.fn(async (url: string, init?: RequestInit) => {
    if (url === "/api/local/session/logout") { active = false; return Response.json({ ok: true }); }
    if (url.startsWith("/api/local/session?")) return active ? Response.json({ tenantId: tenant, companyId: company, userId: user, roles: ["viewer"] }) : Response.json({}, { status: 401 });
    if (url.startsWith("/api/local/data-sources?")) return Response.json([]);
    if (url.startsWith("/api/local/bot-messages?")) return Response.json([group]);
    if (url.startsWith("/api/local/bot-messages/zalo?")) { signal = init?.signal; return held ?? Response.json({}, { status: 404 }); }
    throw new Error("Unexpected owned fixture route");
  });
  vi.stubGlobal("fetch", fetcher); vi.stubGlobal("BroadcastChannel", undefined);
  render(<LocalAiWorkspace companyId={company} companyName="Fixture" />);
  fireEvent.click(await screen.findByRole("button", { name: "Tin nhắn" }));
  fireEvent.click(await screen.findByRole("button", { name: "PRIVATE_GROUP" }));
  expect((await screen.findByRole("alert")).textContent).toContain("không còn được chọn");
  held = new Promise<Response>(resolve => { finish = resolve; }); const previous = signal;
  fireEvent.click(screen.getByRole("button", { name: "Tải lại tin nhắn" })); await waitFor(() => expect(signal).not.toBe(previous));
  const pending = signal!; fireEvent.click(screen.getByRole("button", { name: "Đăng xuất" })); await screen.findByText("Local Pilot");
  expect(pending.aborted).toBe(true); await act(async () => finish(Response.json({ items: [message(1)], hasMore: false })));
  expect(screen.queryByText("PRIVATE_TEXT_1")).toBeNull(); expect(screen.queryByRole("region", { name: "Tin nhắn nhóm khách" })).toBeNull();
});

it("leaving Tin nhắn removes its panels so other sections are not pushed down", async () => {
  const fetcher = vi.fn(async (url: string) => {
    if (url.startsWith("/api/local/session?")) return Response.json({ tenantId: tenant, companyId: company, userId: user, roles: ["viewer"] });
    if (url.startsWith("/api/local/data-sources?")) return Response.json([]);
    if (url.startsWith("/api/local/bot-messages?")) return Response.json([group]);
    if (url.startsWith("/api/local/group-sources?")) return Response.json({}, { status: 404 });
    return Response.json({}, { status: 404 });
  });
  vi.stubGlobal("fetch", fetcher); vi.stubGlobal("BroadcastChannel", undefined);
  // Two siblings sharing a key let production React leave a stale Tin nhắn panel mounted.
  const errors = vi.spyOn(console, "error").mockImplementation(() => {});
  render(<LocalAiWorkspace companyId={company} companyName="Fixture" />);
  for (const next of ["Trợ lý", "Kiến thức", "Tổng quan"]) {
    fireEvent.click(await screen.findByRole("button", { name: "Tin nhắn" }));
    await screen.findByRole("button", { name: "PRIVATE_GROUP" });
    expect(screen.getAllByRole("region", { name: "Tin nhắn nhóm khách" })).toHaveLength(1);
    fireEvent.click(screen.getByRole("button", { name: next }));
    await waitFor(() => expect(screen.queryByRole("region", { name: "Tin nhắn nhóm khách" })).toBeNull());
  }
  expect(errors.mock.calls.filter(call => String(call[0]).includes("same key"))).toEqual([]);
  errors.mockRestore();
});

it("opens the group with the newest messages without an extra click", async () => {
  const quiet = { ...group, externalId: "g-0", displayName: "QUIET_GROUP", lastMessageAt: null, messageCount: 0 };
  const older = { ...group, externalId: "g-2", displayName: "OLDER_GROUP", lastMessageAt: "2026-10-29T02:00:00Z" };
  const fetcher = vi.fn(async (url: string) => {
    if (url.startsWith("/api/local/session?")) return Response.json({ tenantId: tenant, companyId: company, userId: user, roles: ["viewer"] });
    if (url.startsWith("/api/local/data-sources?")) return Response.json([]);
    if (url.startsWith("/api/local/group-sources?")) return Response.json({}, { status: 404 });
    if (url.startsWith("/api/local/bot-messages?")) return Response.json([quiet, older, group]);
    if (url.startsWith("/api/local/bot-messages/zalo?")) return Response.json({ items: [message(3)], hasMore: false });
    throw new Error("Unexpected owned fixture route");
  });
  vi.stubGlobal("fetch", fetcher); vi.stubGlobal("BroadcastChannel", undefined);
  render(<LocalAiWorkspace companyId={company} companyName="Fixture" />);
  fireEvent.click(await screen.findByRole("button", { name: "Tin nhắn" }));
  await screen.findByText("PRIVATE_TEXT_3");
  expect(fetcher.mock.calls.some(([url]) => String(url).includes("group=g-1&"))).toBe(true);
  expect(fetcher.mock.calls.some(([url]) => String(url).includes("group=g-2&") || String(url).includes("group=g-0&"))).toBe(false);
});

it("shows uploaded pictures as thumbnails that open full size, with bold coloured sender names", async () => {
  const fetcher = vi.fn(async (url: string) => {
    if (url.startsWith("/api/local/session?")) return Response.json({ tenantId: tenant, companyId: company, userId: user, roles: ["viewer"] });
    if (url.startsWith("/api/local/data-sources?")) return Response.json([]);
    if (url.startsWith("/api/local/bot-messages?")) return Response.json([group]);
    if (url.startsWith("/api/local/bot-messages/zalo?")) return Response.json({ items: [
      { ...message(3, "@Nguyễn Hoàng Đăng xem https://example.test/a giúp"), imageCount: 0 },
      { ...message(2, "Ảnh lỗi"), hasImage: true, imageCount: 2 }], hasMore: false });
    throw new Error("Unexpected owned fixture route");
  });
  vi.stubGlobal("fetch", fetcher); vi.stubGlobal("BroadcastChannel", undefined);
  render(<LocalAiWorkspace companyId={company} companyName="Fixture" />);
  fireEvent.click(await screen.findByRole("button", { name: "Tin nhắn" }));
  await screen.findByText("Ảnh lỗi");
  const thumbs = screen.getAllByRole("img");
  expect(thumbs).toHaveLength(2);
  expect(thumbs[1].getAttribute("src")).toBe(`/api/local/bot-messages/zalo/image?companyId=${company}&group=g-1&message=m2&position=1`);
  expect(screen.getByText("@Nguyễn Hoàng Đăng").tagName).toBe("STRONG");
  expect(screen.getByRole("link", { name: "https://example.test/a" }).getAttribute("rel")).toContain("noopener");
  expect(screen.getAllByText("Anh Minh")[0].className).toContain("font-bold");
  fireEvent.click(screen.getAllByRole("button", { name: /^Xem Ảnh 1 của Anh Minh/ })[0]);
  const viewer = screen.getByRole("dialog"); expect(viewer.querySelector("img")!.getAttribute("src")).toContain("position=0");
  fireEvent.keyDown(window, { key: "Escape" });
  expect(screen.queryByRole("dialog")).toBeNull();
  // A picture that cannot be loaded shows a short note instead of a broken image.
  fireEvent.error(screen.getAllByRole("img")[1]);
  expect(screen.getByText("Không tải được ảnh")).toBeTruthy();
});

it("groups a burst of pictures from one sender into one album that pages through every picture", async () => {
  const pic = (id: number, minute: number, who = "Huyền Trần") => ({ messageId: `p${id}`, senderName: who, text: "", hasImage: true, imageCount: 1,
    sentAt: `2026-10-30T08:${String(minute).padStart(2, "0")}:00Z` });
  const fetcher = vi.fn(async (url: string) => {
    if (url.startsWith("/api/local/session?")) return Response.json({ tenantId: tenant, companyId: company, userId: user, roles: ["viewer"] });
    if (url.startsWith("/api/local/data-sources?")) return Response.json([]);
    if (url.startsWith("/api/local/bot-messages?")) return Response.json([group]);
    if (url.startsWith("/api/local/bot-messages/zalo?")) return Response.json({ items: [
      { ...message(9, "Xong"), sentAt: "2026-10-30T08:30:00Z" }, pic(8, 20, "Trúc"),
      ...[7, 6, 5, 4, 3, 2, 1].map(n => pic(n, 10)), { ...message(0, "Gửi ảnh nhé"), senderName: "Huyền Trần", sentAt: "2026-10-30T08:09:00Z" }], hasMore: false });
    throw new Error("Unexpected owned fixture route");
  });
  vi.stubGlobal("fetch", fetcher); vi.stubGlobal("BroadcastChannel", undefined);
  render(<LocalAiWorkspace companyId={company} companyName="Fixture" />);
  fireEvent.click(await screen.findByRole("button", { name: "Tin nhắn" }));
  const album = await screen.findByRole("group", { name: "Album 7 ảnh" });
  expect(album.querySelectorAll("img")).toHaveLength(6); expect(album.textContent).toContain("+1");
  expect(screen.getByRole("list", { name: "Tin nhắn trong nhóm" }).querySelectorAll("li")).toHaveLength(4);
  fireEvent.click(screen.getByRole("button", { name: /^Xem Ảnh 6 của Huyền Trần/ }));
  const viewer = screen.getByRole("dialog"); expect(viewer.textContent).toContain("6/7");
  fireEvent.keyDown(window, { key: "ArrowRight" }); expect(screen.getByRole("dialog").textContent).toContain("7/7");
  expect(screen.getByRole("dialog").querySelector("img")!.getAttribute("src")).toContain("message=p7");
  fireEvent.click(screen.getByRole("button", { name: "Ảnh sau" })); expect(screen.getByRole("dialog").textContent).toContain("1/7");
});

it("marks mentions by the real names of people in the group, not by guessing words", async () => {
  const fetcher = vi.fn(async (url: string) => {
    if (url.startsWith("/api/local/session?")) return Response.json({ tenantId: tenant, companyId: company, userId: user, roles: ["viewer"] });
    if (url.startsWith("/api/local/data-sources?")) return Response.json([]);
    if (url.startsWith("/api/local/bot-messages?")) return Response.json([group]);
    if (url.startsWith("/api/local/bot-messages/zalo?")) return Response.json({ items: [
      { ...message(2, "@Ngô Đạt  Phần Mềm @Nguyễn Hoàng Đăng xem nhanh giúp."), senderName: "Huyền Trần" },
      { ...message(1, "@Huyền Trần OK Em. @Xuân Trinh , @All xem nhé"), senderName: "Tuấn Lê" }],
      hasMore: false, senders: ["Ngô Đạt  Phần Mềm", "Nguyễn Hoàng Đăng", "Huyền Trần", "Tuấn Lê"] });
    throw new Error("Unexpected owned fixture route");
  });
  vi.stubGlobal("fetch", fetcher); vi.stubGlobal("BroadcastChannel", undefined);
  render(<LocalAiWorkspace companyId={company} companyName="Fixture" />);
  fireEvent.click(await screen.findByRole("button", { name: "Tin nhắn" }));
  await screen.findByText("@Huyền Trần");
  const marked = [...screen.getByRole("list", { name: "Tin nhắn trong nhóm" }).querySelectorAll("strong")].map(x => x.textContent);
  expect(marked).toEqual(["@Huyền Trần", "@Xuân Trinh", "@All", "@Ngô Đạt  Phần Mềm", "@Nguyễn Hoàng Đăng"]);
});
