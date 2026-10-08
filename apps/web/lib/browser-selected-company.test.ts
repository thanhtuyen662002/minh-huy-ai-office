// @vitest-environment node
import { beforeEach, expect, it, vi } from "vitest";
import { readSelectedBrowserCompany } from "./browser-selected-company";

const fixture = vi.hoisted(() => ({ runtime: vi.fn(), cookies: vi.fn(), read: vi.fn() }));
vi.mock("next/headers", () => ({ cookies: fixture.cookies }));
vi.mock("./browser-auth-runtime", () => ({ getBrowserAuthRuntime: fixture.runtime }));
beforeEach(() => {
  fixture.read.mockReset(); fixture.cookies.mockReset(); fixture.runtime.mockReset();
  fixture.runtime.mockReturnValue({ settings: { localHttp: true }, sessions: { read: fixture.read } });
  fixture.cookies.mockResolvedValue({ get: (name: string) => ({ value: name.includes("binding") ? "private-binding" : "private-sid" }) });
});
it("uses the shared private session instead of browser URL/storage/default claims", async () => {
  fixture.read.mockResolvedValue({ companyId: "22222222-2222-2222-2222-222222222222", accessToken: "PRIVATE", subject: "PRIVATE" });
  expect(await readSelectedBrowserCompany()).toBe("22222222-2222-2222-2222-222222222222");
  expect(fixture.read).toHaveBeenCalledWith("private-binding", "private-sid");
});
it("returns no selection after expiry, revoke, missing cookies, unavailable config or coordination outage", async () => {
  fixture.read.mockResolvedValue(null); expect(await readSelectedBrowserCompany()).toBeNull();
  fixture.read.mockRejectedValue(new Error("PRIVATE")); expect(await readSelectedBrowserCompany()).toBeNull();
  fixture.read.mockClear(); fixture.cookies.mockResolvedValue({ get: () => undefined });
  expect(await readSelectedBrowserCompany()).toBeNull(); expect(fixture.read).not.toHaveBeenCalled();
  fixture.runtime.mockReturnValue(null); expect(await readSelectedBrowserCompany()).toBeNull();
});
