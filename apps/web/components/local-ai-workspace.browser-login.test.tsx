import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { LocalAiWorkspace } from "./local-ai-workspace";
import { browserLoginDestination, type PublicBrowserLogin } from "../lib/browser-login-navigation";

const navigation = vi.hoisted(() => vi.fn());
vi.mock("../lib/browser-login-navigation", async importOriginal => ({
  ...await importOriginal<typeof import("../lib/browser-login-navigation")>(), navigateBrowserLogin: navigation,
}));
const renewal = vi.hoisted(() => vi.fn());
vi.mock("../lib/browser-silent-renew", async importOriginal => ({
  ...await importOriginal<typeof import("../lib/browser-silent-renew")>(), renewBrowserSessionSilently: renewal,
}));
const company = "22222222-2222-2222-2222-222222222222";
const companyB = "33333333-3333-3333-3333-333333333333";
const settings: PublicBrowserLogin = { authorizationEndpoint: "https://identity.example.invalid/authorize",
  clientId: "office", redirectUri: "https://office.example.invalid/api/local/session/oidc/callback", localHttp: false };
function authorization() {
  const url = new URL(settings.authorizationEndpoint);
  for (const [key, value] of Object.entries({ client_id: settings.clientId, redirect_uri: settings.redirectUri,
    response_type: "code", scope: "openid", code_challenge_method: "S256", state: "A".repeat(43),
    nonce: "B".repeat(43), code_challenge: "C".repeat(43) })) url.searchParams.set(key, value);
  return url.href;
}
function deferred() {
  let resolve!: (response: Response) => void;
  const promise = new Promise<Response>(done => { resolve = done; });
  return { resolve, promise };
}
const fetcher = vi.fn();
function workspace(id = company) {
  return <LocalAiWorkspace companyId={id} companyName="Synthetic Company" loginMode="browser" browserLogin={settings} />;
}
beforeEach(() => {
  navigation.mockReset(); renewal.mockReset().mockResolvedValue("failed"); fetcher.mockReset().mockImplementation(async (url: string) => {
    if (url.startsWith("/api/local/session?")) return Response.json({}, { status: 401 });
    if (url === "/api/local/session/oidc/binding") return Response.json({ ok: true });
    if (url === "/api/local/session/oidc/start") return Response.json({ authorizationUrl: authorization() });
    throw new Error("Unexpected fixture request");
  });
  vi.stubGlobal("fetch", fetcher); vi.stubGlobal("BroadcastChannel", undefined);
});
afterEach(() => vi.unstubAllGlobals());

