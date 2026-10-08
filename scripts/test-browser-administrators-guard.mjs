import assert from "node:assert/strict";
import test from "node:test";
import { resolve, join } from "node:path";
import { verifyCompanyAdministrators } from "./smoke-browser-administrators.mjs";

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
