import assert from "node:assert/strict";
import test from "node:test";
import { resolve, join } from "node:path";
import { tmpdir } from "node:os";
import { mkdtemp, mkdir, writeFile, unlink, rmdir } from "node:fs/promises";
import { verifyCompanyAdministrators } from "./smoke-browser-administrators.mjs";
import { startOwnedCoreReplyProxy } from "./owned-browser-core-proxy.mjs";
import { verifyTaskHistory } from "./smoke-browser-task-history.mjs";

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
