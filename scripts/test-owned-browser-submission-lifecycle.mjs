import assert from "node:assert/strict";
import test from "node:test";
import { runInNewContext } from "node:vm";
import { observeSubmissionLifecycle, submissionAbortStage, observeSubmissionWire, submissionWireStage } from "./owned-browser-submission-lifecycle.mjs";
import { EventEmitter } from "node:events";
import { resolve, join } from "node:path";
import { verifyTaskSubmission } from "./smoke-browser-task-submission.mjs";

const origin = "http://127.0.0.1:3000", company = "22222222-2222-4222-8222-222222222222";
const path = `/api/local/tasks/intents/11111111-1111-4111-8111-111111111111/submit?companyId=${company}`;
function fixture(fetch) {
  let now = 0;
  const window = { location: { origin }, fetch };
  runInNewContext(`(${observeSubmissionLifecycle.toString()})({origin,company})`, {
    window, origin, company, URL, AbortSignal, DOMException, performance: { now: () => now },
  });
  return { window, setTime(value) { now = value; }, read: () => window.__aiofficeOwnedSubmissionLifecycle.read(origin + path),
    dispose: () => window.__aiofficeOwnedSubmissionLifecycle.dispose() };
}
test("owned observer forwards exact request and response and records original timeout without cancellation", async () => {
  const response = { privateBody: "NEVER_READ" }, controller = new AbortController();
  const init = { method: "POST", signal: controller.signal, body: "NEVER_READ" };
  const original = function (...args) { assert.equal(this, value.window); assert.equal(args[0], path); assert.equal(args[1], init); value.setTime(9200); return Promise.resolve(response); };
  const value = fixture(original);
  assert.equal(await value.window.fetch(path, init), response);
  assert.equal(controller.signal.aborted, false); value.setTime(10000); controller.abort(new DOMException("PRIVATE", "TimeoutError"));
  assert.equal(submissionAbortStage(value.read()), "replay-timeout-near-deadline-headers");
  value.dispose(); assert.equal(value.window.fetch, original); assert.equal(value.window.__aiofficeOwnedSubmissionLifecycle, undefined);
});
test("owner abort after headers is distinguished; disposed listener no longer changes evidence", async () => {
  const controller = new AbortController(), response = {}; const value = fixture(async () => response);
  assert.equal(await value.window.fetch(path, { method: "POST", signal: controller.signal }), response);
  value.setTime(30); controller.abort(); assert.equal(submissionAbortStage(value.read()), "replay-owner-abort-after-headers");
  assert.equal(value.read().headersNearDeadline, false); value.dispose();
});
test("observation preserves original rejection and ignores foreign paths, companies and non POST requests", async () => {
  const failure = new Error("PRIVATE"); const value = fixture(() => Promise.reject(failure)), controller = new AbortController();
  for (const url of ["https://foreign.invalid" + path, "/api/local/session", path.replace(company, "33333333-3333-4333-8333-333333333333")])
    await assert.rejects(value.window.fetch(url, { method: "POST", signal: controller.signal }), error => error === failure);
  await assert.rejects(value.window.fetch(path, { method: "GET", signal: controller.signal }), error => error === failure);
  assert.equal(value.read(), null);
  await assert.rejects(value.window.fetch(path, { method: "POST", signal: controller.signal }), error => error === failure);
  controller.abort(); assert.equal(submissionAbortStage(value.read()), "replay-request-aborted"); value.dispose();
});
test("observer has a bounded lifetime and never replaces a later fetch wrapper", async () => {
  const value = fixture(async () => ({}));
  const controllers = Array.from({ length: 9 }, () => new AbortController());
  for (const controller of controllers.slice(0, 8)) await value.window.fetch(path, { method: "POST", signal: controller.signal });
  controllers[7].abort(); assert.equal(value.read().kind, "owner");
  await value.window.fetch(path, { method: "POST", signal: controllers[8].signal });
  controllers[8].abort(); assert.equal(value.read(), null);
  const wrapper = () => {}; value.window.fetch = wrapper; value.dispose();
  assert.equal(value.window.fetch, wrapper);
  const second = fixture(async () => ({})), controller = new AbortController();
  await second.window.fetch(path, { method: "POST", signal: controller.signal });
  const project = second.window.__aiofficeOwnedSubmissionLifecycle.read;
  second.dispose(); controller.abort(); assert.equal(project(origin + path).kind, "absent");
});

