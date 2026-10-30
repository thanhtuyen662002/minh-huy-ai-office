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
