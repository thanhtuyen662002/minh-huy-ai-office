// @vitest-environment node
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createServer } from "node:http";
import { createHash } from "node:crypto";
const mocks = vi.hoisted(() => ({ runtime: vi.fn(), cookies: vi.fn(), read: vi.fn(), fetch: vi.fn() }));
vi.mock("./browser-auth-runtime", () => ({ getBrowserAuthRuntime: mocks.runtime }));
vi.mock("next/headers", () => ({ cookies: mocks.cookies }));
import { GET as list, POST as prepare } from "../app/api/local/tasks/intents/route";
import { GET as detail } from "../app/api/local/tasks/intents/[operationId]/route";
import { POST as submit } from "../app/api/local/tasks/intents/[operationId]/submit/route";
import { submissionFingerprint } from "./task-submission-intent";

const company = "22222222-2222-4222-8222-222222222222", operation = "11111111-1111-4111-8111-111111111111";
const networkFetch = globalThis.fetch;
const source = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
const input = { operationId: operation, dataSourceId: source, question: "Tồn kho 😀 �" };
const fingerprint = "6354CD8F9D07F489B9F1B213CE89B4FE5EF4016DFC44329AE740B0C39BDE0656";
const intent = { companyId: company, ...input, state: 0, inputFingerprint: fingerprint, createdAtUtc: "2026-10-09T08:00:00Z", expiresAtUtc: "2026-10-10T08:00:00Z", accepted: null };
const receipt = { companyId: company, operationId: operation, dataSourceId: source, inputFingerprint: fingerprint,
  taskId: "33333333-3333-4333-8333-333333333333", stepId: "44444444-4444-4444-8444-444444444444",
  messageId: "55555555-5555-4555-8555-555555555555", status: 0, dispatchState: 0, createdAtUtc: "2026-10-09T08:00:00Z" };
const session = { companyId: company, accessToken: "PRIVATE-TOKEN", subject: "PRIVATE-SUBJECT", expiresAt: Math.floor(Date.now() / 1000) + 300 };
const page = { companyId: company, items: [intent], offset: 0, limit: 25, hasMore: false };
type Mode = "prepare" | "submit" | "list" | "detail";
function request(mode: Mode, body: unknown = mode === "prepare" ? input : { inputFingerprint: fingerprint }, query = `companyId=${company}`, origin = "https://office.example.test") {
  return new Request(`http://internal-web:3000/api/local/tasks/intents?${query}`, { ...(mode === "prepare" || mode === "submit"
    ? { method: "POST", body: typeof body === "string" ? body : JSON.stringify(body) } : {}),
    headers: { Host: "office.example.test", Origin: origin, "Content-Type": "application/json", Authorization: "Bearer FORGED", "X-AIOffice-Company-Id": "FORGED" } });
}
function invoke(mode: Mode, req = request(mode), operationId = operation) {
  const context = { params: Promise.resolve({ operationId }) };
  return mode === "prepare" ? prepare(req) : mode === "submit" ? submit(req, context) : mode === "list" ? list(req) : detail(req, context);
}
beforeEach(() => {
  vi.clearAllMocks(); vi.stubEnv("AIOFFICE_BROWSER_OIDC_ENABLED", "true"); vi.stubEnv("AIOFFICE_LOCAL_UI_ENABLED", "false");
  mocks.runtime.mockReturnValue({ settings: { localHttp: false, publicOrigin: "https://office.example.test" }, coreApiOrigin: "http://core.fixture.invalid", sessions: { read: mocks.read } });
  mocks.cookies.mockResolvedValue({ get: (name: string) => ({ value: name.includes("binding") ? "PRIVATE-BINDING" : "PRIVATE-SID" }) });
  mocks.read.mockResolvedValue(session);
  mocks.fetch.mockImplementation(async (url: string, init: RequestInit) => Response.json(url.includes("/submit") ? receipt
    : url.includes("?offset=") ? page : intent, { status: url.includes("/submit") ? 202 : 200, headers: { "Set-Cookie": "PRIVATE=discard", "X-PRIVATE": "discard" } }));
  vi.stubGlobal("fetch", mocks.fetch);
});
afterEach(() => { vi.unstubAllGlobals(); vi.unstubAllEnvs(); vi.useRealTimers(); });