test("exhausted fetch observation forwards synchronous failure once and never returns old evidence", async () => {
  const failure = new Error("PRIVATE"), controller = new AbortController(); let calls = 0;
  const value = fixture(() => { calls++; if (calls === 9) throw failure; return Promise.resolve({}); });
  for (let index = 0; index < 8; index++) await value.window.fetch(path, { method: "POST", signal: controller.signal });
  controller.abort(); assert.equal(value.read().kind, "owner");
  assert.throws(() => value.window.fetch(path, { method: "POST", signal: new AbortController().signal }), error => error === failure);
  assert.equal(calls, 9); assert.equal(value.read(), null); value.dispose();
});
test("observation refuses a nonowned origin or duplicate installation before replacing fetch", () => {
  const fetch = () => {}; const window = { location: { origin: "https://foreign.invalid" }, fetch };
  assert.throws(() => runInNewContext(`(${observeSubmissionLifecycle.toString()})({origin,company})`, { window, origin, company }), /Owned browser observation refused/);
  assert.equal(window.fetch, fetch);
  const value = fixture(async () => ({}));
  assert.throws(() => runInNewContext(`(${observeSubmissionLifecycle.toString()})({origin,company})`, { window: value.window, origin, company }), /Owned browser observation refused/);
  value.dispose();
});
test("only fixed scalar categories may reach the refusal stage", () => {
  const evidence = { kind: "timeout", headersBeforeAbort: true, headersNearDeadline: false };
  assert.equal(submissionAbortStage(evidence), "replay-timeout-after-headers");
  for (const value of [null, "PRIVATE", {}, { ...evidence, extra: "PRIVATE" }, { ...evidence, kind: "PRIVATE" },
    { ...evidence, headersBeforeAbort: false }, { ...evidence, headersNearDeadline: "PRIVATE" },
    Object.defineProperty({ ...evidence }, "kind", { get() { throw new Error("PRIVATE"); } })])
    assert.equal(submissionAbortStage(value), "replay-request-aborted");
});

async function wireFixture(action) {
  const saved = { CI: process.env.CI, GITHUB_ACTIONS: process.env.GITHUB_ACTIONS, RUNNER_TEMP: process.env.RUNNER_TEMP };
  const directory = join(resolve("owned-inert-wire"), "aioffice-local");
  Object.assign(process.env, { CI: "true", GITHUB_ACTIONS: "true", RUNNER_TEMP: resolve("owned-inert-wire") });
  const session = new EventEmitter(), calls = [], page = {};
  session.send = async command => { calls.push(command); };
  session.detach = async () => { calls.push("detach"); };
  const context = { newCDPSession: async target => { assert.equal(target, page); calls.push("session"); return session; } };
  try { await action({ context, page, directory, session, calls }); }
  finally { for (const [key, value] of Object.entries(saved)) { if (value === undefined) delete process.env[key]; else process.env[key] = value; } }
}

test("owned wire observer collects only fixed completion metadata and removes every listener", async () => {
  await wireFixture(async ({ context, page, directory, session, calls }) => {
    const observer = await observeSubmissionWire(context, page, { directory, origin, company });
    const request = { url: origin + path, method: "POST" };
    Object.defineProperty(request, "postData", { get() { assert.fail("Private request body accessed"); } });
    session.emit("Network.requestWillBeSent", { requestId: "owned", request });
    const headers = { "Content-Encoding": "gzip" };
    Object.defineProperty(headers, "authorization", { enumerable: true, get() { assert.fail("Private header accessed"); } });
    session.emit("Network.responseReceived", { requestId: "owned", response: { headers } });
    session.emit("Network.dataReceived", { requestId: "owned", dataLength: 10 });
    assert.deepEqual(observer.read(origin + path), { bodyStarted: true, wireFinished: false, compressed: true });
    session.emit("Network.loadingFinished", { requestId: "owned" });
    assert.deepEqual(observer.read(origin + path), { bodyStarted: true, wireFinished: true, compressed: true });
    await observer.dispose(); assert.equal(session.eventNames().length, 0);
    assert.deepEqual(calls, ["session", "Network.enable", "detach"]);
  });
});

test("wire observer ignores foreign scopes and has bounded request lifetime", async () => {
  await wireFixture(async ({ context, page, directory, session }) => {
    const observer = await observeSubmissionWire(context, page, { directory, origin, company });
    for (const url of ["https://foreign.invalid" + path, origin + "/api/local/session", origin + path.replace(company, "33333333-3333-4333-8333-333333333333")])
      session.emit("Network.requestWillBeSent", { requestId: "foreign", request: { url, method: "POST" } });
    session.emit("Network.requestWillBeSent", { requestId: "get", request: { url: origin + path, method: "GET" } });
    assert.equal(observer.read(origin + path), null);
    for (let index = 0; index < 8; index++) session.emit("Network.requestWillBeSent", { requestId: String(index), request: { url: origin + path, method: "POST" } });
    session.emit("Network.dataReceived", { requestId: "7", dataLength: NaN });
    assert.deepEqual(observer.read(origin + path), { bodyStarted: false, wireFinished: false, compressed: false });
    session.emit("Network.dataReceived", { requestId: "7", dataLength: 1 });
    assert.equal(observer.read(origin + path).bodyStarted, true);
    session.emit("Network.requestWillBeSent", { requestId: "8", request: { url: origin + path, method: "POST" } });
    session.emit("Network.loadingFinished", { requestId: "7" });
    session.emit("Network.loadingFinished", { requestId: "8" });
    assert.equal(observer.read(origin + path), null);
    await observer.dispose();
  });
});

