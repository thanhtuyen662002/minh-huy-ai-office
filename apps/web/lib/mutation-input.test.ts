// @vitest-environment node
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { POST as task } from "../app/api/local/tasks/route";
import { PUT as metadata } from "../app/api/local/data-sources/[dataSourceId]/metadata/route";
import { POST as registration } from "../app/api/local/data-sources/read-only-registration/route";
import { MUTATION_BODY_TIMEOUT_MS, readBoundedRequestJson } from "./bounded-request-json";

const fixture = vi.hoisted(() => ({ cookies: vi.fn(), read: vi.fn() }));
vi.mock("next/headers", () => ({ cookies: fixture.cookies }));
vi.mock("./browser-auth-runtime", () => ({ getBrowserAuthRuntime: () => ({
  settings: { publicOrigin: "http://127.0.0.1:3000", localHttp: true },
  coreApiOrigin: "http://core-api:8080", sessions: { read: fixture.read },
}) }));
const company = "22222222-2222-2222-2222-222222222222";
const source = "33333333-3333-3333-3333-333333333333";
const origin = "http://127.0.0.1:3000";
const fetcher = vi.fn();
const cases = [
  { name: "task", method: "POST", maxBytes: 32 * 1024,
    value: { dataSourceId: source, question: "\u0800".repeat(4000) }, invoke: task },
  { name: "metadata", method: "PUT", maxBytes: 8192,
    value: { logicalName: "\u0800".repeat(200), purpose: "\u0800".repeat(200), maxConcurrency: 1024, isEnabled: false },
    invoke: (r: Request) => metadata(r, { params: Promise.resolve({ dataSourceId: source }) }) },
  { name: "registration", method: "POST", maxBytes: 8192,
    value: { bindingId: source, bindingVersion: "9223372036854775807", operationId: company,
      logicalName: "\u0800".repeat(200), purpose: "\u0800".repeat(200), environment: "Production", maxConcurrency: 2 },
    invoke: registration },
];
function request(method: string, body: BodyInit, options: RequestInit = {}) {
  return new Request(`${origin}/api/local/mutation?companyId=${company}`, {
    method, body, duplex: "half", headers: { Origin: origin, "Content-Type": "application/json" }, ...options,
  } as RequestInit);
}
beforeEach(() => {
  fixture.cookies.mockReset().mockResolvedValue({ get: () => ({ value: "private-cookie" }) });
  fixture.read.mockReset().mockResolvedValue({ accessToken: "private-access", companyId: company });
  fetcher.mockReset().mockImplementation(async () => Response.json({ accepted: true }));
  vi.stubGlobal("fetch", fetcher);
  vi.stubEnv("AIOFFICE_LOCAL_CORE_API_URL", "http://core-api:8080");
});
afterEach(() => { vi.useRealTimers(); vi.unstubAllGlobals(); vi.unstubAllEnvs(); });

