import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { LocalAiWorkspace } from "./local-ai-workspace";
import type { PublicBrowserLogin } from "../lib/browser-login-navigation";

const navigation = vi.hoisted(() => vi.fn());
vi.mock("../lib/browser-login-navigation", async importOriginal => ({
  ...await importOriginal<typeof import("../lib/browser-login-navigation")>(), navigateBrowserLogin: navigation,
}));
const companyA = "22222222-2222-2222-2222-222222222222", companyB = "66666666-6666-6666-6666-666666666666";
const choices = [{ companyId: companyA, companyName: "Authorized A" }, { companyId: companyB, companyName: "Authorized B" }];
const settings: PublicBrowserLogin = { authorizationEndpoint: "https://id.example.invalid/authorize", clientId: "office",
  redirectUri: "https://office.example.invalid/api/local/session/oidc/callback", localHttp: false };
function authorization() {
  const url = new URL(settings.authorizationEndpoint);
  for (const [key, value] of Object.entries({ client_id: settings.clientId, redirect_uri: settings.redirectUri,
    response_type: "code", scope: "openid", code_challenge_method: "S256", state: "A".repeat(43),
    nonce: "B".repeat(43), code_challenge: "C".repeat(43) })) url.searchParams.set(key, value);
  return url.href;
}
function deferred() {
  let resolve!: (response: Response) => void; const promise = new Promise<Response>(done => { resolve = done; }); return { promise, resolve };
}
const fetcher = vi.fn();
function workspace(company = companyA, mode: "browser" | "local" = "browser") {
  return <LocalAiWorkspace companyId={company} companyName="Unverified default name" loginMode={mode} browserLogin={settings} />;
}
function fixture(override: (url: string, init?: RequestInit) => Promise<Response> | Response | undefined = () => undefined) {
  fetcher.mockImplementation(async (url: string, init?: RequestInit) => {
    const result = override(url, init); if (result !== undefined) return result;
    const companyId = new URL(url, "http://fixture.invalid").searchParams.get("companyId") ?? companyA;
    if (url.startsWith("/api/local/session?")) return Response.json({ tenantId: "tenant", companyId, userId: "user", roles: ["member"] });
    if (url.startsWith("/api/local/companies?")) return Response.json({ items: choices });
    if (url.startsWith("/api/local/data-sources?")) return Response.json([{ id: "33333333-3333-3333-3333-333333333333", logicalName: "PRIVATE-SOURCE-" + companyId,
      kind: "SqlServer", environment: "Test", purpose: "Synthetic", allowRead: true, allowWrite: false, maxConcurrency: 1, isEnabled: true }]);
    if (url === "/api/local/session/oidc/start") return Response.json({ authorizationUrl: authorization() });
    if (url === "/api/local/session/logout") return Response.json({ ok: true });
    throw new Error("Unexpected synthetic route");
  });
}
beforeEach(() => { navigation.mockReset(); fetcher.mockReset(); vi.stubGlobal("fetch", fetcher); vi.stubGlobal("BroadcastChannel", undefined); });
afterEach(() => { vi.unstubAllGlobals(); vi.restoreAllMocks(); });

it("lists only authoritative choices and clears all old private controls before provider cookie mutation settles", async () => {
  const pending = deferred(); fixture(url => url === "/api/local/session/oidc/start" ? pending.promise : undefined);
  const { container } = render(workspace()); const selector = await screen.findByLabelText("Chuyển công ty");
  expect(screen.getAllByRole("option", { name: /Authorized/ })).toHaveLength(2);
  await screen.findByRole("option", { name: "PRIVATE-SOURCE-" + companyA });
  fireEvent.change(container.querySelector("textarea")!, { target: { value: "PRIVATE-OLD-DRAFT" } });
  fireEvent.change(selector, { target: { value: companyB } });
  await screen.findByText("Đang kiểm tra phiên đăng nhập…");
  expect(container.textContent).not.toMatch(/PRIVATE|Authorized/); expect(container.querySelector("textarea,input,select,header,nav")).toBeNull();
  const call = fetcher.mock.calls.find(([url]) => url === "/api/local/session/oidc/start")!;
  expect(JSON.parse(call[1].body)).toEqual({ companyId: companyB }); expect(call[1].signal).toBeUndefined();
  await act(async () => pending.resolve(Response.json({ authorizationUrl: authorization() })));
  await waitFor(() => expect(navigation).toHaveBeenCalledWith(authorization()));
  expect(fetcher.mock.calls.some(([url]) => url === "/api/local/session/login")).toBe(false);
});