test("wire observation refuses unowned configuration before opening a protocol session", async () => {
  await wireFixture(async ({ context, page, directory, calls }) => {
    for (const override of [{ directory: directory + "/nested" }, { origin: "https://foreign.invalid" }, { company: "PRIVATE" }, { company: "00000000-0000-0000-0000-000000000000" }])
      await assert.rejects(observeSubmissionWire(context, page, { directory, origin, company, ...override }), /Owned browser wire observation refused/);
    process.env.CI = "false";
    await assert.rejects(observeSubmissionWire(context, page, { directory, origin, company }), /Owned browser wire observation refused/);
    assert.deepEqual(calls, []);
  });
});

test("failed wire observation setup removes listeners and retains the original failure", async () => {
  await wireFixture(async ({ context, page, directory, session }) => {
    const failure = new Error("PRIVATE"); session.send = async () => { throw failure; };
    session.detach = async () => { throw new Error("SECONDARY_PRIVATE"); };
    await assert.rejects(observeSubmissionWire(context, page, { directory, origin, company }), error => error === failure);
    assert.equal(session.eventNames().length, 0);
  });
});

test("wire metadata and accepted UI only refine fixed refusal categories", () => {
  const stage = "replay-timeout-after-headers", empty = { bodyStarted: false, wireFinished: false, compressed: false, acceptedUi: false };
  assert.equal(submissionWireStage(stage, empty), "replay-timeout-no-body-observed");
  assert.equal(submissionWireStage(stage, { ...empty, bodyStarted: true }), "replay-timeout-body-started");
  assert.equal(submissionWireStage(stage, { ...empty, bodyStarted: true, compressed: true }), "replay-timeout-compressed-body-started");
  assert.equal(submissionWireStage(stage, { ...empty, wireFinished: true }), "replay-timeout-after-wire-finished");
  assert.equal(submissionWireStage(stage, { ...empty, acceptedUi: true }), "replay-timeout-after-accepted-ui");
  for (const value of [null, "PRIVATE", {}, { ...empty, extra: "PRIVATE" }, { ...empty, bodyStarted: "PRIVATE" },
    Object.defineProperty({ ...empty }, "bodyStarted", { get() { assert.fail("Getter accessed"); } })])
    assert.equal(submissionWireStage(stage, value), stage);
  assert.equal(submissionWireStage("replay-owner-abort-after-headers", { ...empty, acceptedUi: true }), "replay-owner-abort-after-headers");
});