it.each(["\uFEFFTồn kho", "Tồn kho\uFEFF", "\uFEFF"])("real HTTP detail/list preserve Core-valid scalar FEFF and reject changed bytes: %j", async question => {
  const bytes = Buffer.from(question, "utf8"), sizes = Buffer.alloc(8);
  sizes.writeUInt32LE(3); sizes.writeUInt32LE(bytes.length, 4);
  const inputFingerprint = createHash("sha256").update("aioffice-task-intent-v1\0" + source.replaceAll("-", ""))
    .update(sizes).update(bytes).digest("hex").toUpperCase();
  let storedQuestion = question;
  const server = createServer((req, res) => {
    const stored = { ...intent, question: storedQuestion, inputFingerprint };
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end(JSON.stringify(req.url?.includes("?offset=") ? { ...page, items: [stored] } : stored));
  });
  await new Promise<void>(resolve => server.listen(0, "127.0.0.1", resolve));
  try {
    const address = server.address(); if (!address || typeof address === "string") throw new Error("Missing bound transport");
    mocks.runtime.mockReturnValue({ settings: { localHttp: false, publicOrigin: "https://office.example.test" },
      coreApiOrigin: `http://127.0.0.1:${address.port}`, sessions: { read: mocks.read } });
    mocks.fetch.mockImplementation(networkFetch);
    for (const mode of ["detail", "list"] as const) {
      const response = await invoke(mode); expect(response.status).toBe(200); expect(response.headers.get("Cache-Control")).toBe("no-store");
      const body = await response.json(); expect(mode === "detail" ? body.question : body.items[0].question).toBe(question);
      expect(mode === "detail" ? body.inputFingerprint : body.items[0].inputFingerprint).toBe(inputFingerprint);
    }
    storedQuestion = "different";
    expect((await invoke("detail")).status).toBe(503); expect((await invoke("list")).status).toBe(503);
  } finally { server.closeAllConnections(); await new Promise<void>(resolve => server.close(() => resolve())); }
});

