// @vitest-environment node
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { POST } from "./route";

const companyId = "22222222-2222-2222-2222-222222222222";
const credentials = { username: "owner", password: "test-password", companyId };
const token = "test-access-token";
const fetchMock = vi.fn();

function request(body: unknown = credentials) {
  return new Request("http://localhost:3000/api/local/session/login", {
    method: "POST",
    headers: { "Content-Type": "application/json", Origin: "http://localhost:3000" },
    body: JSON.stringify(body),
  });
}

async function failure(response: Response, status: number) {
  expect(response.status).toBe(status);
  expect(response.headers.get("cache-control")).toBe("no-store");
  expect(response.headers.get("set-cookie")).toBeNull();
  const body = await response.text();
  expect(body).not.toContain(credentials.password);
  expect(body).not.toContain(token);
  expect(body).not.toContain("private-upstream-details");
}

function identityAccepted() {
  fetchMock.mockResolvedValueOnce(Response.json({ access_token: token, expires_in: 300 }));
}

beforeEach(() => {
  fetchMock.mockReset();
  vi.stubGlobal("fetch", fetchMock);
  vi.stubEnv("AIOFFICE_LOCAL_UI_ENABLED", "true");
  vi.stubEnv("AIOFFICE_LOCAL_UI_COOKIE_SECURE", "false");
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.unstubAllEnvs();
});

