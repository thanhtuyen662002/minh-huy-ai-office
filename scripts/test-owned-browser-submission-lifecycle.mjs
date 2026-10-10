import assert from "node:assert/strict";
import test from "node:test";
import { runInNewContext } from "node:vm";
import { observeSubmissionLifecycle, submissionAbortStage } from "./owned-browser-submission-lifecycle.mjs";

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
