// @vitest-environment node
import { expect, it } from "vitest";
import { hasSameOrigin } from "./request-origin";

const origin = "http://localhost:3000";
const request = (headers: Record<string, string>, url = `${origin}/api/local/session/login`) => new Request(url, { method: "POST", headers });
it("accepts the request authority without forwarded headers", () => { expect(hasSameOrigin(request({ Origin: origin }))).toBe(true); });
it("accepts container URL with addressed browser Host", () => { expect(hasSameOrigin(request({ Host: "127.0.0.1:3000", Origin: "http://127.0.0.1:3000" }, "http://web:3000/path"))).toBe(true); });
it("accepts IPv6 addressed authority", () => { expect(hasSameOrigin(request({ Host: "[::1]:3000", Origin: "http://[::1]:3000" }, "http://web:3000/path"))).toBe(true); });
it("ignores spoofed forwarded hosts", () => { expect(hasSameOrigin(request({ Host: "localhost:3000", Origin: "https://foreign.invalid", "X-Forwarded-Host": "foreign.invalid" }))).toBe(false); });
it.each(["user@localhost:3000", "localhost:3000/path", "localhost:3000?query=1", "localhost:3000#fragment", "localhost:3000,foreign.invalid", "["])("refuses malformed Host authority: %s", (host) => {
  expect(hasSameOrigin(request({ Host: host, Origin: origin }))).toBe(false);
});
it("refuses non HTTP schemes", () => { expect(hasSameOrigin(request({ Origin: "null" }, "file:///tmp/local-session"))).toBe(false); });