it("settles binding before start and navigates only to the configured Code/S256 provider without collecting a password", async () => {
  const pending = deferred();
  fetcher.mockImplementationOnce(async () => Response.json({}, { status: 401 }));
  fetcher.mockImplementationOnce(() => pending.promise);
  const { container } = render(workspace());
  const button = await screen.findByRole("button", { name: "Đăng nhập doanh nghiệp" });
  expect(container.querySelector('input[name="password"],input[name="username"]')).toBeNull();
  fireEvent.click(button); fireEvent.submit(container.querySelector("form")!);
  expect(fetcher.mock.calls.map(([url]) => url)).toEqual([
    `/api/local/session?companyId=${company}`, "/api/local/session/oidc/binding",
  ]);
  await act(async () => pending.resolve(Response.json({ ok: true })));
  await waitFor(() => expect(navigation).toHaveBeenCalledWith(authorization()));
  const [url, init] = fetcher.mock.calls[2];
  expect(url).toBe("/api/local/session/oidc/start");
  expect(JSON.parse(init.body)).toEqual({ companyId: company });
  expect(init.signal).toBeUndefined(); expect(init.redirect).toBe("error"); expect(init.cache).toBe("no-store");
  expect(fetcher.mock.calls.some(([path]) => path === "/api/local/session/login")).toBe(false);
  expect(container.innerHTML).not.toMatch(/private-access|code_verifier|client_secret/);
});
it.each(["binding", "start"])("reports %s denial generically and never exposes upstream details", async phase => {
  const original = fetcher.getMockImplementation()!;
  fetcher.mockImplementation((url, init) => url === `/api/local/session/oidc/${phase}`
    ? Promise.resolve(Response.json({ error: "PRIVATE-PROVIDER-DETAILS" }, { status: 503 })) : original(url, init));
  render(workspace());
  fireEvent.click(await screen.findByRole("button", { name: "Đăng nhập doanh nghiệp" }));
  expect((await screen.findByRole("alert")).textContent).toBe("Không bắt đầu được đăng nhập. Hãy thử lại.");
  expect(document.body.textContent).not.toContain("PRIVATE-PROVIDER-DETAILS"); expect(navigation).not.toHaveBeenCalled();
  if (phase === "binding") expect(fetcher.mock.calls.some(([url]) => url === "/api/local/session/oidc/start")).toBe(false);
});
it.each(["javascript:alert(1)", "https://foreign.invalid/authorize", "https://identity.example.invalid/other"])("refuses unsafe navigation %s", async destination => {
  const original = fetcher.getMockImplementation()!;
  fetcher.mockImplementation((url, init) => url === "/api/local/session/oidc/start"
    ? Promise.resolve(Response.json({ authorizationUrl: destination })) : original(url, init));
  render(workspace()); fireEvent.click(await screen.findByRole("button", { name: "Đăng nhập doanh nghiệp" }));
  await screen.findByRole("alert"); expect(navigation).not.toHaveBeenCalled();
});
it.each(["binding", "start"])("does not navigate a superseded company and waits for %s cookie mutation settlement", async phase => {
  const pending = deferred(), original = fetcher.getMockImplementation()!;
  fetcher.mockImplementation((url, init) => url === `/api/local/session/oidc/${phase}` ? pending.promise : original(url, init));
  const { rerender } = render(workspace());
  fireEvent.click(await screen.findByRole("button", { name: "Đăng nhập doanh nghiệp" }));
  await waitFor(() => expect(fetcher.mock.calls.some(([url]) => url === `/api/local/session/oidc/${phase}`)).toBe(true));
  rerender(workspace(companyB));
  await act(async () => { await Promise.resolve(); });
  expect(fetcher.mock.calls.some(([url]) => url.includes(companyB))).toBe(false);
  await act(async () => pending.resolve(Response.json(phase === "binding" ? { ok: true } : { authorizationUrl: authorization() })));
  await waitFor(() => expect(fetcher.mock.calls.some(([url]) => url.includes(companyB))).toBe(true));
  expect(navigation).not.toHaveBeenCalled();
});
it("ignores a login continuation after unmount", async () => {
  const pending = deferred(), original = fetcher.getMockImplementation()!;
  fetcher.mockImplementation((url, init) => url === "/api/local/session/oidc/start" ? pending.promise : original(url, init));
  const { unmount } = render(workspace());
  fireEvent.click(await screen.findByRole("button", { name: "Đăng nhập doanh nghiệp" }));
  await waitFor(() => expect(fetcher.mock.calls.some(([url]) => url === "/api/local/session/oidc/start")).toBe(true));
  unmount(); await act(async () => pending.resolve(Response.json({ authorizationUrl: authorization() })));
  expect(navigation).not.toHaveBeenCalled();
});
it("keeps browser mode when its trusted configuration is unavailable", async () => {
  const { container } = render(<LocalAiWorkspace companyId={company} companyName="Synthetic" loginMode="browser" />);
  fireEvent.click(await screen.findByRole("button", { name: "Đăng nhập doanh nghiệp" }));
  expect((await screen.findByRole("alert")).textContent).toBe("Dịch vụ đăng nhập chưa sẵn sàng.");
  expect(container.querySelector("input")).toBeNull(); expect(navigation).not.toHaveBeenCalled();
  expect(fetcher).toHaveBeenCalledOnce();
});

it.each(["client_id", "redirect_uri", "response_type", "scope", "code_challenge_method", "state", "nonce", "code_challenge"])("refuses ambiguous/altered %s", key => {
  const duplicate = new URL(authorization()); duplicate.searchParams.append(key, duplicate.searchParams.get(key)!);
  expect(browserLoginDestination({ authorizationUrl: duplicate.href }, settings)).toBeNull();
  duplicate.searchParams.set(key, "wrong");
  expect(browserLoginDestination({ authorizationUrl: duplicate.href }, settings)).toBeNull();
});
it("allows explicit loopback HTTP and refuses remote HTTP, credentials, fragments or extra fields", () => {
  const local = { ...settings, authorizationEndpoint: "http://127.0.0.1:8081/authorize", localHttp: true };
  const url = authorization().replace(settings.authorizationEndpoint, local.authorizationEndpoint);
  expect(browserLoginDestination({ authorizationUrl: url }, local)).toBe(url);
  expect(browserLoginDestination({ authorizationUrl: url }, { ...local, localHttp: false })).toBeNull();
  const remote = { ...local, authorizationEndpoint: "http://remote.invalid/authorize" };
  expect(browserLoginDestination({ authorizationUrl: url.replace(local.authorizationEndpoint, remote.authorizationEndpoint) }, remote)).toBeNull();
  for (const invalid of [authorization() + "#fragment", authorization().replace("https://", "https://user:password@"), authorization() + "&code_verifier=private"]) {
    expect(browserLoginDestination({ authorizationUrl: invalid }, settings)).toBeNull();
  }
  expect(browserLoginDestination({ authorizationUrl: authorization(), accessToken: "private" }, settings)).toBeNull();
});

