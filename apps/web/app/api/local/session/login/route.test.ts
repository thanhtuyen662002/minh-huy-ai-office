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
    headers: { "Content-Type": "application/json" },
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
    const malformed = new Request("http://localhost:3000/api/local/session/login", { method: "POST", body: "{" });
    await failure(await POST(malformed), 400);
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it.each([
    null,
    {},
    { ...credentials, username: "" },
    { ...credentials, password: "bad\npassword" },
    { ...credentials, companyId: "00000000-0000-0000-0000-000000000000" },
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