describe.each(["local", "browser"])("%s mutation ingress", mode => {
  beforeEach(() => {
    vi.stubEnv("AIOFFICE_LOCAL_UI_ENABLED", mode === "local" ? "true" : "false");
    vi.stubEnv("AIOFFICE_BROWSER_OIDC_ENABLED", mode === "browser" ? "true" : "false");
  });
  describe.each(cases)("$name", ({ method, maxBytes, value, invoke }) => {
    it("preserves supported maximum strings even when fully JSON-escaped", async () => {
      const escaped = JSON.stringify(value).replaceAll("\u0800", "\\u0800");
      const response = await invoke(request(method, escaped));
      expect(response.status).toBe(200);
      expect(JSON.parse(fetcher.mock.calls[0][1].body)).toEqual(value);
      expect(response.headers.get("set-cookie")).toBeNull();
    });
    it("denies actual oversized chunked input before cookie or upstream access despite false Content-Length", async () => {
      let chunks = 0;
      const cancel = vi.fn(() => new Promise<void>(() => {}));
      const stream = new ReadableStream<Uint8Array>({
        pull(controller) { chunks++; controller.enqueue(new Uint8Array(maxBytes / 2 + 1).fill(32)); }, cancel,
      }, { highWaterMark: 0 });
      const response = await invoke(request(method, stream, {
        headers: { Origin: origin, "Content-Type": "application/json", "Content-Length": "2" },
      }));
      expect(response.status).toBe(413); expect(chunks).toBe(2); expect(cancel).toHaveBeenCalledOnce();
      expect(stream.locked).toBe(false);
      expect(fixture.cookies).not.toHaveBeenCalled(); expect(fetcher).not.toHaveBeenCalled();
    });
    it("denies invalid UTF8 instead of accepting replacement characters", async () => {
      const valid = new TextEncoder().encode(JSON.stringify(value));
      const bytes = new Uint8Array(valid.byteLength + 1);
      bytes.set(valid); bytes[valid.byteLength] = 0xff;
      expect((await invoke(request(method, bytes))).status).toBe(400);
      expect(fixture.cookies).not.toHaveBeenCalled(); expect(fetcher).not.toHaveBeenCalled();
    });
    it("checks Origin before reading even a stalled body", async () => {
      const pull = vi.fn();
      const stream = new ReadableStream<Uint8Array>({ pull }, { highWaterMark: 0 });
      const response = await invoke(request(method, stream, {
        headers: { Origin: "https://foreign.invalid", "Content-Type": "application/json" },
      }));
      expect(response.status).toBe(403); expect(pull).not.toHaveBeenCalled();
      expect(fixture.cookies).not.toHaveBeenCalled(); expect(fetcher).not.toHaveBeenCalled();
      await stream.cancel();
    });
    it("cancels a stalled read on its body deadline without awaiting hostile cleanup", async () => {
      vi.useFakeTimers();
      const cancel = vi.fn(() => new Promise<void>(() => {}));
      const stream = new ReadableStream<Uint8Array>({ cancel }, { highWaterMark: 0 });
      const response = invoke(request(method, stream));
      await vi.advanceTimersByTimeAsync(MUTATION_BODY_TIMEOUT_MS);
      expect((await response).status).toBe(408); expect(cancel).toHaveBeenCalledOnce();
      expect(stream.locked).toBe(false);
      expect(fixture.cookies).not.toHaveBeenCalled(); expect(fetcher).not.toHaveBeenCalled();
    });
    it("cancels aborted input before authority access", async () => {
      const signal = new AbortController();
      const cancel = vi.fn();
      const stream = new ReadableStream<Uint8Array>({ cancel }, { highWaterMark: 0 });
      const response = invoke(request(method, stream, { signal: signal.signal }));
      await Promise.resolve(); signal.abort();
      expect((await response).status).toBe(400); expect(cancel).toHaveBeenCalledOnce();
      expect(fixture.cookies).not.toHaveBeenCalled(); expect(fetcher).not.toHaveBeenCalled();
    });
    it("requires JSON media before consuming input", async () => {
      const pull = vi.fn();
      const stream = new ReadableStream<Uint8Array>({ pull }, { highWaterMark: 0 });
      expect((await invoke(request(method, stream, { headers: { Origin: origin, "Content-Type": "text/plain" } }))).status).toBe(400);
      expect(pull).not.toHaveBeenCalled(); expect(fixture.cookies).not.toHaveBeenCalled();
      await stream.cancel();
    });
  });
});

it("accepts the exact actual-byte limit and refuses the next byte", async () => {
  for (const length of [8192, 8193]) {
    const body = JSON.stringify({ x: "a".repeat(length - 8) });
    expect(new TextEncoder().encode(body).byteLength).toBe(length);
    const result = await readBoundedRequestJson(request("POST", body), 8192);
    expect(result.ok).toBe(length === 8192);
    if (!result.ok) expect(result.status).toBe(413);
  }
});
it("refuses an already consumed or locked body generically", async () => {
  const consumed = request("POST", "{}"); await consumed.text();
  expect(await readBoundedRequestJson(consumed, 8192)).toEqual({ ok: false, status: 400 });
  const locked = request("POST", "{}"); const reader = locked.body!.getReader();
  expect(await readBoundedRequestJson(locked, 8192)).toEqual({ ok: false, status: 400 });
  reader.releaseLock();
});