// Exercise the actual nested reader AND historical caller, without copying their
// implementations. Inert browser/SQL callbacks cannot establish native acceptance.
function restoredReceiptFixture({ status = 202, bytes, finished = null, bodyFailure, stalled = false, stalledBody = false, graphFailure = false } = {}) {
  const operationId = "11111111-1111-4111-8111-111111111111";
  const receipt = { companyId: company, operationId, inputFingerprint: "A".repeat(64),
    taskId: "33333333-3333-4333-8333-333333333333", stepId: "44444444-4444-4444-8444-444444444444",
    messageId: "55555555-5555-4555-8555-555555555555", createdAtUtc: "2026-10-10T00:00:00Z" };
  const phases = [], calls = { click: 0, body: 0, json: 0, worker: 0, graph: 0, idle: 0, wait: 0 };
  const source = verifyTaskSubmission.toString();
  const section = (start, end) => {
    const first = source.indexOf(start), last = source.indexOf(end, first);
    assert.ok(first >= 0 && last > first); return source.slice(first, last);
  };
  const proof = condition => { if (!condition) throw new Error("Task submission browser proof failed."); };
  const response = { status: () => status, url: () => origin + path,
    request: () => ({ method: () => "POST", failure: () => null }),
    finished: () => stalled ? new Promise(() => {}) : Promise.resolve(finished),
    body: async () => { calls.body++; if (bodyFailure) throw bodyFailure;
      if (stalledBody) return new Promise(() => {}); return bytes ?? Buffer.from(JSON.stringify(receipt)); },
    json: async () => { calls.json++; throw new Error("PRIVATE original unbounded JSON capture must not run."); } };
  const context = { proof, TextDecoder, JSON, URL, submissionAbortStage, submissionWireStage,
    page: { waitForResponse: (predicate, options) => {
      calls.wait++; assert.equal(options.timeout, 20_000); assert.equal(predicate(response), true); return Promise.resolve(response);
    }, getByRole: (role, options) => { assert.equal(role, "button"); assert.equal(options.name, "Thử lại đúng yêu cầu");
      assert.equal(options.exact, true); return { click: async () => { calls.click++; } }; } },
    wireObservation: null, console: { log: () => {} }, historicalPhase: name => phases.push(name),
    savedPath: path.split("/submit?")[0], query: `?companyId=${company}`, prepared: receipt,
    initialCounts: [1], initial: ["original"], completed: async task => { assert.equal(task, receipt.taskId); calls.worker++; },
    oneGraph: (counts, snapshot, task) => { calls.graph++; assert.equal(counts[0], 1);
      assert.equal(snapshot[0], "original"); assert.equal(task, receipt.taskId); if (graphFailure) proof(false); },
    idle: async () => { calls.idle++; },
    // Inert clock compresses only the existing20s reader timer. Keep its requested
    // bound observable; neither native proof nor product timeout is changed.
    setTimeout: (callback, milliseconds) => { assert.equal(milliseconds, 20_000); return setTimeout(callback, 5); }, clearTimeout };
  context.bounded = runInNewContext("(" + section("async function bounded(", "\n  const guid =") + ")", context);
  context.requiredReceipt = runInNewContext("(" + section("async function requiredReceipt(", "\n  const sent = [];") + ")", context);
  const caller = section("const positive = page.waitForResponse(", "\n    stage(\"second-provider-owner-and-company-isolation\")");
  return { calls, phases, receipt, run: () => runInNewContext("(async () => {" + caller + "})()", context) };
}

test("actual restored-source caller requires original completed bounded body before exact receipt and one graph", async () => {
  const value = restoredReceiptFixture(); await value.run();
  assert.deepEqual(value.calls, { click: 1, body: 1, json: 0, worker: 1, graph: 1, idle: 1, wait: 1 });
  assert.ok(value.phases.indexOf("restored-replay-body-json") < value.phases.indexOf("restored-receipt-equal"));
  assert.equal(value.phases.at(-1), "restored-idle");
});

for (const [name, options, lastPhase] of [
  ["non202", { status: 403 }, "restored-refused-other"],
  ["failed stream", { finished: new Error("PRIVATE") }, "restored-replay-stream-failed"],
  ["stalled stream", { stalled: true }, "restored-replay-stream-wait-failed"],
  ["stalled body", { stalledBody: true }, "restored-replay-body-read-failed"],
  ["empty body", { bytes: Buffer.alloc(0) }, "restored-replay-empty-body"],
  ["oversized body", { bytes: Buffer.alloc(4097, 32) }, "restored-replay-body-too-large"],
  ["invalid UTF8", { bytes: Buffer.from([0xc3, 0x28]) }, "restored-replay-body-invalid-utf8"],
  ["invalid JSON", { bytes: Buffer.from("{PRIVATE") }, "restored-replay-body-invalid-json"],
  ["unavailable browser body", { bodyFailure: new Error("No resource with given identifier PRIVATE") }, "restored-replay-browser-body-unavailable"],
  ["failed body read", { bodyFailure: new Error("PRIVATE") }, "restored-replay-body-read-failed"],
  ["wrong receipt", { bytes: Buffer.from("{}") }, "restored-receipt-equal"],
]) test(`actual restored-source caller refuses ${name} before worker or graph`, async () => {
  const value = restoredReceiptFixture(options);
  await assert.rejects(value.run(), error => error.message === "Task submission browser proof failed.");
  assert.equal(value.phases.at(-1), lastPhase); assert.equal(value.calls.json, 0);
  assert.equal(value.calls.worker, 0); assert.equal(value.calls.graph, 0); assert.equal(value.calls.idle, 0);
  assert.ok(value.phases.every(phase => !phase.includes("PRIVATE")));
});

test("actual restored-source caller retains one-graph refusal after a valid original receipt", async () => {
  const value = restoredReceiptFixture({ graphFailure: true });
  await assert.rejects(value.run(), error => error.message === "Task submission browser proof failed.");
  assert.equal(value.phases.at(-1), "restored-one-graph"); assert.equal(value.calls.worker, 1);
  assert.equal(value.calls.graph, 1); assert.equal(value.calls.idle, 0);
});
