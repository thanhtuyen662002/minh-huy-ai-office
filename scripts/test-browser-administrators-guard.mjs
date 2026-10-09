import assert from "node:assert/strict";
import test from "node:test";
import { resolve, join } from "node:path";
import { tmpdir } from "node:os";
import { mkdtemp, mkdir, writeFile, unlink, rmdir } from "node:fs/promises";
import { verifyCompanyAdministrators } from "./smoke-browser-administrators.mjs";
import { startOwnedCoreReplyProxy, committedReplyEvidence } from "./owned-browser-core-proxy.mjs";
import { verifyTaskHistory } from "./smoke-browser-task-history.mjs";
import { verifyTaskSubmission } from "./smoke-browser-task-submission.mjs";
import { requireOwnedGroupInbox, verifyOwnedGroupInbox } from "./smoke-browser-group-inbox.mjs";

test("group inbox browser refuses unowned flags/directories before fixture files, Docker, SQL or network", async () => {
  const root = resolve("guard-test-no-resources"), owned = join(root, "aioffice-local");
  const previous = Object.fromEntries(["CI", "GITHUB_ACTIONS", "RUNNER_TEMP"].map(key => [key, process.env[key]]));
  try {
    for (const scenario of [{ CI: "false" }, { GITHUB_ACTIONS: "false" }, { RUNNER_TEMP: "" }, { directory: root }, { directory: join(owned, "nested") }]) {
      const { directory = owned, ...flags } = scenario;
      Object.assign(process.env, { CI: "true", GITHUB_ACTIONS: "true", RUNNER_TEMP: root }, flags);
      await assert.rejects(verifyOwnedGroupInbox(directory, null), /^Error: Owned group inbox proof refused\.$/);
    }
    requireOwnedGroupInbox(owned, { CI: "true", GITHUB_ACTIONS: "true", RUNNER_TEMP: root });
    Object.assign(process.env, { CI: "true", GITHUB_ACTIONS: "true", RUNNER_TEMP: root });
    await assert.rejects(verifyOwnedGroupInbox(owned, { sourceId: "invalid" }), /^Error: Owned group inbox proof refused\.$/);
  } finally {
    for (const [key, value] of Object.entries(previous)) { if (value === undefined) delete process.env[key]; else process.env[key] = value; }
  }
});

test("submission browser proof refuses unowned paths/flags/hosts before resources", async () => {
  const root = resolve("guard-test-no-resources"), owned = join(root, "aioffice-local");
  const previous = Object.fromEntries(["CI", "GITHUB_ACTIONS", "RUNNER_TEMP"].map(key => [key, process.env[key]]));
  try {
    for (const scenario of [{ flags: { CI: "false" } }, { flags: { GITHUB_ACTIONS: "false" } }, { flags: { RUNNER_TEMP: "" } },
      { directory: root }, { directory: join(owned, "nested") }, { app: "https://customer.example.invalid" }, { identity: "https://customer.example.invalid" }]) {
      Object.assign(process.env, { CI: "true", GITHUB_ACTIONS: "true", RUNNER_TEMP: root }, scenario.flags);
      await assert.rejects(verifyTaskSubmission({ directory: owned, app: "http://127.0.0.1:3000", identity: "http://127.0.0.1:8081", ...scenario }), /^Error: Task submission browser proof failed\.$/);
    }
  } finally {
    for (const [key, value] of Object.entries(previous)) { if (value === undefined) delete process.env[key]; else process.env[key] = value; }
  }
});

