import assert from "node:assert/strict";
import test from "node:test";
import { runInNewContext } from "node:vm";
import { observeSubmissionLifecycle, submissionAbortStage, observeSubmissionWire, submissionWireStage } from "./owned-browser-submission-lifecycle.mjs";
import { EventEmitter } from "node:events";
import { resolve, join } from "node:path";

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
  for (const controller of controllers) await value.window.fetch(path, { method: "POST", signal: controller.signal });
  controllers[8].abort(); assert.equal(value.read().kind, "absent");
  controllers[7].abort(); assert.equal(value.read().kind, "owner");
  const wrapper = () => {}; value.window.fetch = wrapper; value.dispose();
  assert.equal(value.window.fetch, wrapper);
  const second = fixture(async () => ({})), controller = new AbortController();
  await second.window.fetch(path, { method: "POST", signal: controller.signal });
  const project = second.window.__aiofficeOwnedSubmissionLifecycle.read;
  second.dispose(); controller.abort(); assert.equal(project(origin + path).kind, "absent");
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
    for (let index = 0; index < 9; index++) session.emit("Network.requestWillBeSent", { requestId: String(index), request: { url: origin + path, method: "POST" } });
    session.emit("Network.loadingFinished", { requestId: "8" });
    session.emit("Network.dataReceived", { requestId: "7", dataLength: NaN });
    assert.deepEqual(observer.read(origin + path), { bodyStarted: false, wireFinished: false, compressed: false });
    session.emit("Network.dataReceived", { requestId: "7", dataLength: 1 });
    assert.equal(observer.read(origin + path).bodyStarted, true);
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
