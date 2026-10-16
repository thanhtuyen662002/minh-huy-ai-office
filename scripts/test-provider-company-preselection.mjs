import { test } from "node:test";
import assert from "node:assert/strict";
import { verifyCompanyPreselection } from "./smoke-browser-company-preselection.mjs";
import { finishCompanyDiscovery } from "./owned-browser-company-discovery.mjs";

test("company browser refuses unowned flags/directory/hosts before files, SQL or browser", async () => {
  const prior = { CI: process.env.CI, GITHUB_ACTIONS: process.env.GITHUB_ACTIONS, RUNNER_TEMP: process.env.RUNNER_TEMP };
  let calls = 0;
  try {
    for (const env of [{ CI: "false", GITHUB_ACTIONS: "true" }, { CI: "true", GITHUB_ACTIONS: "false" }]) {
      Object.assign(process.env, env, { RUNNER_TEMP: "C:/owned-ci-test" });
      await assert.rejects(() => verifyCompanyPreselection({ directory: "C:/unowned", app: "http://127.0.0.1:3000", identity: "http://127.0.0.1:8081",
        browser: { newContext() { calls++; } }, sql() { calls++; } }), /^Error: Owned provider company proof failed\.$/);
    }
    assert.equal(calls, 0);
  } finally {
    for (const [key, value] of Object.entries(prior)) if (value === undefined) delete process.env[key]; else process.env[key] = value;
  }
});
test("existing browser proof follows an explicit authorized chooser UI and requires completed selection", async () => {
  const target = "22222222-2222-2222-2222-222222222222", events = [];
  const page = {
    getByRole(kind, selector) { events.push([kind, selector.name]); return { async waitFor() {}, async count() { return 0; }, async click() { events.push("click"); } }; },
    locator() { return { async count() { return 0; } }; },
    async evaluate() { return { status: 200, cache: "no-store", value: { items: [{ companyId: target, companyName: "Owned choice" }] } }; },
    async waitForResponse() { return { status() { return 200; } }; }, async waitForURL() { events.push("home"); },
  };
  await finishCompanyDiscovery(page, "http://127.0.0.1:3000", target, { status: () => 303, headers: () => ({ location: "http://127.0.0.1:3000/sign-in/company" }) });
  assert(events.some((item) => Array.isArray(item) && item[1] === "Owned choice")); assert(events.includes("click")); assert.equal(events.at(-1), "home");
  page.waitForResponse = async () => ({ status: () => 403 });
  await assert.rejects(() => finishCompanyDiscovery(page, "http://127.0.0.1:3000", target,
    { status: () => 303, headers: () => ({ location: "http://127.0.0.1:3000/sign-in/company" }) }));
});
test("old proof cannot silently select missing/duplicate target or paint an old private workspace", async () => {
  const target = "22222222-2222-2222-2222-222222222222";
  for (const mode of ["missing", "duplicate", "private-workspace"]) {
    let clicked = false;
    const page = {
      getByRole() { return { async waitFor() {}, async count() { return 0; }, async click() { clicked = true; } }; },
      locator() { return { async count() { return mode === "private-workspace" ? 1 : 0; } }; },
      async evaluate() { return { status: 200, cache: "no-store", value: { items: mode === "missing" ? []
        : [{ companyId: target, companyName: "Owned" }, { companyId: target, companyName: "Duplicate" }] } }; },
    };
    await assert.rejects(() => finishCompanyDiscovery(page, "http://127.0.0.1:3000", target,
      { status: () => 303, headers: () => ({ location: "http://127.0.0.1:3000/sign-in/company" }) }));
    assert.equal(clicked, false);
  }
});
