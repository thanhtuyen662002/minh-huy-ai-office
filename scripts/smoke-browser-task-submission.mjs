// Actual shipping UI, issued Code/S256 sessions and owned SQL/Core/worker only.
import { resolve, join } from "node:path";
import { readFile, mkdir } from "node:fs/promises";
import { randomUUID } from "node:crypto";
import { observeSubmissionLifecycle, submissionAbortStage } from "./owned-browser-submission-lifecycle.mjs";

export async function verifyTaskSubmission({ directory, manifest, browser, ownerPage: page, ownerContext, sql, app, identity, coreReplyFault, setStage }) {
  const proof = condition => { if (!condition) throw new Error("Task submission browser proof failed."); };
  proof(process.env.CI === "true" && process.env.GITHUB_ACTIONS === "true" && process.env.RUNNER_TEMP
    && resolve(directory) === join(resolve(process.env.RUNNER_TEMP), "aioffice-local")
    && app === "http://127.0.0.1:3000" && identity === "http://127.0.0.1:8081");
  const stage = name => setStage("submission-" + name);
  async function bounded(promise, milliseconds = 15_000) {
    let timer;
    try { return await Promise.race([promise, new Promise((_, reject) => { timer = setTimeout(() => reject(new Error("Task submission browser proof failed.")), milliseconds); })]); }
    finally { clearTimeout(timer); }
  }
  const guid = value => typeof value === "string" && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(value)
    && value !== "00000000-0000-0000-0000-000000000000";
  const [tenant, company, owner, source] = ["TENANT", "COMPANY", "USER", "DATA_SOURCE"].map(key => manifest[`AIOFFICE_${key}_ID`]);
  proof([tenant, company, owner, source].every(guid));
  const scope = `TenantId='${tenant}' AND CompanyId='${company}'`, query = `?companyId=${company}`;
  const tables = { Tasks: "TenantId,CompanyId,Id", TaskSteps: "TenantId,CompanyId,TaskId,Id",
    TaskEvents: "TenantId,CompanyId,TaskId,Sequence", TaskCheckpoints: "TenantId,CompanyId,TaskId,StepId,Version",
    TaskStepExecutions: "TenantId,CompanyId,TaskId,StepId", TaskDispatches: "TenantId,CompanyId,TaskId,StepId,MessageId",
    CustomerAiCreditSettlements: "SettlementId", TaskSubmissionIntents: "TenantId,CompanyId,UserId,OperationId" };
  const snapshot = () => Object.entries(tables).map(([table, order]) => {
    const value = sql(`USE AIOfficeLocal; SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),
      COALESCE((SELECT * FROM aioffice.${table} ORDER BY ${order} FOR JSON PATH,INCLUDE_NULL_VALUES),N'[]'))),2);`);
    proof(/^[0-9A-F]{64}$/.test(value)); return value;
  });
  const equal = (a, b) => proof(a.length === b.length && a.every((value, index) => value === b[index]));
  const effectCounts = () => sql("USE AIOfficeLocal; SELECT CONCAT(" + Object.keys(tables).slice(0, -1)
    .map(table => `(SELECT COUNT_BIG(*) FROM aioffice.${table})`).join(",N',',") + ");").split(",").map(Number);
  function oneGraph(beforeCounts, beforeSnapshot, task) {
    const after = effectCounts(), expected = [1, 1, 3, 1, 1, 1, 0];
    proof(beforeCounts.length === expected.length && after.length === expected.length
      && after.every((value, index) => value - beforeCounts[index] === expected[index]));
    proof(snapshot().at(-2) === beforeSnapshot.at(-2));
    for (const table of Object.keys(tables).slice(0, -2)) {
      proof(sql(`USE AIOfficeLocal; SELECT COUNT(*) FROM aioffice.${table} WHERE ${scope} AND ${table === "Tasks" ? "Id" : "TaskId"}='${task}';`)
        === (table === "TaskEvents" ? "3" : "1"));
    }
  }
  const get = (target, path) => target.evaluate(async path => {
    const response = await fetch(path, { cache: "no-store" }); return { status: response.status, text: await response.text() };
  }, path);
  const sid = async context => (await context.cookies(app)).find(item => item.name === "aioffice_browser_session")?.value;
  const waitResponse = (path, status) => page.waitForResponse(response => new URL(response.url()).pathname === path
    && response.request().method() === "POST" && response.status() === status, { timeout: 20_000 });
  async function switchCompany(target, selected, phase = "provider-company-switch") {
    const callback = target.waitForResponse(response => response.url().startsWith(app + "/api/local/session/oidc/callback?"));
    const request = target.waitForRequest(request => request.url().startsWith(identity + "/realms/aioffice-local/protocol/openid-connect/auth?"));
    // A waiter can reject while selectOption is still pending. Observe that
    // rejection immediately, then require the original waiter below so its
    // failure reaches the fixed-stage parent and owned restoration finally.
    callback.catch(() => {}); request.catch(() => {});
    stage(phase + "-select");
    await target.getByRole("combobox", { name: "Chuyển công ty", exact: true }).selectOption(selected);
    stage(phase + "-auth-request");
    const url = new URL((await request).url()); proof(url.searchParams.get("response_type") === "code" && url.searchParams.get("code_challenge_method") === "S256");
    stage(phase + "-callback");
    proof((await callback).status() === 303);
    stage(phase + "-workspace");
    await target.getByRole("button", { name: "Đăng xuất", exact: true }).waitFor();
    stage(phase + "-current-session");
    const current = await get(target, `/api/local/session?companyId=${selected}`); proof(current.status === 200 && JSON.parse(current.text).companyId === selected);
  }
  async function completed(task) {
    proof(guid(task));
    for (let attempt = 0; attempt < 120; attempt++) {
      if (sql(`USE AIOfficeLocal; SELECT COUNT(*) FROM aioffice.Tasks WHERE ${scope} AND Id='${task}' AND Status=N'Completed';`) === "1") {
        proof(sql(`USE AIOfficeLocal; SELECT COUNT(*) FROM aioffice.TaskStepExecutions WHERE ${scope} AND TaskId='${task}' AND Attempt=1 AND LastFailureClass IS NULL;`) === "1");
        proof(sql(`USE AIOfficeLocal; SELECT COUNT(*) FROM aioffice.TaskDispatches WHERE ${scope} AND TaskId='${task}' AND Attempt=1;`) === "1");
        proof(sql(`USE AIOfficeLocal; SELECT COUNT(*) FROM aioffice.TaskCheckpoints WHERE ${scope} AND TaskId='${task}';`) === "1"); return;
      }
      await page.waitForTimeout(500);
    }
    proof(false);
  }
  async function composer(question) {
    await page.getByRole("button", { name: "Trợ lý AI", exact: true }).first().click();
    await page.getByRole("combobox", { name: "Nguồn dữ liệu", exact: true }).selectOption(source);
    await page.locator('textarea[name="question"]').fill(question);
  }
  async function idle() { await page.getByRole("button", { name: "Gửi", exact: true }).waitFor();
    await page.waitForFunction(() => [...document.querySelectorAll("button")].some(button => button.textContent.trim() === "Gửi" && !button.disabled), null, { timeout: 60_000 }); }
  async function requiredReceipt(response, phase) {
    const streamFailure = async fallback => {
      // Playwright's client finished promise need not settle on requestfailed.
      // Read the actual browser failure, exposing only exact fixed categories.
      // This remains a refusal; never replace the receipt or retry a request.
      let failure;
      try { failure = response.request().failure()?.errorText; } catch { /* Unknown browser boundary. */ }
      if (failure === "net::ERR_ABORTED") {
        try {
          return submissionAbortStage(await page.evaluate(url => window.__aiofficeOwnedSubmissionLifecycle?.read(url) ?? null, response.url()));
        } catch { return "replay-request-aborted"; }
      }
      return new Map([
        ["net::ERR_ABORTED", "replay-request-aborted"],
        ["net::ERR_CONNECTION_RESET", "replay-request-reset"],
        ["net::ERR_INCOMPLETE_CHUNKED_ENCODING", "replay-request-truncated"],
        ["net::ERR_CONTENT_LENGTH_MISMATCH", "replay-request-length"],
        ["net::ERR_FAILED", "replay-request-failed"],
      ]).get(failure) ?? fallback;
    };
    phase("replay-stream-finished");
    let finished;
    try { finished = await bounded(response.finished(), 20_000); }
    catch { phase(await streamFailure("replay-stream-wait-failed")); proof(false); }
    if (finished !== null) { phase(await streamFailure("replay-stream-failed")); proof(false); }
    phase("replay-body-read");
    let bytes;
    try { bytes = await bounded(response.body(), 20_000); }
    catch (error) {
      // Classify only fixed browser protocol boundaries. Never emit the
      // exception text, which may contain URLs, selectors or private JSON.
      phase(typeof error?.message === "string" && /No (?:resource|data).*identifier/i.test(error.message)
        ? "replay-browser-body-unavailable" : "replay-body-read-failed"); proof(false);
    }
    phase(bytes.byteLength === 0 ? "replay-empty-body" : bytes.byteLength > 4096 ? "replay-body-too-large" : "replay-body-utf8");
    proof(bytes.byteLength > 0 && bytes.byteLength <= 4096);
    let text;
    try { text = new TextDecoder("utf-8", { fatal: true }).decode(bytes); }
    catch { phase("replay-body-invalid-utf8"); proof(false); }
    phase("replay-body-json");
    try { return JSON.parse(text); }
    catch { phase("replay-body-invalid-json"); proof(false); }
  }
  const sent = [];
  const observe = request => { if (request.method() === "POST" && new URL(request.url()).pathname.startsWith("/api/local/tasks/intents"))
    sent.push({ path: new URL(request.url()).pathname, body: request.postDataJSON() }); };
  page.on("request", observe);
  let secondaryContext, release, heldPattern;
  try {
    await page.evaluate(observeSubmissionLifecycle, { origin: app, company });
    for (const boundary of ["headers", "body"]) {
      const phase = name => stage("real-core202-" + boundary + "-" + name);
      phase("initial-session");
      const question = `Kiểm tra tồn kho ${boundary} 😀 �`, originalSid = await sid(ownerContext), count = sent.length;
      const initialCounts = effectCounts(), initialSnapshot = snapshot();
      proof(originalSid);
      phase("composer"); await composer(question);
      phase("fault-arm");
      coreReplyFault.arm({ kind: "task-submit", company, source, question, boundary });
      const lost = page.waitForResponse(response => /\/api\/local\/tasks\/intents\/[0-9a-f-]{36}\/submit\?/.test(response.url())
        && response.request().method() === "POST" && response.status() === 503, { timeout: 20_000 });
      phase("first-response");
      await page.getByRole("button", { name: "Gửi", exact: true }).click(); await lost;
      phase("retry-visible");
      await page.getByRole("button", { name: "Thử lại đúng yêu cầu", exact: true }).waitFor();
      const fault = coreReplyFault.read(); coreReplyFault.disarm();
      phase("fault-evidence");
      proof(fault.count === 1 && fault.upstreamStatus === 202 && [fault.operationId, fault.taskId, fault.stepId, fault.messageId].every(guid));
      phase("post-requests");
      proof(sent.length === count + 2 && sent[count].body.question === question && sent[count].body.operationId === fault.operationId
        && Object.keys(sent[count + 1].body).join(",") === "inputFingerprint" && sent[count + 1].body.inputFingerprint === fault.inputFingerprint);
      phase("worker-completed"); await completed(fault.taskId);
      phase("graph-after-commit"); oneGraph(initialCounts, initialSnapshot, fault.taskId); const committed = snapshot();
      phase("session-unchanged");
      proof(await sid(ownerContext) === originalSid);
      // Observe a refusal as well as202; filtering it out hides the actual
      // boundary behind a timeout. Keep the202 acceptance requirement.
      const retry = page.waitForResponse(response => new URL(response.url()).pathname === `/api/local/tasks/intents/${fault.operationId}/submit`
        && response.request().method() === "POST", { timeout: 20_000 });
      retry.catch(() => {});
      phase("replay-click");
      await page.getByRole("button", { name: "Thử lại đúng yêu cầu", exact: true }).click();
      phase("replay-response");
      const response = await retry;
      phase(response.status() === 202 ? "replay-202-body" : response.status() === 503 ? "replay-refused503"
        : response.status() === 401 ? "replay-refused401" : response.status() === 409 ? "replay-refused409" : "replay-refused-other");
      proof(response.status() === 202);
      const receipt = await requiredReceipt(response, phase);
      phase("receipt-equal");
      proof(["companyId", "operationId", "inputFingerprint", "taskId", "stepId", "messageId", "createdAtUtc"].every(key => receipt[key] === (key === "companyId" ? company : fault[key])));
      phase("final-idle"); await idle();
      phase("same-original-request"); proof(await sid(ownerContext) === originalSid && sent.length === count + 3
        && sent[count + 2].path === sent[count + 1].path && JSON.stringify(sent[count + 2].body) === JSON.stringify(sent[count + 1].body));
      phase("graph-unchanged"); equal(snapshot(), committed);
      console.log(`PASS actual Core committed202 ${boundary} loss, same issuedSID/operation/fingerprint-only explicit retry and unchanged graph/worker/dispatch/settlements`);
    }

    stage("real-prepare-loss");
    const question = "\uFEFFYêu cầu nhập xuất khôi phục 😀 �\uFEFF", initial = snapshot(), initialCounts = effectCounts(), count = sent.length;
    await composer(question); coreReplyFault.arm({ kind: "intent-prepare", company, source, question, boundary: "body" });
    const lostPrepare = waitResponse("/api/local/tasks/intents", 503);
    await page.getByRole("button", { name: "Gửi", exact: true }).click(); await lostPrepare;
    await page.getByRole("button", { name: "Thử lại đúng yêu cầu", exact: true }).waitFor();
    const prepared = coreReplyFault.read(); coreReplyFault.disarm();
    proof(prepared.count === 1 && prepared.upstreamStatus === 200 && guid(prepared.operationId) && sent.length === count + 1);
    equal(snapshot().slice(0, -1), initial.slice(0, -1));
    const frozen = snapshot(), savedPath = `/api/local/tasks/intents/${prepared.operationId}`, oldSid = await sid(ownerContext);
    const stored = JSON.parse((await get(page, savedPath + query)).text);
    proof(stored.state === 0 && stored.question === question && stored.inputFingerprint === prepared.inputFingerprint);

    stage("reload-new-issued-session-get-only-recovery");
    await page.reload(); await page.getByRole("button", { name: "Đăng xuất", exact: true }).waitFor();
    await page.getByRole("button", { name: "Đăng xuất", exact: true }).click();
    const callback = page.waitForResponse(response => response.url().startsWith(app + "/api/local/session/oidc/callback?"));
    const authorization = page.waitForRequest(request => request.url().startsWith(identity + "/realms/aioffice-local/protocol/openid-connect/auth?"));
    await page.getByRole("button", { name: "Đăng nhập doanh nghiệp", exact: true }).click();
    proof(new URL((await authorization).url()).searchParams.get("code_challenge_method") === "S256" && (await callback).status() === 303);
    await page.getByRole("button", { name: "Đăng xuất", exact: true }).waitFor(); proof(await sid(ownerContext) && await sid(ownerContext) !== oldSid);
    await page.getByRole("button", { name: "Công việc", exact: true }).click();
    const panel = page.getByRole("region", { name: "Yêu cầu đã lưu", exact: true });
    await panel.getByRole("button", { name: "Lấy yêu cầu đã lưu " + question, exact: true }).waitFor();
    proof(sent.length === count + 1); equal(snapshot(), frozen);
    const artifact = join(resolve(process.env.RUNNER_TEMP), "aioffice-browser-proof"); await mkdir(artifact, { recursive: true });
    await page.screenshot({ path: join(artifact, "task-submission.png"), fullPage: true });
    await panel.getByRole("button", { name: "Lấy yêu cầu đã lưu " + question, exact: true }).click();
    await page.getByRole("button", { name: "Gửi yêu cầu đã lưu", exact: true }).waitFor();
    proof(sent.length === count + 1); equal(snapshot(), frozen);

    stage("historical-owner-read-current-source-denial");
    const historicalPhase = name => stage("historical-source-" + name);
    try {
      historicalPhase("disable-sql");
      sql(`USE AIOfficeLocal; UPDATE aioffice.DataSources SET IsEnabled=0 WHERE ${scope} AND Id='${source}';`);
      historicalPhase("owner-read");
      const historical = await get(page, savedPath + query);
      historicalPhase(historical.status === 200 ? "owner-body" : historical.status === 401 ? "owner-refused401"
        : historical.status === 403 ? "owner-refused403" : historical.status === 503 ? "owner-refused503" : "owner-refused-other");
      proof(historical.status === 200 && JSON.parse(historical.text).question === question);
      const denied = page.waitForResponse(response => new URL(response.url()).pathname === savedPath + "/submit"
        && response.request().method() === "POST", { timeout: 20_000 });
      denied.catch(() => {});
      historicalPhase("denied-send-click");
      await page.getByRole("button", { name: "Gửi yêu cầu đã lưu", exact: true }).click();
      historicalPhase("denied-response");
      const refusal = await denied;
      historicalPhase(refusal.status() === 403 ? "denied403" : refusal.status() === 409 ? "denied-refused409"
        : refusal.status() === 401 ? "denied-refused401" : refusal.status() === 503 ? "denied-refused503" : "denied-refused-other");
      proof(refusal.status() === 403);
      historicalPhase("retry-visible");
      await page.getByRole("button", { name: "Thử lại đúng yêu cầu", exact: true }).waitFor();
      historicalPhase("denied-graph-unchanged"); equal(snapshot(), frozen);
    } finally { sql(`USE AIOfficeLocal; UPDATE aioffice.DataSources SET IsEnabled=1 WHERE ${scope} AND Id='${source}';`); }
    const positive = page.waitForResponse(response => new URL(response.url()).pathname === savedPath + "/submit"
      && response.request().method() === "POST", { timeout: 20_000 });
    positive.catch(() => {});
    historicalPhase("restored-send-click");
    await page.getByRole("button", { name: "Thử lại đúng yêu cầu", exact: true }).click();
    historicalPhase("restored-response");
    const restoredResponse = await positive;
    historicalPhase(restoredResponse.status() === 202 ? "restored202-body" : restoredResponse.status() === 409 ? "restored-refused409"
      : restoredResponse.status() === 401 ? "restored-refused401" : restoredResponse.status() === 503 ? "restored-refused503" : "restored-refused-other");
    proof(restoredResponse.status() === 202); const receipt = await restoredResponse.json();
    historicalPhase("restored-receipt-equal");
    proof(receipt.operationId === prepared.operationId && receipt.inputFingerprint === prepared.inputFingerprint);
    historicalPhase("restored-worker"); await completed(receipt.taskId);
    historicalPhase("restored-one-graph"); oneGraph(initialCounts, initial, receipt.taskId);
    historicalPhase("restored-idle"); await idle();
    console.log("PASS real prepare reply loss, reload/new CodeS256 issuedSID GET-only owner recovery, explicit restored-source send and one completed task");

    stage("second-provider-owner-and-company-isolation");
    const bytes = await readFile(join(directory, "history-fixture.json")); proof(bytes.length <= 4096);
    const fixture = JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(bytes));
    proof(guid(fixture.companyId) && guid(fixture.otherUserId) && /^archive-proof-[0-9a-f]{32}$/.test(fixture.otherUsername));
    secondaryContext = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
    const secondary = await secondaryContext.newPage(); secondary.setDefaultTimeout(15_000); await secondary.goto(app);
    const auth = secondary.waitForRequest(request => request.url().startsWith(identity + "/realms/aioffice-local/protocol/openid-connect/auth?"));
    await secondary.getByRole("button", { name: "Đăng nhập doanh nghiệp", exact: true }).click();
    const url = new URL((await auth).url()); proof(url.searchParams.get("code_challenge_method") === "S256" && url.searchParams.get("response_type") === "code");
    await secondary.locator("#username").waitFor({ state: "visible" }); proof(new URL(secondary.url()).origin === identity);
    await secondary.locator("#username").fill(fixture.otherUsername); await secondary.locator("#password").fill(manifest.AIOFFICE_OWNER_PASSWORD);
    const otherCallback = secondary.waitForResponse(response => response.url().startsWith(app + "/api/local/session/oidc/callback?"));
    await secondary.locator("#kc-login").click(); proof((await otherCallback).status() === 303);
    await secondary.getByRole("button", { name: "Đăng xuất", exact: true }).waitFor();
    const current = JSON.parse((await get(secondary, "/api/local/session" + query)).text); proof(current.userId === fixture.otherUserId && !current.roles.includes("admin"));
    proof((await get(secondary, savedPath + query)).status === 404);
    const otherList = await get(secondary, "/api/local/tasks/intents" + query + "&offset=0&limit=25"), other = JSON.parse(otherList.text);
    proof(otherList.status === 200 && other.items.length === 25 && !otherList.text.includes(question) && guid(other.items[0].operationId));
    proof((await get(page, `/api/local/tasks/intents/${other.items[0].operationId}` + query)).status === 404);
    await switchCompany(secondary, fixture.companyId);
    const admin = JSON.parse((await get(secondary, `/api/local/session?companyId=${fixture.companyId}`)).text); proof(admin.roles.includes("admin"));
    proof((await get(secondary, savedPath + `?companyId=${fixture.companyId}`)).status === 404);
    console.log("PASS actual two CodeS256 users and companies: reciprocal owner404 including administrator, no foreign intent content");

    stage("held-private-intent-company-switch");
    const lateQuestion = "Yêu cầu riêng đang khôi phục 😀 �", lateOp = randomUUID();
    const created = await page.evaluate(async ({ query, lateOp, source, lateQuestion }) => {
      const response = await fetch("/api/local/tasks/intents" + query, { method: "POST", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ operationId: lateOp, dataSourceId: source, question: lateQuestion }) }); return response.status;
    }, { query, lateOp, source, lateQuestion }); proof(created === 200);
    const baseline = snapshot(), readCount = sent.length;
    await page.getByRole("button", { name: "Công việc", exact: true }).click();
    await page.getByRole("button", { name: "Lấy yêu cầu đã lưu " + lateQuestion, exact: true }).waitFor();
    heldPattern = `**/api/local/tasks/intents/${lateOp}?companyId=${company}`;
    let captured, finished, failed = false, attempted = false;
    const held = new Promise(resolve => { release = resolve; }), reached = new Promise(resolve => { captured = resolve; });
    const delivered = new Promise(resolve => { finished = resolve; });
    await page.route(heldPattern, async route => {
      try { const response = await route.fetch({ timeout: 15_000 }); proof(response.status() === 200); captured();
        await bounded(held, 30_000); attempted = true; await route.fulfill({ response }).catch(() => {});
      } catch { failed = true; captured(); await route.abort().catch(() => {}); } finally { finished(); }
    });
    await page.getByRole("button", { name: "Lấy yêu cầu đã lưu " + lateQuestion, exact: true }).click();
    await bounded(reached); proof(!failed); await switchCompany(page, fixture.companyId, "held-private-intent-switch-away"); release(); await bounded(delivered);
    await page.unroute(heldPattern); heldPattern = null;
    proof(attempted && !failed && await page.getByText(lateQuestion, { exact: true }).count() === 0 && sent.length === readCount);
    proof((await get(page, `/api/local/tasks/intents/${lateOp}?companyId=${fixture.companyId}`)).status === 404);
    await switchCompany(page, company, "held-private-intent-switch-back"); await page.getByRole("button", { name: "Công việc", exact: true }).click();
    await page.getByRole("button", { name: "Lấy yêu cầu đã lưu " + lateQuestion, exact: true }).waitFor(); equal(snapshot(), baseline);
    console.log("PASS actual successful private intent held across provider company switch is discarded, restored GET-only recovery preserves all effect and intent bytes");
    stage("external-member-loss-private-clear-restored-recovery");
    try {
      sql(`USE AIOfficeLocal; UPDATE aioffice.CompanyMemberships SET IsActive=0 WHERE ${scope} AND UserId='${owner}';`);
      await page.getByRole("button", { name: "Tải lại yêu cầu", exact: true }).click();
      await page.getByRole("button", { name: "Đăng nhập doanh nghiệp", exact: true }).waitFor();
      proof(await page.getByText(lateQuestion, { exact: true }).count() === 0
        && await page.getByRole("region", { name: "Yêu cầu đã lưu", exact: true }).count() === 0 && sent.length === readCount);
    } finally { sql(`USE AIOfficeLocal; UPDATE aioffice.CompanyMemberships SET IsActive=1 WHERE ${scope} AND UserId='${owner}';`); }
    const restored = page.waitForResponse(response => response.url().startsWith(app + "/api/local/session/oidc/callback?"));
    await page.getByRole("button", { name: "Đăng nhập doanh nghiệp", exact: true }).click(); proof((await restored).status() === 303);
    await page.getByRole("button", { name: "Đăng xuất", exact: true }).waitFor(); await page.getByRole("button", { name: "Công việc", exact: true }).click();
    await page.getByRole("button", { name: "Lấy yêu cầu đã lưu " + lateQuestion, exact: true }).waitFor();
    proof(sent.length === readCount); equal(snapshot(), baseline);
    console.log("PASS shipping owner recovery clears private data after external membership loss and restores GET-only under a new issued session with unchanged durable bytes");
    await page.getByRole("button", { name: "Trợ lý AI", exact: true }).first().click();
  } finally {
    await page.evaluate(() => window.__aiofficeOwnedSubmissionLifecycle?.dispose()).catch(() => {});
    page.off("request", observe); release?.(); if (heldPattern) await page.unroute(heldPattern).catch(() => {});
    coreReplyFault.disarm(); if (secondaryContext) await secondaryContext.close();
  }
}