it("goes straight to the provider on first load so there is only one sign-in page, at most once a minute", async () => {
  window.sessionStorage.clear();
  const auto = () => <LocalAiWorkspace companyId={company} companyName="Synthetic Company" loginMode="browser" browserLogin={settings} autoStartLogin />;
  const first = render(auto());
  await waitFor(() => expect(navigation).toHaveBeenCalledWith(authorization()));
  expect(screen.getByText("Đang chuyển đến trang đăng nhập…")).toBeTruthy();
  first.unmount(); navigation.mockReset();
  // A provider callback that lands back here signed out must not redirect again in a loop.
  render(auto());
  await screen.findByText("Đăng nhập bằng tài khoản công ty để tiếp tục.");
  expect(navigation).not.toHaveBeenCalled();
  fireEvent.click(screen.getByRole("button", { name: "Đăng nhập doanh nghiệp" }));
  await waitFor(() => expect(navigation).toHaveBeenCalledOnce());
  expect(renewal).toHaveBeenCalledWith(company, settings);
  window.sessionStorage.clear();
});

it("renews a lapsed session in the background without going to the sign-in page", async () => {
  window.sessionStorage.clear();
  let signedIn = false;
  const original = fetcher.getMockImplementation()!;
  fetcher.mockImplementation((url, init) => url.startsWith("/api/local/session?") && signedIn
    ? Promise.resolve(Response.json({ tenantId: "11111111-1111-1111-1111-111111111111", companyId: company,
      userId: "44444444-4444-4444-4444-444444444444", roles: ["member"] })) : original(url, init));
  let finish!: (result: string) => void;
  renewal.mockImplementation(() => new Promise(resolve => { finish = resolve; }));
  render(<LocalAiWorkspace companyId={company} companyName="Synthetic Company" loginMode="browser" browserLogin={settings} autoStartLogin />);
  await screen.findByText("Đang gia hạn phiên đăng nhập…");
  signedIn = true;
  await act(async () => finish("renewed"));
  await waitFor(() => expect(screen.queryByText("Đang gia hạn phiên đăng nhập…")).toBeNull());
  await waitFor(() => expect(screen.queryByRole("button", { name: "Đăng nhập doanh nghiệp" })).toBeNull());
  expect(navigation).not.toHaveBeenCalled();
  expect(renewal).toHaveBeenCalledOnce();
});

it("leaves background renewal off when sign-in is driven by hand", async () => {
  window.sessionStorage.setItem("aioffice-auto-login", "off");
  render(<LocalAiWorkspace companyId={company} companyName="Synthetic Company" loginMode="browser" browserLogin={settings} autoStartLogin />);
  await screen.findByText("Đăng nhập bằng tài khoản công ty để tiếp tục.");
  expect(renewal).not.toHaveBeenCalled(); expect(navigation).not.toHaveBeenCalled();
  window.sessionStorage.clear();
});

it("reopens the page the user was on after coming back from the sign-in page", async () => {
  window.history.replaceState(null, "", "/");
  window.sessionStorage.setItem("aioffice-return-path", JSON.stringify({ path: "/cong-viec", at: Date.now() }));
  fetcher.mockImplementation(async (url: string) => url.startsWith("/api/local/session?")
    ? Response.json({ tenantId: "11111111-1111-1111-1111-111111111111", companyId: company,
      userId: "44444444-4444-4444-4444-444444444444", roles: ["member"] }) : Response.json({}, { status: 404 }));
  render(workspace());
  await waitFor(() => expect(window.location.pathname).toBe("/cong-viec"));
  expect(window.sessionStorage.getItem("aioffice-return-path")).toBeNull();
  window.history.replaceState(null, "", "/");
});
