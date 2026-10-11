// Cold tests of the actual helper AND caller; no Chromium/SQL/runtime qualification.
import assert from "node:assert/strict";
import test from "node:test";
import { readFile } from "node:fs/promises";
import { runInNewContext } from "node:vm";

const source = await readFile(new URL("./smoke-browser-oidc.mjs", import.meta.url), "utf8");
const helperStart = source.indexOf("async function readOwnedExpiryReceipt(");
const helperEnd = source.indexOf("\ntry {", helperStart);
assert.ok(helperStart >= 0 && helperEnd > helperStart);
const helper = source.slice(helperStart, helperEnd);
const callerStart = source.indexOf("  const submitted = page.waitForResponse(");
const callerEnd = source.indexOf("  const privateDraft =", callerStart);
assert.ok(callerStart > helperEnd && callerEnd > callerStart);
const caller = source.slice(callerStart, callerEnd);
const company = "22222222-2222-4222-8222-222222222222", taskId = "33333333-3333-4333-8333-333333333333";
const url = `http://127.0.0.1:3000/api/local/tasks/intents/11111111-1111-4111-8111-111111111111/submit?companyId=${company}`;

function fixture({ status = 202, cache = "no-store", bytes, stream, stalledStream, stalledBody, bodyFailure, networkFailure, waiterFailure, heldClick } = {}) {
  const calls = { wait: 0, click: 0, finished: 0, body: 0, json: 0, taskRead: 0, privateUi: 0 }, phases = [];
  const response = { status: () => status, headers: () => ({ "cache-control": cache }), url: () => url,
    request: () => ({ method: () => "POST", failure: () => networkFailure ? { errorText: networkFailure } : null }),
    finished: () => { calls.finished++; return stalledStream ? new Promise(() => {}) : Promise.resolve(stream ?? null); },
    body: async () => { calls.body++; if (bodyFailure) throw bodyFailure;
      return stalledBody ? new Promise(() => {}) : bytes ?? Buffer.from(JSON.stringify({ taskId })); } };
  const context = { TextDecoder, Promise, Map, Error, URL,
    JSON: { parse: text => { calls.json++; return JSON.parse(text); } },
    setTimeout: (callback, milliseconds) => { assert.equal(milliseconds, 20_000); return setTimeout(callback, 5); }, clearTimeout,
    stage: "cold-original", query: `?companyId=${company}`, privateMessage: "PRIVATE synthetic fixture",
    guid: value => typeof value === "string" && /^[0-9a-f-]{36}$/.test(value) && value !== "00000000-0000-0000-0000-000000000000",
    requireProof: value => { if (!value) throw new Error("Browser proof failed."); },
    page: { waitForResponse: predicate => { calls.wait++; assert.equal(predicate(response), true);
      return waiterFailure ? Promise.reject(waiterFailure) : Promise.resolve(response); },
      getByRole: (role, options) => { assert.equal(role, "button"); assert.equal(options.name, "Gửi"); assert.equal(options.exact, true);
        return { click: async () => { calls.click++; if (heldClick) await new Promise(resolve => setTimeout(resolve, 15)); } }; },
      getByText: (text, options) => { assert.equal(text, "PRIVATE synthetic fixture"); assert.equal(options.exact, true);
        return { waitFor: async () => { calls.privateUi++; } }; } },
    get: async target => { assert.equal(target, `/api/local/tasks/${taskId}?companyId=${company}`); calls.taskRead++;
      return { status: 200, cache: "no-store", text: JSON.stringify({ taskId }) }; },
  };
  context.readOwnedExpiryReceipt = runInNewContext("("+helper+")", context);
  return { calls, phases, response, context,
    read: () => context.readOwnedExpiryReceipt(response, phase => phases.push(phase)),
    run: () => runInNewContext("(async () => {"+caller+"})()", context) };
}

