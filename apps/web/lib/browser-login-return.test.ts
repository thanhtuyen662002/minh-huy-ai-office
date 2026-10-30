import { afterEach, expect, it, vi } from "vitest";
import { navigateBrowserLogin, takeLoginReturnPath } from "./browser-login-navigation";

afterEach(() => { window.sessionStorage.clear(); vi.restoreAllMocks(); window.history.replaceState(null, "", "/"); });

it("remembers the open page once across a sign-in redirect", () => {
  const assign = vi.fn(); vi.spyOn(window, "location", "get").mockReturnValue({ ...window.location, pathname: "/ket-noi", assign });
  navigateBrowserLogin("https://login.example.test/auth");
  expect(assign).toHaveBeenCalledWith("https://login.example.test/auth");
  vi.restoreAllMocks();
  expect(takeLoginReturnPath()).toBe("/ket-noi");
  expect(takeLoginReturnPath()).toBeNull();
});

it.each([["/https://evil.test", 0], ["//evil.test", 0], ["/ket-noi", 20 * 60_000]])("ignores unsafe or stale path %s", (path, age) => {
  window.sessionStorage.setItem("aioffice-return-path", JSON.stringify({ path, at: Date.now() - age }));
  expect(takeLoginReturnPath()).toBeNull();
});
