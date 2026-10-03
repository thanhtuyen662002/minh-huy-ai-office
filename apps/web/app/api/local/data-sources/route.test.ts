// @vitest-environment node
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { POST } from "./route";
import { PUT } from "./[dataSourceId]/route";

const session = vi.hoisted(() => ({ token: "issued-session-token" as string | null }));
vi.mock("next/headers", () => ({
  cookies: async () => ({ get: () => session.token ? { value: session.token } : undefined }),
}));

const companyId = "22222222-2222-2222-2222-222222222222";
const sourceId = "44444444-4444-4444-4444-444444444444";
const origin = "http://localhost:3000";
const metadata = {
  logicalName: "company.erp.test", kind: "sql-server", environment: "test", purpose: "acceptance",
  connectionSecretReference: "secretref://env/test-only", allowRead: true, allowWrite: false,
  maxConcurrency: 2, isEnabled: true,
};
const fetchMock = vi.fn();

const mutations = [
  { method: "POST", path: "/api/data-sources/", invoke: (request: Request) => POST(request) },
  { method: "PUT", path: `/api/data-sources/${sourceId}`, invoke: (request: Request) => PUT(request, { params: Promise.resolve({ dataSourceId: sourceId }) }) },
];

function request(method: string, body: string = JSON.stringify(metadata), company: string = companyId, requestOrigin: string | null = origin) {
  const headers: Record<string, string> = { "Content-Type": "application/json" };
  if (requestOrigin !== null) headers.Origin = requestOrigin;
  return new Request(`${origin}/api/local/data-sources?companyId=${company}`, { method, body, headers });
}

beforeEach(() => {
  session.token = "issued-session-token";
  fetchMock.mockReset();
  vi.stubGlobal("fetch", fetchMock);
  vi.stubEnv("AIOFFICE_LOCAL_UI_ENABLED", "true");
  vi.stubEnv("AIOFFICE_LOCAL_CORE_API_URL", "http://127.0.0.1:8080");
});
afterEach(() => { vi.unstubAllGlobals(); vi.unstubAllEnvs(); });

describe.each(mutations)("local data source $method", ({ method, path, invoke }) => {
  it("accepts the browser authority when Next.js uses its internal container URL", async () => {
    fetchMock.mockResolvedValueOnce(Response.json({ id: sourceId }, { status: method === "POST" ? 201 : 200 }));
    const response = await invoke(new Request(`http://0.0.0.0:3000/api/local/data-sources?companyId=${companyId}`, {
      method, body: JSON.stringify(metadata),
      headers: { "Content-Type": "application/json", Host: "127.0.0.1:3000", Origin: "http://127.0.0.1:3000" },
    }));
    expect(response.status).toBe(method === "POST" ? 201 : 200);
    expect(fetchMock).toHaveBeenCalledOnce();
    expect(fetchMock.mock.calls[0][0]).toBe(`http://127.0.0.1:8080${path}`);
  });

  it("rejects a foreign origin even when its forwarded host matches", async () => {
    const response = await invoke(new Request(`http://0.0.0.0:3000/api/local/data-sources?companyId=${companyId}`, {
      method, body: JSON.stringify(metadata),
      headers: { "Content-Type": "application/json", Host: "127.0.0.1:3000", Origin: "https://foreign.example.invalid", "X-Forwarded-Host": "foreign.example.invalid" },
    }));
    expect(response.status).toBe(403);
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it.each(["user@localhost:3000", "localhost:3000/path", "localhost:3000?query=x", "localhost:3000#fragment"])("rejects a malformed authority", async (host) => {
    const response = await invoke(new Request(`http://0.0.0.0:3000/api/local/data-sources?companyId=${companyId}`, {
      method, body: JSON.stringify(metadata),
      headers: { "Content-Type": "application/json", Host: host, Origin: origin },
    }));
    expect(response.status).toBe(403);
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("forwards the issued HttpOnly session and company selector to Core API", async () => {
    fetchMock.mockResolvedValueOnce(Response.json({ id: sourceId }, { status: method === "POST" ? 201 : 200, headers: { "Set-Cookie": "upstream-secret=discard" } }));
    const response = await invoke(request(method));
    expect(response.status).toBe(method === "POST" ? 201 : 200);
    expect(response.headers.get("cache-control")).toBe("no-store");
    expect(response.headers.get("set-cookie")).toBeNull();
    expect(await response.json()).toEqual({ id: sourceId });
    expect(fetchMock).toHaveBeenCalledOnce();
    const [url, options] = fetchMock.mock.calls[0];
    expect(url).toBe(`http://127.0.0.1:8080${path}`);
    expect(options.method).toBe(method);
    expect(options.cache).toBe("no-store");
    expect(options.headers.get("Authorization")).toBe("Bearer issued-session-token");
    expect(options.headers.get("X-AIOffice-Company-Id")).toBe(companyId);
    expect(JSON.parse(options.body)).toEqual(metadata);
  });

  it("preserves the authoritative management denial without returning credentials", async () => {
    fetchMock.mockResolvedValueOnce(new Response(null, { status: 403 }));
    const response = await invoke(request(method));
    expect(response.status).toBe(403);
    expect(response.headers.get("cache-control")).toBe("no-store");
    expect(await response.text()).toBe("");
  });

  it.each([null, "https://foreign.example.invalid", "http://localhost:3001"])("rejects an untrusted origin before contacting Core API", async (requestOrigin) => {
    const response = await invoke(request(method, JSON.stringify(metadata), companyId, requestOrigin));
    expect(response.status).toBe(403);
    expect(response.headers.get("cache-control")).toBe("no-store");
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it.each(["not-a-company", "00000000-0000-0000-0000-000000000000"])("rejects an invalid company selector", async (company) => {
    const response = await invoke(request(method, JSON.stringify(metadata), company));
    expect(response.status).toBe(400);
    expect(response.headers.get("cache-control")).toBe("no-store");
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it.each(["{", "null", "[]", "\"text\""])("rejects malformed or non-object metadata", async (body) => {
    const response = await invoke(request(method, body));
    expect(response.status).toBe(400);
    expect(response.headers.get("cache-control")).toBe("no-store");
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("rejects a missing session without contacting Core API", async () => {
    session.token = null;
    const response = await invoke(request(method));
    expect(response.status).toBe(401);
    expect(response.headers.get("cache-control")).toBe("no-store");
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("disables mutations with the local UI", async () => {
    vi.stubEnv("AIOFFICE_LOCAL_UI_ENABLED", "false");
    const response = await invoke(request(method));
    expect(response.status).toBe(503);
    expect(response.headers.get("cache-control")).toBe("no-store");
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("masks transport details and returns a non-cacheable availability failure", async () => {
    fetchMock.mockRejectedValueOnce(new Error("private-upstream-details"));
    const response = await invoke(request(method));
    expect(response.status).toBe(502);
    expect(response.headers.get("cache-control")).toBe("no-store");
    expect(await response.text()).not.toContain("private-upstream-details");
  });
});

it("rejects a malformed source ID before fetching or constructing an upstream path", async () => {
  const response = await PUT(request("PUT"), { params: Promise.resolve({ dataSourceId: "../foreign" }) });
  expect(response.status).toBe(400);
  expect(response.headers.get("cache-control")).toBe("no-store");
  expect(fetchMock).not.toHaveBeenCalled();
});