it.each(["prepare", "submit", "list", "detail"] as const)("uses real issued-session helper and final private release fence for %s", async mode => {
  const response = await invoke(mode); expect(response.status).toBe(mode === "submit" ? 202 : 200);
  expect(response.headers.get("Cache-Control")).toBe("no-store"); expect(response.headers.get("Set-Cookie")).toBeNull(); expect(response.headers.get("X-PRIVATE")).toBeNull();
  expect(mocks.fetch).toHaveBeenCalledTimes(mode === "submit" ? 2 : 1); expect(mocks.read).toHaveBeenCalledTimes(mode === "submit" ? 5 : 3);
  for (const [url, init] of mocks.fetch.mock.calls) {
    expect(url).toMatch(/^http:\/\/core.fixture.invalid\/api\/tasks\/intents/);
    expect(init.headers.get("Authorization")).toBe("Bearer PRIVATE-TOKEN"); expect(init.headers.get("X-AIOffice-Company-Id")).toBe(company);
    expect(init.cache).toBe("no-store"); expect(init.redirect).toBe("error"); expect(init.signal).toBeInstanceOf(AbortSignal);
    expect(init.headers.get("Idempotency-Key")).toBeNull();
  }
  if (mode === "submit") {
    expect(mocks.fetch.mock.calls[0][1].method ?? "GET").toBe("GET");
    expect(mocks.fetch.mock.calls[1][0]).toBe(`http://core.fixture.invalid/api/tasks/intents/${operation}/submit`);
    expect(JSON.parse(mocks.fetch.mock.calls[1][1].body)).toEqual({ inputFingerprint: fingerprint });
  } else if (mode === "prepare") expect(JSON.parse(mocks.fetch.mock.calls[0][1].body)).toEqual(input);
});
it.each(["prepare", "submit"] as const)("checks Origin before consuming %s body or reading authority", async mode => {
  const req = request(mode, input, `companyId=${company}`, "https://hostile.example"); const read = vi.spyOn(req.body!, "getReader");
  const response = await invoke(mode, req); expect(response.status).toBe(403); expect(read).not.toHaveBeenCalled(); expect(mocks.read).not.toHaveBeenCalled();
});
it.each(["companyId=bad", `companyId=${company}&companyId=${company}`, `companyId=${company}&tenantId=FORGED`,
  `companyId=${company}&ownerId=FORGED`, `companyId=${company}&offset=0`])("rejects mutation selector %s before reading body", async query => {
  const req = request("prepare", input, query); const read = vi.spyOn(req.body!, "getReader");
  expect((await invoke("prepare", req)).status).toBe(400); expect(read).not.toHaveBeenCalled(); expect(mocks.fetch).not.toHaveBeenCalled();
});
it.each(["offset=-1", "offset=01", "offset=10001", "limit=26", "limit=0", "limit=01", "limit=1&limit=2", "ownerId=FORGED"])("rejects list pagination %s", async suffix => {
  expect((await invoke("list", request("list", null, `companyId=${company}&${suffix}`))).status).toBe(400); expect(mocks.read).not.toHaveBeenCalled();
});
it.each(["bad", "11111111111141118111111111111111", "00000000-0000-0000-0000-000000000000"])("rejects invalid operation %s", async operationId => {
  expect((await invoke("submit", request("submit"), operationId)).status).toBe(400); expect((await invoke("detail", request("detail"), operationId)).status).toBe(400);
  expect(mocks.read).not.toHaveBeenCalled();
});
it.each([null, {}, { ...input, question: "\ud800" }, { ...input, question: "a\n" }, { ...input, question: " x" }, { ...input, maxAttempts: 3 },
  { ...input, dataSourceId: "------------------------------------" }, `{"operationId":"${operation}","\\u006fperationId":"${operation}","dataSourceId":"${source}","question":"test"}`])("rejects malformed prepare input %j without Core", async body => {
  expect((await invoke("prepare", request("prepare", body))).status).toBe(400); expect(mocks.fetch).not.toHaveBeenCalled();
});
it.each([{}, { inputFingerprint: fingerprint.toLowerCase() }, { inputFingerprint: fingerprint, question: "replacement" },
  { inputFingerprint: fingerprint, dataSourceId: source }, `{"inputFingerprint":"${fingerprint}","inputFingerprint":"${fingerprint}"}`])("rejects replacement/ambiguous execute input %j", async body => {
  expect((await invoke("submit", request("submit", body))).status).toBe(400); expect(mocks.fetch).not.toHaveBeenCalled();
});
it("preserves the maximum fully escaped scalar body and bounds actual overflow without trusting length", async () => {
  const large = { ...input, question: "😀".repeat(2000) }; const hash = await submissionFingerprint(large);
  mocks.fetch.mockResolvedValue(Response.json({ ...intent, ...large, inputFingerprint: hash }));
  expect((await invoke("prepare", request("prepare", large))).status).toBe(200);
  mocks.fetch.mockClear();
  expect((await invoke("prepare", request("prepare", " ".repeat(32769)))).status).toBe(413); expect(mocks.fetch).not.toHaveBeenCalled();
});
it("bounds stalled/aborted request reads and cancels without reading authority", async () => {
  vi.useFakeTimers(); const cancel = vi.fn();
  const req = new Request(`http://web/api/local/tasks/intents?companyId=${company}`, { method: "POST", body: new ReadableStream({ cancel }),
    headers: { Host: "office.example.test", Origin: "https://office.example.test", "Content-Type": "application/json" }, duplex: "half" } as RequestInit);
  const pending = invoke("prepare", req); await vi.advanceTimersByTimeAsync(10001);
  expect((await pending).status).toBe(408); expect(cancel).toHaveBeenCalled(); expect(mocks.read).not.toHaveBeenCalled();
});
it("binds submit to stored exact input before any execution and rejects changed fingerprint", async () => {
  expect((await invoke("submit", request("submit", { inputFingerprint: "A".repeat(64) }))).status).toBe(409);
  expect(mocks.fetch).toHaveBeenCalledTimes(1); expect(mocks.fetch.mock.calls[0][1].method ?? "GET").toBe("GET");
  mocks.fetch.mockReset().mockResolvedValue(Response.json({ ...intent, question: "PRIVATE_TAMPER" }));
  const response = await invoke("submit"); expect(response.status).toBe(503); expect(mocks.fetch).toHaveBeenCalledTimes(1); expect(await response.text()).not.toContain("PRIVATE");
});
it.each(["companyId", "operationId", "dataSourceId", "inputFingerprint"] as const)("rejects unbound accepted receipt %s", async field => {
  mocks.fetch.mockImplementation(async (url: string) => Response.json(url.includes("/submit") ? { ...receipt, [field]: field === "inputFingerprint" ? "A".repeat(64) : "66666666-6666-4666-8666-666666666666" } : intent,
    { status: url.includes("/submit") ? 202 : 200 }));
  expect((await invoke("submit")).status).toBe(503);
});
it("requires the same committed identities on replay and allows mutable worker state to advance", async () => {
  mocks.fetch.mockImplementation(async (url: string) => Response.json(url.includes("/submit") ? { ...receipt, status: 6, dispatchState: 2 }
    : { ...intent, state: 1, accepted: receipt }, { status: url.includes("/submit") ? 202 : 200 }));
  expect((await invoke("submit")).status).toBe(202);
  for (const field of ["taskId", "stepId", "messageId"] as const) {
    mocks.fetch.mockImplementation(async (url: string) => Response.json(url.includes("/submit") ? { ...receipt, [field]: company }
      : { ...intent, state: 1, accepted: receipt }, { status: url.includes("/submit") ? 202 : 200 }));
    expect((await invoke("submit")).status).toBe(503);
  }
});
it.each(["prepare", "submit"] as const)("keeps real transport uncertainty503 and the same operation after lost %s reply", async mode => {
  mocks.fetch.mockImplementation(async (url: string) => {
    if (mode === "prepare" || url.includes("/submit")) throw new Error("PRIVATE_CORE_TRANSPORT");
    return Response.json(intent);
  });
  const first = await invoke(mode); expect(first.status).toBe(503); expect(await first.text()).not.toContain("PRIVATE");
  const prior = mocks.fetch.mock.calls.at(-1)![1].body;
  mocks.fetch.mockImplementation(async (url: string) => Response.json(url.includes("/submit") ? receipt : intent, { status: url.includes("/submit") ? 202 : 200 }));
  expect((await invoke(mode)).status).toBe(mode === "submit" ? 202 : 200);
  expect(mocks.fetch.mock.calls.at(-1)![1].body).toBe(prior);
});
it.each(["prepare", "submit", "detail", "list"] as const)("denies missing/replaced/revoked final SID for %s", async mode => {
  for (const final of [null, { ...session, companyId: operation }]) {
    mocks.read.mockReset(); for (let index = 0; index < (mode === "submit" ? 4 : 2); index++) mocks.read.mockResolvedValueOnce(session);
    mocks.read.mockResolvedValue(final);
    const response = await invoke(mode); expect(response.status).toBe(401); expect(await response.text()).not.toContain("Tồn kho");
  }
});
it("does not POST after the issued SID is revoked between owner lookup and explicit submit", async () => {
  mocks.read.mockReset().mockResolvedValueOnce(session).mockResolvedValueOnce(session).mockResolvedValue(null);
  const response = await invoke("submit"); expect(response.status).toBe(401); expect(mocks.fetch).toHaveBeenCalledTimes(1);
});
it("drops late private bodies after logout and never relays provider diagnostics", async () => {
  let finish!: ReadableStreamDefaultController<Uint8Array>;
  mocks.fetch.mockResolvedValue(new Response(new ReadableStream({ start(controller) { finish = controller; } }), { headers: { "Content-Type": "application/json" } }));
  const pending = invoke("detail"); await vi.waitFor(() => expect(mocks.fetch).toHaveBeenCalledTimes(1));
  mocks.read.mockResolvedValue(null); finish.enqueue(new TextEncoder().encode(JSON.stringify(intent))); finish.close();
  const denied = await pending; expect(denied.status).toBe(401); expect(await denied.text()).not.toContain("Tồn kho");
  mocks.read.mockResolvedValue(session); mocks.fetch.mockResolvedValue(Response.json({ error: "PRIVATE_PROVIDER" }, { status: 500 }));
  const failed = await invoke("prepare"); expect(failed.status).toBe(503); expect(await failed.text()).not.toContain("PRIVATE");
});
it("rejects duplicate nested receipt fields and malformed UTF8 without releasing private payload", async () => {
  const accepted = JSON.stringify({ ...intent, state: 1, accepted: receipt }).replace('"taskId":', `"taskId":"${receipt.taskId}","taskId":`);
  mocks.fetch.mockResolvedValue(new Response(accepted, { headers: { "Content-Type": "application/json" } }));
  expect((await invoke("detail")).status).toBe(503);
  mocks.fetch.mockResolvedValue(new Response(new Uint8Array([123, 34, 255, 34, 58, 49, 125]), { headers: { "Content-Type": "application/json" } }));
  expect((await invoke("list")).status).toBe(503);
});
it.each(["intent-limit", "intent-expired", "operation-conflict", "intent-unavailable", "PRIVATE_UNRECOGNIZED"])("returns only allowlisted bounded conflict code %s", async code => {
  mocks.fetch.mockResolvedValue(Response.json({ error: "PRIVATE_DIAGNOSTIC", code }, { status: 409 }));
  const response = await invoke("prepare"); expect(response.status).toBe(409); expect(response.headers.get("Cache-Control")).toBe("no-store");
  const body = await response.json(); expect(body.code).toBe(code.startsWith("PRIVATE") ? undefined : code); expect(JSON.stringify(body)).not.toContain("PRIVATE");
});