test("Core fault matches only the actual scoped prepare or fingerprint-only202 receipt", () => {
  const id = number => `10000000-0000-0000-0000-${String(number).padStart(12, "0")}`;
  const source = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", company = id(1), operationId = id(2), question = "Tồn kho 😀 �";
  const inputFingerprint = "6354CD8F9D07F489B9F1B213CE89B4FE5EF4016DFC44329AE740B0C39BDE0656";
  const request = { method: "POST", url: `/api/tasks/intents/${operationId}/submit`, headers: { "x-aioffice-company-id": company } };
  const fault = { kind: "task-submit", count: 0, company, source, question };
  const receipt = { companyId: company, operationId, dataSourceId: source, inputFingerprint, taskId: id(3), stepId: id(4), messageId: id(5),
    status: 0, dispatchState: 0, createdAtUtc: "2026-10-09T00:00:00+00:00" };
  assert.equal(committedReplyEvidence(fault, request, 202, { inputFingerprint }, receipt).taskId, id(3));
  for (const [changedRequest, status, input, changedReceipt, changedFault] of [
    [{ ...request, method: "GET" }, 202, { inputFingerprint }, receipt, fault],
    [{ ...request, headers: { "x-aioffice-company-id": id(9) } }, 202, { inputFingerprint }, receipt, fault],
    [{ ...request, url: `/api/tasks/intents/${id(9)}/submit` }, 202, { inputFingerprint }, receipt, fault],
    [request, 200, { inputFingerprint }, receipt, fault], [request, 202, { inputFingerprint, question }, receipt, fault],
    [request, 202, { inputFingerprint }, { ...receipt, operationId: id(9) }, fault],
    [request, 202, { inputFingerprint }, { ...receipt, companyId: id(9) }, fault],
    [request, 202, { inputFingerprint }, { ...receipt, dataSourceId: id(9) }, fault],
    [request, 202, { inputFingerprint }, { ...receipt, inputFingerprint: "0".repeat(64) }, fault],
    [request, 202, { inputFingerprint }, { ...receipt, stepId: "invalid" }, fault],
    [request, 202, { inputFingerprint }, { ...receipt, status: 7 }, fault],
    [request, 202, { inputFingerprint }, { ...receipt, dispatchState: 4 }, fault],
    [request, 202, { inputFingerprint }, { ...receipt, dispatchState: undefined, dispatchStatus: 0 }, fault],
    [request, 202, { inputFingerprint }, receipt, { ...fault, count: 1 }],
    [request, 202, { inputFingerprint }, receipt, { ...fault, kind: "unknown" }],
  ]) assert.equal(committedReplyEvidence(changedFault, changedRequest, status, input, changedReceipt), null);
  const prepared = { companyId: company, operationId, dataSourceId: source, question, inputFingerprint, state: 0, accepted: null,
    createdAtUtc: receipt.createdAtUtc, expiresAtUtc: "2026-10-10T00:00:00+00:00" };
  const prepareFault = { ...fault, kind: "intent-prepare" }, prepareRequest = { ...request, url: "/api/tasks/intents" }, input = { operationId, dataSourceId: source, question };
  assert.equal(committedReplyEvidence(prepareFault, prepareRequest, 200, input, prepared).operationId, operationId);
  for (const delta of [{ state: 1 }, { accepted: receipt }, { question: "changed" }, { expiresAtUtc: "2026-10-11T00:00:00Z" }])
    assert.equal(committedReplyEvidence(prepareFault, prepareRequest, 200, input, { ...prepared, ...delta }), null);
});

test("Core reply transport refuses unowned flags/directories before listener or files", async () => {
  const keys = ["CI", "GITHUB_ACTIONS", "AIOFFICE_BROWSER_CORE_REPLY_PROOF"];
  const previous = Object.fromEntries(keys.map(key => [key, process.env[key]]));
  try {
    for (const scenario of [{ CI: "false" }, { GITHUB_ACTIONS: "false" }, { AIOFFICE_BROWSER_CORE_REPLY_PROOF: "false" }, { directory: "/tmp/retained" }]) {
      Object.assign(process.env, { CI: "true", GITHUB_ACTIONS: "true", AIOFFICE_BROWSER_CORE_REPLY_PROOF: "true" }, scenario);
      await assert.rejects(startOwnedCoreReplyProxy(scenario.directory ?? "/tmp/aioffice-core-reply-proof"), /^Error: Owned Core reply proof refused\.$/);
    }
  } finally {
    for (const [key, value] of Object.entries(previous)) { if (value === undefined) delete process.env[key]; else process.env[key] = value; }
  }
});