test("actual expiry caller requires the original completed bounded receipt before durable task and private UI", async () => {
  const value = fixture(); await value.run();
  assert.deepEqual(value.calls, { wait: 1, click: 1, finished: 1, body: 1, json: 2, taskRead: 1, privateUi: 1 });
  assert.equal(value.context.stage, "owned-expiry-private-message");
});

for (const [name, options, phase] of [
  ["non202", { status: 403 }, "owned-expiry-submit-accepted"],
  ["cacheable reply", { cache: "private" }, "owned-expiry-submit-accepted"],
  ["failed stream", { stream: new Error("PRIVATE") }, "owned-expiry-receipt-stream-failed"],
  ["stalled stream", { stalledStream: true }, "owned-expiry-receipt-stream-wait-failed"],
  ["stalled body", { stalledBody: true }, "owned-expiry-receipt-body-read-failed"],
  ["empty body", { bytes: Buffer.alloc(0) }, "owned-expiry-receipt-empty-body"],
  ["oversized body", { bytes: Buffer.alloc(4097, 32) }, "owned-expiry-receipt-body-too-large"],
  ["invalid UTF8", { bytes: Buffer.from([0xc3, 0x28]) }, "owned-expiry-receipt-body-invalid-utf8"],
  ["invalid JSON", { bytes: Buffer.from("{PRIVATE") }, "owned-expiry-receipt-body-invalid-json"],
  ["unavailable protocol body", { bodyFailure: new Error("No resource with given identifier PRIVATE") }, "owned-expiry-receipt-browser-body-unavailable"],
  ["failed body read", { bodyFailure: new Error("PRIVATE URL/token") }, "owned-expiry-receipt-body-read-failed"],
  ["missing task identity", { bytes: Buffer.from("{}") }, "owned-expiry-submit-receipt-task-id"],
  ["empty task identity", { bytes: Buffer.from(JSON.stringify({ taskId: "00000000-0000-0000-0000-000000000000" })) }, "owned-expiry-submit-receipt-task-id"],
]) test(`actual expiry caller refuses ${name} before any task read or private UI`, async () => {
  const value = fixture(options);
  await assert.rejects(value.run(), error => ["Browser receipt proof failed.", "Browser proof failed."].includes(error.message));
  assert.equal(value.context.stage, phase); assert.equal(value.calls.taskRead, 0); assert.equal(value.calls.privateUi, 0);
  assert.equal(value.calls.click, 1); assert.equal(value.calls.wait, 1);
  assert.ok(!value.context.stage.includes("PRIVATE"));
});

for (const [code, phase] of [
  ["net::ERR_ABORTED", "receipt-request-aborted"], ["net::ERR_CONNECTION_RESET", "receipt-request-reset"],
  ["net::ERR_INCOMPLETE_CHUNKED_ENCODING", "receipt-request-truncated"], ["net::ERR_CONTENT_LENGTH_MISMATCH", "receipt-request-length"],
  ["net::ERR_FAILED", "receipt-request-failed"], ["PRIVATE arbitrary text", "receipt-stream-failed"],
]) test(`actual original reader projects only the fixed ${phase} refusal`, async () => {
  const value = fixture({ stream: new Error("PRIVATE"), networkFailure: code });
  await assert.rejects(value.read(), error => error.message === "Browser receipt proof failed.");
  assert.equal(value.phases.at(-1), phase); assert.equal(value.calls.body, 0); assert.equal(value.calls.taskRead, 0);
});

test("early original waiter rejection is observed while click is pending and is still required", async () => {
  const original = new Error("PRIVATE original waiter"); const value = fixture({ waiterFailure: original, heldClick: true });
  await assert.rejects(value.run(), error => error === original);
  assert.equal(value.context.stage, "owned-expiry-submit-response");
  assert.equal(value.calls.wait, 1); assert.equal(value.calls.click, 1); assert.equal(value.calls.body, 0); assert.equal(value.calls.taskRead, 0);
});
