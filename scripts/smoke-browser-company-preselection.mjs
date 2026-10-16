import { readFile, mkdir } from "node:fs/promises";
import { resolve, join } from "node:path";
import { finishCompanyDiscovery } from "./owned-browser-company-discovery.mjs";

export async function verifyCompanyPreselection({ directory, manifest, browser, sql, app, identity, setStage }) {
  const requireProof = (value) => { if (!value) throw new Error("Owned provider company proof failed."); };
  requireProof(process.env.CI === "true" && process.env.GITHUB_ACTIONS === "true" && process.env.RUNNER_TEMP
    && resolve(directory) === join(resolve(process.env.RUNNER_TEMP), "aioffice-local")
    && app === "http://127.0.0.1:3000" && identity === "http://127.0.0.1:8081");
  const stage = (name) => setStage("company-discovery-" + name);
  const guid = (value) => typeof value === "string" && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(value);
  stage("fixture"); const bytes = await readFile(join(directory, "company-signin-fixture.json")); requireProof(bytes.length <= 8192);
  const fixture = JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(bytes));
  requireProof(Object.keys(fixture).sort().join(",") === "companies,identities,sources"
    && fixture.companies.length === 2 && fixture.companies.every(guid) && new Set(fixture.companies).size === 2
    && fixture.sources.length === 2 && fixture.sources.every(guid) && fixture.identities.length === 3);
  const tenant = manifest.AIOFFICE_TENANT_ID, defaultCompany = manifest.AIOFFICE_COMPANY_ID;
  requireProof(guid(tenant) && guid(defaultCompany) && !fixture.companies.includes(defaultCompany));
  for (const [index, user] of fixture.identities.entries()) {
    requireProof(Object.keys(user).sort().join(",") === "companies,userId,username" && guid(user.userId)
      && /^company-proof-[0-9a-f]{32}$/.test(user.username)
      && JSON.stringify(user.companies) === JSON.stringify(index === 0 ? fixture.companies.slice(0, 1) : index === 1 ? fixture.companies : []));
    requireProof(sql(`USE AIOfficeLocal; SELECT COUNT(*) FROM aioffice.CompanyMemberships WHERE TenantId='${tenant}' AND CompanyId='${defaultCompany}' AND UserId='${user.userId}';`) === "0");
  }
  const tables = { Companies: "TenantId,Id", Users: "TenantId,Id", CompanyMemberships: "TenantId,CompanyId,UserId",
    RoleAssignments: "TenantId,CompanyId,UserId,RoleKey", CompanyMembershipAccessAudits: "TenantId,CompanyId,Id",
    CompanyAdministratorAudits: "TenantId,CompanyId,Id", DataSources: "TenantId,CompanyId,Id", DataSourceSecretBindings: "TenantId,CompanyId,Id",
    Tasks: "TenantId,CompanyId,Id", TaskSteps: "TenantId,CompanyId,TaskId,Id", TaskEvents: "TenantId,CompanyId,TaskId,Sequence",
    TaskCheckpoints: "TenantId,CompanyId,TaskId,StepId,Version", TaskDispatches: "TenantId,CompanyId,TaskId,StepId,MessageId",
    TaskStepExecutions: "TenantId,CompanyId,TaskId,StepId", CustomerAiCreditSettlements: "SettlementId" };
  const snapshot = () => Object.entries(tables).map(([table, order]) => {
    const hash = sql(`USE AIOfficeLocal; SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),
      COALESCE((SELECT * FROM aioffice.${table} ORDER BY ${order} FOR JSON PATH,INCLUDE_NULL_VALUES),N'[]'))),2);`);
    requireProof(/^[0-9A-F]{64}$/.test(hash)); return hash;
  });
  const before = snapshot(), contexts = [];
  const get = (page, path) => page.evaluate(async (path) => {
    const response = await fetch(path, { cache: "no-store" });
    return { status: response.status, text: await response.text(), cache: response.headers.get("cache-control") };
  }, path);
  const sid = async (context) => (await context.cookies(app)).find((item) => item.name === "aioffice_browser_session")?.value;
  async function login(user, { context, page }, password = true) {
    await page.goto(app); const callback = page.waitForResponse((reply) => reply.url().startsWith(app + "/api/local/session/oidc/callback?"));
    callback.catch(() => {});
    const auth = page.waitForRequest((request) => request.url().startsWith(identity + "/realms/aioffice-local/protocol/openid-connect/auth?")); auth.catch(() => {});
    await page.getByRole("button", { name: "Đăng nhập doanh nghiệp", exact: true }).click();
    const url = new URL((await auth).url()); requireProof(url.searchParams.get("response_type") === "code" && url.searchParams.get("code_challenge_method") === "S256");
    if (password) {
      await page.locator("#username").waitFor({ state: "visible" }); requireProof(new URL(page.url()).origin === identity);
      await page.locator("#username").fill(user.username); await page.locator("#password").fill(manifest.AIOFFICE_OWNER_PASSWORD);
      await page.locator("#kc-login").click();
    }
    const result = await callback; return result;
  }
  async function fresh() {
    const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } }); contexts.push(context);
    const page = await context.newPage(); page.setDefaultTimeout(15_000); page.setDefaultNavigationTimeout(20_000);
    return { context, page };
  }
  async function session(user, current, company) {
    await current.page.getByRole("button", { name: "Đăng xuất", exact: true }).waitFor();
    const response = await get(current.page, "/api/local/session?companyId=" + company);
    requireProof(response.status === 200 && response.cache === "no-store"); const authority = JSON.parse(response.text);
    requireProof(authority.companyId === company && authority.tenantId === tenant && authority.userId === user.userId && authority.roles.includes("viewer"));
    requireProof((await get(current.page, "/api/local/session?companyId=" + defaultCompany)).status === 401);
    requireProof((await get(current.page, "/api/local/data-sources?companyId=" + defaultCompany)).status === 401);
    await current.page.reload(); await current.page.getByRole("button", { name: "Đăng xuất", exact: true }).waitFor();
    requireProof((await get(current.page, "/api/local/session?companyId=" + company)).status === 200);
    const source = current.page.getByRole("combobox", { name: "Nguồn dữ liệu", exact: true }); await source.waitFor();
    await source.locator(`option[value="${fixture.sources[fixture.companies.indexOf(company)]}"]`).waitFor({ state: "attached" });
    await current.page.getByRole("button", { name: "Công việc", exact: true }).click();
    requireProof((await get(current.page, "/api/local/tasks?companyId=" + company)).status === 200);
  }
  const membership = `TenantId='${tenant}' AND CompanyId='${fixture.companies[1]}' AND UserId='${fixture.identities[1].userId}'`;
  try {
    stage("one-nondefault"); const single = await fresh(); const singleReply = await login(fixture.identities[0], single);
    requireProof(singleReply.status() === 303 && singleReply.headers().location === app + "/");
    await session(fixture.identities[0], single, fixture.companies[0]); requireProof(await sid(single.context));
    requireProof((await get(single.page, "/api/local/session?companyId=" + fixture.companies[1])).status === 401);
    stage("multiple-no-authority"); const multi = await fresh(); const multiReply = await login(fixture.identities[1], multi);
    requireProof(multiReply.status() === 303 && multiReply.headers().location === app + "/sign-in/company");
    await multi.page.getByRole("heading", { name: "Chọn công ty để tiếp tục", exact: true }).waitFor(); requireProof(!await sid(multi.context));
    for (const company of [defaultCompany, ...fixture.companies]) requireProof((await get(multi.page, "/api/local/session?companyId=" + company)).status === 401);
    requireProof(await multi.page.locator('textarea[name="question"]').count() === 0);
    stage("multiple-fresh-selection"); await finishCompanyDiscovery(multi.page, app, fixture.companies[1], multiReply);
    await session(fixture.identities[1], multi, fixture.companies[1]); requireProof(await sid(single.context) !== await sid(multi.context));
    requireProof((await get(multi.page, "/api/local/session?companyId=" + fixture.companies[0])).status === 401);
    stage("external-revoke"); sql(`USE AIOfficeLocal; UPDATE aioffice.CompanyMemberships SET IsActive=0 WHERE ${membership};`);
    await multi.page.evaluate(() => window.dispatchEvent(new Event("focus")));
    await multi.page.getByRole("button", { name: "Đăng nhập doanh nghiệp", exact: true }).waitFor();
    requireProof(await multi.page.locator('textarea[name="question"]').count() === 0);
    stage("external-restore"); sql(`USE AIOfficeLocal; UPDATE aioffice.CompanyMemberships SET IsActive=1 WHERE ${membership};`);
    const restored = await login(fixture.identities[1], multi, false); await finishCompanyDiscovery(multi.page, app, fixture.companies[1], restored);
    await session(fixture.identities[1], multi, fixture.companies[1]);
    stage("no-choice"); const none = await fresh(); const denied = await login(fixture.identities[2], none);
    requireProof(denied.status() === 403 && !await sid(none.context));
    requireProof((await denied.text()).includes("quản trị viên") && denied.headers()["cache-control"] === "no-store");
    stage("unchanged-sql-graphs"); requireProof(snapshot().every((value, index) => value === before[index]));
    const artifact = join(resolve(process.env.RUNNER_TEMP), "aioffice-browser-proof"); await mkdir(artifact, { recursive: true });
    await multi.page.screenshot({ path: join(artifact, "company-signin.png"), fullPage: true });
    console.log("PASS actual two fresh Code/S256 nondefault identities, one/multiple authorized choices, no preselection SID/workspace, reload/source/history and external revoke/restore");
    console.log("PASS actual zero-company provider identity denial and unchanged identity/role/member/task/dispatch/audit/settlement graphs");
  } finally {
    sql(`USE AIOfficeLocal; UPDATE aioffice.CompanyMemberships SET IsActive=1 WHERE ${membership};`);
    for (const context of contexts) await context.close().catch(() => {});
  }
}