test("administrator browser adversaries refuse unowned paths/flags/hosts before resources", async () => {
  const root = resolve("guard-test-no-resources"), owned = join(root, "aioffice-local");
  const previous = Object.fromEntries(["CI", "GITHUB_ACTIONS", "RUNNER_TEMP"].map(key => [key, process.env[key]]));
  const defaults = { CI: "true", GITHUB_ACTIONS: "true", RUNNER_TEMP: root };
  const cases = [
    { flags: { CI: "false" } }, { flags: { GITHUB_ACTIONS: "false" } }, { flags: { RUNNER_TEMP: "" } },
    { directory: root }, { directory: join(root, "retained") }, { directory: join(owned, "nested") },
    { directory: join(root, "..", "aioffice-local") }, { app: "https://example.invalid" }, { identity: "https://example.invalid" },
  ];
  try {
    for (const scenario of cases) {
      Object.assign(process.env, defaults, scenario.flags);
      await assert.rejects(verifyCompanyAdministrators({ directory: owned, app: "http://127.0.0.1:3000",
        identity: "http://127.0.0.1:8081", ...scenario }), /^Error: Administrator browser proof failed\.$/);
    }
  } finally {
    for (const [key, value] of Object.entries(previous)) {
      if (value === undefined) delete process.env[key]; else process.env[key] = value;
    }
  }
});

test("archive browser proof refuses unowned paths/flags/hosts before fixture reads or browser use", async () => {
  const root = resolve("guard-test-no-resources"), owned = join(root, "aioffice-local");
  const previous = Object.fromEntries(["CI", "GITHUB_ACTIONS", "RUNNER_TEMP"].map(key => [key, process.env[key]]));
  const defaults = { CI: "true", GITHUB_ACTIONS: "true", RUNNER_TEMP: root };
  try {
    for (const scenario of [{ flags: { CI: "false" } }, { flags: { GITHUB_ACTIONS: "false" } }, { flags: { RUNNER_TEMP: "" } },
      { directory: root }, { directory: join(owned, "nested") }, { app: "https://customer.example.invalid" }, { identity: "https://customer.example.invalid" }]) {
      Object.assign(process.env, defaults, scenario.flags);
      await assert.rejects(verifyTaskHistory({ directory: owned, app: "http://127.0.0.1:3000", identity: "http://127.0.0.1:8081", ...scenario }), /^Error: Task history browser proof failed\.$/);
    }
  } finally {
    for (const [key, value] of Object.entries(previous)) { if (value === undefined) delete process.env[key]; else process.env[key] = value; }
  }
});

test("archive positive fixture and fingerprint preparation reaches browser without network or SQL", async () => {
  const root = await mkdtemp(join(tmpdir(), "aioffice-history-guard-")), owned = join(root, "aioffice-local");
  const previous = Object.fromEntries(["CI", "GITHUB_ACTIONS", "RUNNER_TEMP"].map(key => [key, process.env[key]]));
  const id = number => `10000000-0000-0000-0000-${String(number).padStart(12, "0")}`;
  let stage, fingerprints = 0;
  const path = join(owned, "history-fixture.json");
  try {
    await mkdir(owned);
    await writeFile(path, JSON.stringify({ companyId: id(4), otherUserId: id(5),
      otherUsername: "archive-proof-" + "a".repeat(32), taskIds: [6, 7, 8, 9].map(id) }), { mode: 0o600 });
    Object.assign(process.env, { CI: "true", GITHUB_ACTIONS: "true", RUNNER_TEMP: root });
    await assert.rejects(verifyTaskHistory({ directory: owned, app: "http://127.0.0.1:3000", identity: "http://127.0.0.1:8081",
      manifest: { AIOFFICE_COMPANY_ID: id(1), AIOFFICE_TENANT_ID: id(2), AIOFFICE_USER_ID: id(3) },
      sql: () => { fingerprints++; return "AB".repeat(32); }, setStage: value => { stage = value; },
      browser: { newContext: () => { throw new Error("Positive preparation reached browser sentinel"); } },
    }), /^Error: Positive preparation reached browser sentinel$/);
    assert.equal(stage, "history-owned-second-browser-context");
    assert.equal(fingerprints, 12);
  } finally {
    for (const [key, value] of Object.entries(previous)) { if (value === undefined) delete process.env[key]; else process.env[key] = value; }
    await unlink(path).catch(() => {}); await rmdir(owned); await rmdir(root);
  }
});
