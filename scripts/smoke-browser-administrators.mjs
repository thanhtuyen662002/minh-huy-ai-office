// Actual shipping controls and two Code/S256 sessions, restricted to owned CI.
// Never print provider bodies, passwords, authorization codes or cookies.
import { randomUUID } from "node:crypto";
import { resolve, join } from "node:path";

export async function verifyCompanyAdministrators({ directory, manifest, browser, ownerPage, ownerContext, sql,
  app, identity, company, tenant, owner, setStage }) {
  const requireProof = condition => { if (!condition) throw new Error("Administrator browser proof failed."); };
  requireProof(process.env.CI === "true" && process.env.GITHUB_ACTIONS === "true" && process.env.RUNNER_TEMP
    && resolve(directory) === join(resolve(process.env.RUNNER_TEMP), "aioffice-local")
    && app === "http://127.0.0.1:3000" && identity === "http://127.0.0.1:8081");
  const guid = value => typeof value === "string" && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(value);
  for (const id of [company, tenant, owner]) requireProof(guid(id));
  const stage = name => setStage("administrator-" + name);
  stage("owned-identity-fixture");
  const boundedIdentity = async (path, init) => {
    const response = await fetch(identity + path, { ...init, signal: AbortSignal.timeout(15_000) });
    requireProof(response.ok);
    const text = await response.text(); requireProof(Buffer.byteLength(text) <= 65536);
    return text ? JSON.parse(text) : null;
  };
  const token = await boundedIdentity("/realms/master/protocol/openid-connect/token", { method: "POST",
    headers: { "Content-Type": "application/x-www-form-urlencoded" }, body: new URLSearchParams({
      client_id: "admin-cli", grant_type: "password", username: "bootstrap-admin", password: manifest.AIOFFICE_IDENTITY_ADMIN_PASSWORD,
    }) });
  requireProof(typeof token.access_token === "string");
  const username = "browser-administrator-" + randomUUID().replaceAll("-", "");
  const adminHeaders = { Authorization: "Bearer " + token.access_token, "Content-Type": "application/json" };
  await boundedIdentity("/admin/realms/aioffice-local/users", { method: "POST", headers: adminHeaders, body: JSON.stringify({
    username, enabled: true, emailVerified: true, firstName: "Disposable", lastName: "Administrator",
    email: username + "@example.invalid", credentials: [{ type: "password", value: manifest.AIOFFICE_OWNER_PASSWORD, temporary: false }],
  }) });
  const identities = await boundedIdentity("/admin/realms/aioffice-local/users?username=" + username + "&exact=true", { headers: adminHeaders });
  requireProof(Array.isArray(identities) && identities.length === 1 && identities[0].username === username && guid(identities[0].id));
  const member = randomUUID(), name = "Disposable browser administrator";
  const scope = `TenantId='${tenant}' AND CompanyId='${company}'`;
  const memberScope = scope + ` AND UserId='${member}'`;
  const auditScope = scope + ` AND TargetUserId='${member}'`;
  // Keep the fixture and immutable history for the owned volume lifetime.
  sql(`USE AIOfficeLocal; SET XACT_ABORT ON; BEGIN TRANSACTION;
    INSERT aioffice.Users(TenantId,Id,IdentityProvider,Subject,DisplayName)
      VALUES('${tenant}','${member}',N'local-keycloak',N'${identities[0].id}',N'${name}');
    INSERT aioffice.CompanyMemberships(TenantId,CompanyId,UserId) VALUES('${tenant}','${company}','${member}');
    INSERT aioffice.RoleAssignments(TenantId,CompanyId,UserId,RoleKey) VALUES('${tenant}','${company}','${member}',N'viewer');
    COMMIT TRANSACTION;`);
  const fingerprint = (table, predicate, order, columns = "*") => sql(`USE AIOfficeLocal; SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',
    (SELECT ${columns} FROM aioffice.${table} WHERE ${predicate} ORDER BY ${order} FOR JSON PATH,INCLUDE_NULL_VALUES)),2);`);
  const preserved = [
    ["Users", `TenantId='${tenant}' AND Id IN ('${owner}','${member}')`, "Id"],
    ["RoleAssignments", scope + ` AND (UserId='${owner}' OR (UserId='${member}' AND RoleKey COLLATE Latin1_General_100_BIN2<>N'admin'))`, "UserId,RoleKey"],
    ["CompanyMemberships", memberScope, "UserId", "TenantId,CompanyId,UserId,IsActive,CreatedAtUtc"],
    ["Tasks", scope, "Id"],
  ];
  const before = preserved.map(item => fingerprint(...item));
  requireProof(before.every(item => /^[0-9A-F]{64}$/.test(item))
    && Number(sql(`USE AIOfficeLocal; SELECT COUNT(*) FROM aioffice.Tasks WHERE ${scope};`)) > 0);
  const path = `/api/local/company/members/${member}/administrator?companyId=${company}`;
  const query = `?companyId=${company}`;
  const state = () => sql(`USE AIOfficeLocal; SELECT CONCAT(CONVERT(int,IsActive),N':',Version,N':',
    (SELECT COUNT(*) FROM aioffice.RoleAssignments WHERE ${memberScope} AND RoleKey COLLATE Latin1_General_100_BIN2=N'admin' AND DATALENGTH(RoleKey)=10))
    FROM aioffice.CompanyMemberships WHERE ${memberScope};`);
  const auditCount = () => sql(`USE AIOfficeLocal; SELECT COUNT(*) FROM aioffice.CompanyAdministratorAudits WHERE ${auditScope};`);
  const cookie = async context => (await context.cookies(app)).find(item => item.name === "aioffice_browser_session")?.value;
  const ownerSid = await cookie(ownerContext);
  const current = page => page.evaluate(async path => {
    const response = await fetch(path, { cache: "no-store" });
    return { status: response.status, body: await response.json(), cache: response.headers.get("cache-control") };
  }, "/api/local/session" + query);
  const mutate = (page, body) => page.evaluate(async ({ path, body }) => {
    const response = await fetch(path, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
    return { status: response.status, body: await response.json(), cache: response.headers.get("cache-control") };
  }, { path, body });
  const focus = page => page.evaluate(() => window.dispatchEvent(new Event("focus")));
  const button = administrator => ownerPage.getByRole("button", {
    name: `${administrator ? "Gỡ quyền quản trị" : "Cấp quyền quản trị"} ${name}`, exact: true,
  });
  await ownerPage.getByRole("button", { name: "Tải lại thành viên", exact: true }).click();
  await button(false).waitFor();
  const secondaryContext = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
  try {
    const secondary = await secondaryContext.newPage(); secondary.setDefaultTimeout(15_000); secondary.setDefaultNavigationTimeout(20_000);
    stage("secondary-provider-code-s256-sign-in");
    await secondary.goto(app);
    await secondary.getByRole("button", { name: "Đăng nhập doanh nghiệp", exact: true }).click();
    await secondary.locator("#username").waitFor({ state: "visible" });
    requireProof(new URL(secondary.url()).origin === identity);
    const callback = secondary.waitForResponse(response => response.url().startsWith(app + "/api/local/session/oidc/callback?"));
    await secondary.locator("#username").fill(username); await secondary.locator("#password").fill(manifest.AIOFFICE_OWNER_PASSWORD);
    await secondary.locator("#kc-login").click();
    requireProof((await callback).status() === 303);
    await secondary.getByRole("button", { name: "Đăng xuất", exact: true }).waitFor();
    const secondarySid = await cookie(secondaryContext), initial = await current(secondary);
    requireProof(secondarySid && secondarySid !== ownerSid && initial.status === 200 && initial.body.userId === member
      && initial.body.companyId === company && !initial.body.roles.includes("admin") && initial.cache === "no-store");
    requireProof(await secondary.getByRole("button", { name: "Thành viên", exact: true }).count() === 0);

    stage("shipping-grant-fresh-existing-sid");
    await button(false).click(); await button(true).waitFor();
    requireProof(state() === "1:2:1" && auditCount() === "1" && (await current(secondary)).body.roles.includes("admin")
      && await cookie(secondaryContext) === secondarySid);
    await focus(secondary);
    await secondary.getByRole("button", { name: "Thành viên", exact: true }).first().click();
    await secondary.getByRole("cell", { name, exact: true }).waitFor();
    requireProof(await secondary.getByRole("button", { name: `Gỡ quyền quản trị ${name}`, exact: true }).isDisabled());

    stage("shipping-remove-fresh-denial-private-control-clear");
    await button(true).click(); await button(false).waitFor();
    const revoked = await current(secondary);
    requireProof(state() === "1:3:0" && auditCount() === "2" && revoked.status === 200 && !revoked.body.roles.includes("admin"));
    const denied = await mutate(secondary, { operationId: randomUUID(), expectedVersion: "3", isAdministrator: true });
    requireProof(denied.status === 403 && denied.cache === "no-store" && state() === "1:3:0" && auditCount() === "2");
    await focus(secondary);
    await secondary.waitForFunction(() => ![...document.querySelectorAll("button")].some(item => item.textContent?.trim() === "Thành viên")
      && !document.querySelector('section[aria-label="Thành viên công ty"] tbody'));
    requireProof(await cookie(secondaryContext) === secondarySid);

    stage("shipping-committed-lost-grant-reply");
    let lostInput, committed = false;
    const loseReply = async route => {
      lostInput = route.request().postDataJSON();
      const response = await route.fetch(); committed = response.status() === 200;
      await route.abort("failed");
    };
    await ownerPage.route(app + path, loseReply);
    await button(false).click();
    await ownerPage.getByRole("button", { name: `Thử lại thao tác với ${name}`, exact: true }).waitFor();
    requireProof(committed && lostInput.expectedVersion === "3" && lostInput.isAdministrator && guid(lostInput.operationId)
      && state() === "1:4:1" && auditCount() === "3" && (await current(secondary)).body.roles.includes("admin"));
    await ownerPage.unroute(app + path, loseReply);
    const replayed = ownerPage.waitForResponse(response => response.url() === app + path && response.request().method() === "POST");
    await ownerPage.getByRole("button", { name: `Thử lại thao tác với ${name}`, exact: true }).click();
    const replay = await replayed;
    requireProof(replay.status() === 200 && replay.headers()["cache-control"] === "no-store" && !replay.headers()["set-cookie"]
      && JSON.stringify(replay.request().postDataJSON()) === JSON.stringify(lostInput));
    await button(true).waitFor(); requireProof(state() === "1:4:1" && auditCount() === "3");
    await focus(secondary); await secondary.getByRole("button", { name: "Thành viên", exact: true }).first().click();
    await secondary.getByRole("cell", { name, exact: true }).waitFor();
    await button(true).click(); await button(false).waitFor();
    requireProof(state() === "1:5:0" && auditCount() === "4" && !(await current(secondary)).body.roles.includes("admin"));
    const historical = await mutate(ownerPage, lostInput);
    requireProof(historical.status === 200 && historical.body.isAdministrator && historical.body.membershipVersion === "4"
      && state() === "1:5:0" && auditCount() === "4");
    await focus(secondary);
    await secondary.waitForFunction(() => !document.querySelector('section[aria-label="Thành viên công ty"] tbody')
      && ![...document.querySelectorAll("button")].some(item => item.textContent?.trim() === "Thành viên"));
    requireProof(await cookie(secondaryContext) === secondarySid);

    stage("shipping-role-stale-version-setup");
    sql(`USE AIOfficeLocal; UPDATE aioffice.CompanyMemberships SET Version=Version+1 WHERE ${memberScope};`);
    requireProof(state() === "1:6:0" && auditCount() === "4");
    const stale = ownerPage.waitForResponse(response => response.url() === app + path && response.request().method() === "POST");
    stage("shipping-role-stale-version-response");
    await button(false).click(); const conflict = await stale;
    requireProof(conflict.status() === 409 && conflict.request().postDataJSON().expectedVersion === "5");
    stage("shipping-role-stale409-private-clear");
    await ownerPage.getByRole("alert").waitFor();
    await ownerPage.locator('section[aria-label="Thành viên công ty"] tbody').waitFor({ state: "detached" });
    requireProof(await button(false).count() === 0 && await button(true).count() === 0);
    requireProof(state() === "1:6:0" && auditCount() === "4");
    stage("shipping-role-stale-version-reload");
    await ownerPage.getByRole("button", { name: "Tải lại thành viên", exact: true }).click();
    await button(false).waitFor(); await button(false).click(); await button(true).waitFor();
    requireProof(state() === "1:7:1" && auditCount() === "5");
    stage("shipping-role-restored-final-authority");
    await button(true).click(); await button(false).waitFor();
    requireProof(state() === "1:8:0" && auditCount() === "6" && !(await current(secondary)).body.roles.includes("admin")
      && await cookie(ownerContext) === ownerSid && await cookie(secondaryContext) === secondarySid);
    for (let index = 0; index < preserved.length; index++) {
      stage("preserved-" + preserved[index][0]);
      requireProof(fingerprint(...preserved[index]) === before[index]);
    }
    console.log("PASS actual Chromium administrator grant/remove, two provider sessions/fresh authority under unchanged SIDs, lost-response historical replay/stale409/private clearing and preserved identities/other roles/member state/tasks");
  } finally {
    await secondaryContext.close();
  }
}