it("keeps current company and never starts authentication for forged or same selections", async () => {
  fixture(); render(workspace()); const selector = await screen.findByLabelText("Chuyển công ty");
  fireEvent.change(selector, { target: { value: "foreign" } }); fireEvent.change(selector, { target: { value: companyA } });
  await act(async () => { await Promise.resolve(); }); expect(navigation).not.toHaveBeenCalled();
  expect(fetcher.mock.calls.some(([url]) => url === "/api/local/session/oidc/start")).toBe(false);
});

it("restores fresh verified context after start denial without returning the old private draft or upstream diagnostics", async () => {
  fixture(url => url === "/api/local/session/oidc/start" ? Response.json({ error: "PRIVATE-UPSTREAM" }, { status: 403 }) : undefined);
  const { container } = render(workspace()); const selector = await screen.findByLabelText("Chuyển công ty");
  await screen.findByRole("option", { name: "PRIVATE-SOURCE-" + companyA });
  fireEvent.change(container.querySelector("textarea")!, { target: { value: "PRIVATE-OLD-DRAFT" } });
  fireEvent.change(selector, { target: { value: companyB } });
  await screen.findByText("Không chuyển được công ty. Hãy thử lại.");
  expect(container.textContent).not.toContain("PRIVATE-UPSTREAM"); expect((container.querySelector("textarea") as HTMLTextAreaElement).value).toBe("");
  expect(navigation).not.toHaveBeenCalled();
});

it("discards delayed old-company directory replies after a scope generation changes", async () => {
  const pending = deferred(); fixture(url => url.startsWith(`/api/local/companies?companyId=${companyA}`) ? pending.promise : undefined);
  const { container, rerender } = render(workspace()); await screen.findByRole("option", { name: "PRIVATE-SOURCE-" + companyA });
  rerender(workspace(companyB)); await screen.findByLabelText("Chuyển công ty");
  await act(async () => pending.resolve(Response.json({ items: [{ companyId: companyA, companyName: "PRIVATE-OLD-COMPANY" }] })));
  expect(container.textContent).not.toContain("PRIVATE-OLD-COMPANY"); expect((screen.getByLabelText("Chuyển công ty") as HTMLSelectElement).value).toBe(companyB);
});

it("withholds malformed or non-current directory choices and offers a bounded retry", async () => {
  let denied = true; fixture(url => url.startsWith("/api/local/companies?") && denied
    ? Response.json({ items: [choices[1]], error: "PRIVATE" }) : undefined);
  const { container } = render(workspace()); const retry = await screen.findByRole("button", { name: "Làm mới công ty" });
  expect(screen.queryByLabelText("Chuyển công ty")).toBeNull(); expect(container.textContent).not.toContain("Authorized B");
  denied = false; fireEvent.click(retry); await screen.findByLabelText("Chuyển công ty");
});

it("preserves explicit local development mode without introducing browser company cookies", async () => {
  fixture(); render(workspace(companyA, "local")); await screen.findByRole("option", { name: "PRIVATE-SOURCE-" + companyA });
  expect(screen.queryByLabelText("Chuyển công ty")).toBeNull(); expect(fetcher.mock.calls.some(([url]) => url.startsWith("/api/local/companies?"))).toBe(false);
});
