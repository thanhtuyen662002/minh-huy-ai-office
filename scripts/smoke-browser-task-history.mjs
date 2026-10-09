// Shipping archive UI against the owned disposable SQL and two Code/S256 sessions.
import { resolve, join } from "node:path";
import { readFile, mkdir } from "node:fs/promises";

export async function verifyTaskHistory({ directory, manifest, browser, ownerPage, ownerContext, sql, app, identity, setStage }) {
  const requireProof = condition => { if (!condition) throw new Error("Task history browser proof failed."); };
  requireProof(process.env.CI === "true" && process.env.GITHUB_ACTIONS === "true" && process.env.RUNNER_TEMP
    && resolve(directory) === join(resolve(process.env.RUNNER_TEMP), "aioffice-local")
    && app === "http://127.0.0.1:3000" && identity === "http://127.0.0.1:8081");
  const stage = name => setStage("history-" + name);
  async function bounded(promise, milliseconds = 15_000) {
    let timer;
    try { return await Promise.race([promise, new Promise((_, reject) => { timer = setTimeout(() => reject(new Error("Task history browser proof failed.")), milliseconds); })]); }
    finally { clearTimeout(timer); }
  }
  const guid = value => typeof value === "string" && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(value);
  stage("owned-fixture-read");
  const bytes = await readFile(join(directory, "history-fixture.json")); requireProof(bytes.length <= 4096);
  stage("owned-fixture-json");
  const fixture = JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(bytes));
  stage("owned-fixture-shape");
  requireProof(Object.keys(fixture).sort().join(",") === "companyId,otherUserId,otherUsername,taskIds"
    && guid(fixture.companyId) && guid(fixture.otherUserId) && /^archive-proof-[0-9a-f]{32}$/.test(fixture.otherUsername)
    && Array.isArray(fixture.taskIds) && fixture.taskIds.length === 4 && fixture.taskIds.every(guid)
    && new Set(fixture.taskIds).size === 4);
  const originalCompany = manifest.AIOFFICE_COMPANY_ID, tenant = manifest.AIOFFICE_TENANT_ID, owner = manifest.AIOFFICE_USER_ID;
  stage("owned-fixture-identities");
  requireProof([originalCompany, tenant, owner].every(guid) && fixture.companyId !== originalCompany && fixture.otherUserId !== owner);
  const selected = fixture.companyId, query = `?companyId=${selected}`, scope = `TenantId='${tenant}' AND CompanyId='${selected}'`;
  const ownerMember = scope + ` AND UserId='${owner}'`;
  const question = "Tồn kho 😀 �", answer = "Kết quả đã lưu 😀 �";
  const get = (page, path) => page.evaluate(async path => {
    const response = await fetch(path, { cache: "no-store" });
    return { status: response.status, cache: response.headers.get("cache-control"), text: await response.text() };
  }, path);
  const cookie = async context => (await context.cookies(app)).find(item => item.name === "aioffice_browser_session")?.value;
  async function switchCompany(page, target) {
    const callback = page.waitForResponse(response => response.url().startsWith(app + "/api/local/session/oidc/callback?"));
    const authorization = page.waitForRequest(request => request.url().startsWith(identity + "/realms/aioffice-local/protocol/openid-connect/auth?"));
    await page.getByRole("combobox", { name: "Chuyển công ty", exact: true }).selectOption(target);
    const request = new URL((await authorization).url()); requireProof(request.searchParams.get("response_type") === "code"
      && request.searchParams.get("code_challenge_method") === "S256" && request.searchParams.get("code_challenge"));
    requireProof((await callback).status() === 303);
    await page.getByRole("button", { name: "Đăng xuất", exact: true }).waitFor();
    const context = await get(page, `/api/local/session?companyId=${target}`); requireProof(context.status === 200 && JSON.parse(context.text).companyId === target);
  }
  async function openHistory(page) {
    await page.getByRole("button", { name: "Công việc", exact: true }).click();
    await page.getByRole("region", { name: "Công việc của tôi", exact: true }).waitFor();
    await page.getByRole("cell", { name: question, exact: true }).first().waitFor();
  }
  async function openCompleted(page) {
    const row = page.getByRole("row").filter({ has: page.getByRole("cell", { name: "Hoàn thành", exact: true }) });
    await row.getByRole("button", { name: "Xem công việc " + question, exact: true }).click();
    await page.getByText(answer, { exact: true }).waitFor();
    requireProof(await page.getByRole("article", { name: "Chi tiết công việc", exact: true }).getByText("Mã công việc: " + fixture.taskIds[0], { exact: true }).count() === 1);
  }
  const tables = { Tasks: "TenantId,CompanyId,Id", TaskSteps: "TenantId,CompanyId,TaskId,Id",
    TaskEvents: "TenantId,CompanyId,TaskId,Sequence", TaskCheckpoints: "TenantId,CompanyId,TaskId,StepId,Version",
    TaskStepExecutions: "TenantId,CompanyId,TaskId,StepId", TaskDispatches: "TenantId,CompanyId,TaskId,StepId,MessageId",
    Users: "TenantId,Id", RoleAssignments: "TenantId,CompanyId,UserId,RoleKey", CompanyMemberships: "TenantId,CompanyId,UserId",
    CompanyMembershipAccessAudits: "TenantId,CompanyId,Id", CompanyAdministratorAudits: "TenantId,CompanyId,Id", CustomerAiCreditSettlements: "SettlementId" };
  const snapshot = () => Object.entries(tables).map(([table, order], index) => {
    stage("owned-fingerprint-" + index);
    const value = sql(`USE AIOfficeLocal; SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',
      CONVERT(varbinary(max),(SELECT * FROM aioffice.${table} ORDER BY ${order} FOR JSON PATH))),2);`);
    if (!/^[0-9A-F]{64}$/.test(value)) {
      // Fixed table index and bounded classification only: never emit durable
      // JSON, hash content, SQL text, private identifiers or CLI diagnostics.
      stage(`owned-fingerprint-${index}-length-${Math.min(value.length, 999)}-${value === "NULL" ? "null" : /^[0-9a-fA-F]*$/.test(value) ? "hex" : "nonhex"}`);
      requireProof(false);
    }
    return value;
  });
  stage("owned-fixture-snapshots");
  const before = snapshot(); requireProof(before.every(value => /^[0-9A-F]{64}$/.test(value)));
  stage("owned-second-browser-context");
  const secondaryContext = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
  let held, release, fulfilled, routePattern;
  try {
    stage("shipping-owner-list-detail"); await switchCompany(ownerPage, selected); await openHistory(ownerPage);
    for (const name of ["Hoàn thành", "Chờ xử lý", "Thất bại"]) requireProof(await ownerPage.getByRole("cell", { name, exact: true }).count() === 1);
    await openCompleted(ownerPage);
    const ownerList = await get(ownerPage, "/api/local/tasks" + query); requireProof(ownerList.status === 200 && ownerList.cache === "no-store");
    requireProof(JSON.parse(ownerList.text).items.length === 3 && JSON.parse(ownerList.text).items.every(item => fixture.taskIds.slice(0, 3).includes(item.taskId)));
    requireProof((await get(ownerPage, `/api/local/tasks/${fixture.taskIds[3]}/history` + query)).status === 404);
    for (const status of ["Chờ xử lý", "Thất bại"]) {
      await ownerPage.getByRole("row").filter({ has: ownerPage.getByRole("cell", { name: status, exact: true }) })
        .getByRole("button", { name: "Xem công việc " + question, exact: true }).click();
      await ownerPage.getByRole("article", { name: "Chi tiết công việc", exact: true }).waitFor();
      requireProof(await ownerPage.getByText(answer, { exact: true }).count() === 0);
      await ownerPage.getByText(status === "Thất bại" ? /Công việc không hoàn thành/ : /Công việc chưa hoàn thành/).waitFor();
    }
    stage("reload-and-new-issued-session-recovery"); await ownerPage.reload(); await openHistory(ownerPage); await openCompleted(ownerPage);
    const oldSid = await cookie(ownerContext); requireProof(oldSid);
    await ownerPage.getByRole("button", { name: "Đăng xuất", exact: true }).click();
    const callback = ownerPage.waitForResponse(response => response.url().startsWith(app + "/api/local/session/oidc/callback?"));
    await ownerPage.getByRole("button", { name: "Đăng nhập doanh nghiệp", exact: true }).click();
    requireProof((await callback).status() === 303); await ownerPage.getByRole("button", { name: "Đăng xuất", exact: true }).waitFor();
    requireProof((await cookie(ownerContext)) && (await cookie(ownerContext)) !== oldSid);
    await openHistory(ownerPage); await openCompleted(ownerPage);
    console.log("PASS actual shipping owner history completed/pending/failed/detail, reload and new issued-session recovery without task rerun");

    stage("second-provider-code-s256-owner-isolation");
    const secondary = await secondaryContext.newPage(); secondary.setDefaultTimeout(15_000); secondary.setDefaultNavigationTimeout(20_000);
    await secondary.goto(app + query);
    const otherAuth = secondary.waitForRequest(request => request.url().startsWith(identity + "/realms/aioffice-local/protocol/openid-connect/auth?"));
    await secondary.getByRole("button", { name: "Đăng nhập doanh nghiệp", exact: true }).click();
    const authorization = new URL((await otherAuth).url()); requireProof(authorization.searchParams.get("code_challenge_method") === "S256"
      && authorization.searchParams.get("response_type") === "code");
    await secondary.locator("#username").waitFor({ state: "visible" }); requireProof(new URL(secondary.url()).origin === identity);
    const otherCallback = secondary.waitForResponse(response => response.url().startsWith(app + "/api/local/session/oidc/callback?"));
    await secondary.locator("#username").fill(fixture.otherUsername); await secondary.locator("#password").fill(manifest.AIOFFICE_OWNER_PASSWORD);
    await secondary.locator("#kc-login").click(); requireProof((await otherCallback).status() === 303);
    await secondary.getByRole("button", { name: "Đăng xuất", exact: true }).waitFor();
    const otherCurrent = await get(secondary, "/api/local/session" + query), otherSid = await cookie(secondaryContext);
    requireProof(otherCurrent.status === 200 && JSON.parse(otherCurrent.text).userId === fixture.otherUserId
      && JSON.parse(otherCurrent.text).roles.includes("admin") && otherSid && otherSid !== await cookie(ownerContext));
    await secondary.getByRole("button", { name: "Công việc", exact: true }).click(); await secondary.getByRole("cell", { name: "Other owner task", exact: true }).waitFor();
    const otherList = await get(secondary, "/api/local/tasks" + query); requireProof(otherList.status === 200 && JSON.parse(otherList.text).items.length === 1
      && JSON.parse(otherList.text).items[0].taskId === fixture.taskIds[3] && !otherList.text.includes(question));
    requireProof((await get(secondary, `/api/local/tasks/${fixture.taskIds[0]}/history` + query)).status === 404);
    await secondary.getByRole("button", { name: "Xem công việc Other owner task", exact: true }).click(); await secondary.getByText("Other owner saved answer", { exact: true }).waitFor();
    requireProof(await secondary.getByText(answer, { exact: true }).count() === 0);
    console.log("PASS actual two Code/S256 users: administrator archive remains owner-only with reciprocal404 and isolated saved answers");

    stage("late-private-detail-company-switch-fence");
    routePattern = `**/api/local/tasks/${fixture.taskIds[0]}/history?companyId=${selected}`;
    held = new Promise(resolve => { release = resolve; }); let captured;
    const reached = new Promise(resolve => { captured = resolve; });
    let finished, capturedFailure = false, attempted = false;
    fulfilled = new Promise(resolve => { finished = resolve; });
    await ownerPage.route(routePattern, async route => {
      try {
        const response = await route.fetch({ timeout: 15_000 }); requireProof(response.status() === 200); captured(); await bounded(held, 30_000);
        attempted = true;
        try { await route.fulfill({ response }); } catch { /* Generation cancellation can discard the old transport. */ }
      } catch {
        capturedFailure = true; captured(); await route.abort().catch(() => {});
      } finally { finished(); }
    });
    await ownerPage.getByRole("button", { name: "Cập nhật trạng thái", exact: true }).click(); await bounded(reached);
    requireProof(!capturedFailure);
    await switchCompany(ownerPage, originalCompany); release(); await bounded(fulfilled); await ownerPage.unroute(routePattern);
    requireProof(attempted && !capturedFailure);
    requireProof(await ownerPage.getByText(answer, { exact: true }).count() === 0 && await ownerPage.getByRole("region", { name: "Công việc của tôi", exact: true }).count() === 0);
    requireProof((await get(ownerPage, `/api/local/tasks/${fixture.taskIds[0]}/history?companyId=${originalCompany}`)).status === 404);
    await switchCompany(ownerPage, selected); await openHistory(ownerPage); await openCompleted(ownerPage);
    stage("external-membership-loss-private-clear-and-restore");
    sql(`USE AIOfficeLocal; UPDATE aioffice.CompanyMemberships SET IsActive=0 WHERE ${ownerMember};`);
    await ownerPage.getByRole("button", { name: "Tải lại công việc", exact: true }).click(); await ownerPage.getByRole("button", { name: "Đăng nhập doanh nghiệp", exact: true }).waitFor();
    requireProof(await ownerPage.getByText(answer, { exact: true }).count() === 0 && await ownerPage.getByText(question, { exact: true }).count() === 0);
    sql(`USE AIOfficeLocal; UPDATE aioffice.CompanyMemberships SET IsActive=1 WHERE ${ownerMember};`);
    const restored = ownerPage.waitForResponse(response => response.url().startsWith(app + "/api/local/session/oidc/callback?"));
    await ownerPage.getByRole("button", { name: "Đăng nhập doanh nghiệp", exact: true }).click(); requireProof((await restored).status() === 303);
    await ownerPage.getByRole("button", { name: "Đăng xuất", exact: true }).waitFor(); await openHistory(ownerPage); await openCompleted(ownerPage);
    requireProof(snapshot().every((value, index) => value === before[index]));
    console.log("PASS shipping late body/company-switch fence, external membership loss/private clearing and restored archive with unchanged tasks/dispatches/identity/roles/audits/credit settlements");
    stage("sanitized-artifact"); const artifact = join(resolve(process.env.RUNNER_TEMP), "aioffice-browser-proof"); await mkdir(artifact, { recursive: true });
    await ownerPage.screenshot({ path: join(artifact, "task-history.png"), fullPage: true });
    await switchCompany(ownerPage, originalCompany);
  } finally {
    release?.();
    if (routePattern) await ownerPage.unroute(routePattern).catch(() => {});
    sql(`USE AIOfficeLocal; UPDATE aioffice.CompanyMemberships SET IsActive=1 WHERE ${ownerMember};`);
    await secondaryContext.close();
  }
}
