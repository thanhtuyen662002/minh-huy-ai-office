// @vitest-environment node
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { POST } from "./route";

const origin = "http://localhost:3000";
const request = (value: string | null = origin) => new Request(`${origin}/api/local/session/logout`, {
  method: "POST", headers: value === null ? {} : { Origin: value },
});
beforeEach(() => {
  vi.stubEnv("AIOFFICE_LOCAL_UI_ENABLED", "true");
  vi.stubEnv("AIOFFICE_LOCAL_UI_COOKIE_SECURE", "false");
});
afterEach(() => { vi.unstubAllEnvs(); });

it.each([null, "null", "https://foreign.invalid", "http://localhost:3001", `${origin}/`])("cannot clear a cookie from untrusted Origin: %s", async (value) => {
  const response = await POST(request(value));
  expect(response.status).toBe(403);
  expect(response.headers.get("cache-control")).toBe("no-store");
  expect(response.headers.get("set-cookie")).toBeNull();
});

it("does not clear cookies when the local UI is disabled", async () => {
  vi.stubEnv("AIOFFICE_LOCAL_UI_ENABLED", "false");
  const response = await POST(request());
  expect(response.status).toBe(503);
  expect(response.headers.get("cache-control")).toBe("no-store");
  expect(response.headers.get("set-cookie")).toBeNull();
});

it.each([false, true])("clears the same-origin HttpOnly cookie with secure=%s", async (secure) => {
  vi.stubEnv("AIOFFICE_LOCAL_UI_COOKIE_SECURE", String(secure));
  const response = await POST(request());
  expect(response.status).toBe(200);
  expect(response.headers.get("cache-control")).toBe("no-store");
  const cookie = response.headers.get("set-cookie");
  expect(cookie).toContain("aioffice_local_access_token=");
  expect(cookie).toContain("Max-Age=0");
  expect(cookie).toContain("HttpOnly");
  expect(cookie).toContain("SameSite=lax");
  expect(cookie?.includes("Secure")).toBe(secure);
});