it.each(["headers", "body"] as const)("real Node HTTP202 transport loss at %s retains the exact deliberate operation", async boundary => {
  // Transport control only. Mandatory actual Core/SQL/worker acceptance is
  // covered separately by the owned disposable stack, never by this server.
  let posts = 0, effects = 0; const bodies: unknown[] = [];
  const server = createServer((req, res) => {
    if (req.method === "GET") { res.writeHead(200, { "Content-Type": "application/json" }); res.end(JSON.stringify(effects ? { ...intent, state: 1, accepted: receipt } : intent)); return; }
    let body = ""; req.on("data", chunk => { body += chunk; }); req.on("end", () => {
      posts++; bodies.push(JSON.parse(body)); effects = 1;
      const text = JSON.stringify(receipt);
      if (posts === 1) {
        if (boundary === "headers") { req.socket.destroy(); return; }
        res.writeHead(202, { "Content-Type": "application/json", "Content-Length": Buffer.byteLength(text) }); res.write(text.slice(0, 1));
        setTimeout(() => res.destroy(), 30); return;
      }
      res.writeHead(202, { "Content-Type": "application/json" }); res.end(text);
    });
  });
  await new Promise<void>(resolve => server.listen(0, "127.0.0.1", resolve));
  try {
    const address = server.address(); if (!address || typeof address === "string") throw new Error("Missing bound transport");
    mocks.runtime.mockReturnValue({ settings: { localHttp: false, publicOrigin: "https://office.example.test" }, coreApiOrigin: `http://127.0.0.1:${address.port}`, sessions: { read: mocks.read } });
    mocks.fetch.mockImplementation(networkFetch);
    const unknown = await invoke("submit"); expect(unknown.status).toBe(503); expect(unknown.headers.get("Cache-Control")).toBe("no-store"); expect(effects).toBe(1);
    expect((await invoke("submit")).status).toBe(202); expect(posts).toBe(2); expect(effects).toBe(1);
    expect(bodies).toEqual([{ inputFingerprint: fingerprint }, { inputFingerprint: fingerprint }]);
    const calls = mocks.fetch.mock.calls.filter(([url]) => String(url).endsWith("/submit"));
    expect(calls).toHaveLength(2); expect(calls[0][0]).toBe(calls[1][0]);
    for (const call of mocks.read.mock.calls) expect(call).toEqual(["PRIVATE-BINDING", "PRIVATE-SID"]);
  } finally { server.closeAllConnections(); await new Promise<void>(resolve => server.close(() => resolve())); }
});