describe("local sign-in response privacy", () => {
  it("does not cache or issue a session when local sign-in is disabled", async () => {
    vi.stubEnv("AIOFFICE_LOCAL_UI_ENABLED", "false");
    await failure(await POST(request()), 503);
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("rejects malformed request JSON without contacting the identity service", async () => {
    const malformed = new Request("http://localhost:3000/api/local/session/login", {
      method: "POST", headers: { "Content-Type": "application/json", Origin: "http://localhost:3000" }, body: "{",
    });
    await failure(await POST(malformed), 400);
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it.each([
    null,
    {},
    { ...credentials, username: "" },
    { ...credentials, password: "bad\npassword" },
    { ...credentials, companyId: "00000000-0000-0000-0000-000000000000" },
    { ...credentials, tenantId: "foreign-authority" },
    { ...credentials, roles: ["admin"] },
    [credentials],
  ])("rejects an invalid login payload without caching it", async (body) => {
    await failure(await POST(request(body)), 400);
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("does not cache identity-service transport failures", async () => {
    fetchMock.mockRejectedValueOnce(new Error("private-upstream-details"));
    await failure(await POST(request()), 503);
  });

  it("does not cache or reflect invalid credentials from the identity service", async () => {
    fetchMock.mockResolvedValueOnce(new Response("private-upstream-details", { status: 401 }));
    await failure(await POST(request()), 401);
  });

  it("does not cache malformed identity JSON", async () => {
    fetchMock.mockResolvedValueOnce(new Response("not json", { status: 200 }));
    await failure(await POST(request()), 502);
  });

  it.each([null, { access_token: "" }, { access_token: 123 }])("does not issue a session for an invalid token response", async (body) => {
    fetchMock.mockResolvedValueOnce(Response.json(body));
    await failure(await POST(request()), 502);
  });

  it("does not cache a company denial or issue a session", async () => {
    identityAccepted();
    fetchMock.mockResolvedValueOnce(new Response("private-upstream-details", { status: 403 }));
    await failure(await POST(request()), 403);
  });

  it("does not cache an authorization API failure", async () => {
    identityAccepted();
    fetchMock.mockResolvedValueOnce(new Response("private-upstream-details", { status: 500 }));
    await failure(await POST(request()), 502);
  });

  it("does not cache authorization API transport errors", async () => {
    identityAccepted();
    fetchMock.mockRejectedValueOnce(new Error("private-upstream-details"));
    await failure(await POST(request()), 503);
  });

  it("fails closed on malformed authorization JSON", async () => {
    identityAccepted();
    fetchMock.mockResolvedValueOnce(new Response("not json", { status: 200 }));
    await failure(await POST(request()), 502);
  });

  it("issues only an HttpOnly session after authoritative company authorization", async () => {
    const context = {
      tenantId: "11111111-1111-1111-1111-111111111111", companyId,
      userId: "33333333-3333-3333-3333-333333333333", roles: ["admin"],
    };
    identityAccepted();
    fetchMock.mockResolvedValueOnce(Response.json(context));
    const response = await POST(request());
    expect(response.status).toBe(200);
    expect(response.headers.get("cache-control")).toBe("no-store");
    expect(response.headers.get("set-cookie")).toContain("HttpOnly");
    expect(response.headers.get("set-cookie")).toContain("SameSite=lax");
    expect(await response.json()).toEqual({ ok: true, context });
    const options = fetchMock.mock.calls[1][1];
    expect(options.cache).toBe("no-store");
    expect(options.headers.Authorization).toBe(`Bearer ${token}`);
    expect(options.headers["X-AIOffice-Company-Id"]).toBe(companyId);
  });
});

describe("local sign-in browser and input boundary", () => {
  const url = "http://localhost:3000/api/local/session/login";
  const origin = "http://localhost:3000";
  function raw(body: BodyInit, headers: Record<string, string> = {}) {
    return new Request(url, { method: "POST", headers: { "Content-Type": "application/json", Origin: origin, ...headers }, body });
  }

  it.each([null, "null", "https://foreign.invalid", "http://localhost:3001", `${origin}/`, `${origin}, https://foreign.invalid`])("refuses untrusted Origin before identity access or cookies: %s", async (value) => {
    const headers: Record<string, string> = { "Content-Type": "application/json" };
    if (value !== null) headers.Origin = value;
    await failure(await POST(new Request(url, { method: "POST", headers, body: JSON.stringify(credentials) })), 403);
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("ignores attacker forwarded authority", async () => {
    await failure(await POST(raw(JSON.stringify(credentials), { Host: "localhost:3000", Origin: "https://foreign.invalid", "X-Forwarded-Host": "foreign.invalid", "X-Forwarded-Proto": "https" })), 403);
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it.each(["localhost:3000/", "localhost:3000/a/..", "localhost:3000\\a\\.."]) ("refuses raw Host normalization before identity or cookie issuance: %s", async (host) => {
    await failure(await POST(raw(JSON.stringify(credentials), { Host: host })), 403);
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("accepts the addressed Host when Next uses a container hostname", async () => {
    fetchMock.mockResolvedValueOnce(new Response("", { status: 401 }));
    const addressed = new Request("http://web:3000/api/local/session/login", {
      method: "POST", headers: { Host: "127.0.0.1:3000", Origin: "http://127.0.0.1:3000", "Content-Type": "application/json" }, body: JSON.stringify(credentials),
    });
    await failure(await POST(addressed), 401);
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it.each(["text/plain", "application/x-www-form-urlencoded", ""]) ("requires JSON media type: %s", async (mediaType) => {
    await failure(await POST(raw(JSON.stringify(credentials), { "Content-Type": mediaType })), 415);
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("accepts exactly8192 bytes without changing credential strings", async () => {
    const json = JSON.stringify(credentials);
    fetchMock.mockResolvedValueOnce(new Response("", { status: 401 }));
    await failure(await POST(raw(" ".repeat(8192 - Buffer.byteLength(json)) + json)), 401);
    const form = fetchMock.mock.calls[0][1].body as URLSearchParams;
    expect(form.get("username")).toBe(credentials.username);
    expect(form.get("password")).toBe(credentials.password);
  });

  it.each([" ".repeat(8193), JSON.stringify({ ...credentials, password: "á".repeat(4096) })])("bounds actual UTF8 bytes before identity access", async (body) => {
    await failure(await POST(raw(body, { "Content-Length": "1" })), 413);
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("rejects invalid UTF8 instead of replacing bytes", async () => {
    await failure(await POST(raw(new Uint8Array([255]).buffer)), 400);
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("keeps oversized refusal when stream cancellation throws", async () => {
    const cancel = vi.fn().mockRejectedValue(new Error("private-upstream-details"));
    const stream = new ReadableStream<Uint8Array>({ start(controller) { controller.enqueue(new Uint8Array(8193)); }, cancel });
    const init: RequestInit & { duplex: "half" } = { method: "POST", headers: { Origin: origin, "Content-Type": "application/json" }, body: stream, duplex: "half" };
    await failure(await POST(new Request(url, init)), 413);
    expect(cancel).toHaveBeenCalledTimes(1);
    expect(fetchMock).not.toHaveBeenCalled();
  });
});
