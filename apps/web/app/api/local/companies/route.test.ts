// @vitest-environment node
import { beforeEach, expect, it, vi } from "vitest";
import { GET } from "./route";

const fixture = vi.hoisted(() => ({ fetch: vi.fn(), enabled: vi.fn(() => true) }));
vi.mock("../../../../lib/local-ai-bff", async importOriginal => ({
  ...await importOriginal<typeof import("../../../../lib/local-ai-bff")>(),
  fetchCoreApi: fixture.fetch, isOfficeAiUiEnabled: fixture.enabled,
}));
const company = "22222222-2222-2222-2222-222222222222";
const choice = { companyId: company, companyName: "Company" };
const request = (query = `companyId=${company}`) => new Request(`http://localhost:3000/api/local/companies?${query}`);
beforeEach(() => { fixture.fetch.mockReset(); fixture.enabled.mockReturnValue(true); });
async function privateFailure(response: Response, status: number) {
  expect(response.status).toBe(status); expect(response.headers.get("cache-control")).toBe("no-store");
  expect(response.headers.get("set-cookie")).toBeNull(); expect(await response.text()).not.toContain("PRIVATE");
}
it("relays only verified two-field choices through the issued-session Core helper", async () => {
  fixture.fetch.mockResolvedValue(Response.json({ items: [choice] }));
  const response = await GET(request()); expect(await response.json()).toEqual({ items: [choice] });
  expect(response.headers.get("cache-control")).toBe("no-store"); expect(response.headers.get("set-cookie")).toBeNull();
  expect(fixture.fetch).toHaveBeenCalledWith("/api/auth/companies", company, { signal: expect.any(AbortSignal) });
});
it.each(["", "companyId=foreign", `companyId=${company}&companyId=${company}`])("rejects invalid or duplicate selection %s", async query => {
  await privateFailure(await GET(request(query)), 400); expect(fixture.fetch).not.toHaveBeenCalled();
});
it("requires enabled UI and issued session", async () => {
  fixture.enabled.mockReturnValue(false); await privateFailure(await GET(request()), 503); expect(fixture.fetch).not.toHaveBeenCalled();
  fixture.enabled.mockReturnValue(true); fixture.fetch.mockResolvedValue(null); await privateFailure(await GET(request()), 401);
});
it.each([401, 403, 500])("bounds upstream denial %i", async status => {
  fixture.fetch.mockResolvedValue(Response.json({ error: "PRIVATE diagnostics" }, { status, headers: { "Set-Cookie": "PRIVATE" } }));
  await privateFailure(await GET(request()), status === 500 ? 503 : status);
});
it.each([{ items: [choice], roles: ["admin"] }, { items: [{ ...choice, companyName: "PRIVATE\n" }] }, { items: [choice, choice] }])(
  "rejects malformed upstream choices without returning private data", async body => {
    fixture.fetch.mockResolvedValue(Response.json(body)); await privateFailure(await GET(request()), 503);
  });
it("bounds transport, malformed JSON and non-JSON replies", async () => {
  fixture.fetch.mockRejectedValue(new Error("PRIVATE")); await privateFailure(await GET(request()), 503);
  fixture.fetch.mockResolvedValue(new Response("PRIVATE")); await privateFailure(await GET(request()), 503);
  fixture.fetch.mockResolvedValue(new Response("{PRIVATE", { headers: { "Content-Type": "application/json" } })); await privateFailure(await GET(request()), 503);
});
it("rejects malformed UTF8 bytes rather than publishing a repaired authoritative company name", async () => {
  const prefix = new TextEncoder().encode(`{"items":[{"companyId":"${company}","companyName":"`);
  const suffix = new TextEncoder().encode('"}]}');
  const bytes = new Uint8Array(prefix.length + 1 + suffix.length); bytes.set(prefix); bytes[prefix.length] = 255; bytes.set(suffix, prefix.length + 1);
  fixture.fetch.mockResolvedValue(new Response(bytes, { headers: { "Content-Type": "application/json" } }));
  await privateFailure(await GET(request()), 503);
});
it("refuses isolated surrogate JSON escapes while keeping partial choices private", async () => {
  for (const companyName of ["PRIVATE\ud800", "PRIVATE\udc00", "PRIVATE\ud800x", "PRIVATE\udc00\ud800"]) {
    const json = JSON.stringify({ items: [choice, { companyId: "33333333-3333-3333-3333-333333333333", companyName }] });
    fixture.fetch.mockResolvedValue(new Response(json, { headers: { "Content-Type": "application/json" } }));
    await privateFailure(await GET(request()), 503);
  }
});
it.each(["Công ty Việt Nam", "Legitimate \ufffd name"])("preserves valid Unicode bytes exactly: %s", async name => {
  const value = { ...choice, companyName: name }; fixture.fetch.mockResolvedValue(Response.json({ items: [value] }));
  const response = await GET(request()); expect(response.status).toBe(200); expect(await response.json()).toEqual({ items: [value] });
});
it("bounds bytes even before structural parsing and cancels the oversized response", async () => {
  const cancelled = vi.fn(); let sent = false;
  const stream = new ReadableStream<Uint8Array>({ pull(controller) {
    if (!sent) { sent = true; controller.enqueue(new Uint8Array(128 * 1024 + 1)); }
  }, cancel: cancelled });
  fixture.fetch.mockResolvedValue(new Response(stream, { headers: { "Content-Type": "application/json" } }));
  await privateFailure(await GET(request()), 503); expect(cancelled).toHaveBeenCalled();
});
it("cancels a stalled streamed body when the shared deadline expires", async () => {
  const deadline = new AbortController(), cancelled = vi.fn();
  const timeout = vi.spyOn(AbortSignal, "timeout").mockReturnValue(deadline.signal);
  try {
    fixture.fetch.mockResolvedValue(new Response(new ReadableStream<Uint8Array>({ cancel: cancelled }),
      { headers: { "Content-Type": "application/json" } }));
    const pending = GET(request()); await Promise.resolve(); await Promise.resolve(); deadline.abort();
    await privateFailure(await pending, 503); expect(cancelled).toHaveBeenCalled(); expect(timeout).toHaveBeenCalledWith(10_000);
  } finally { timeout.mockRestore(); }
});
