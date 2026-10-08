// Required real Chromium proof against only the owned disposable GitHub CI stack.
// No traces, provider bodies, credentials, tokens or private cookie values are logged.
import { chromium } from "playwright";
import { readFile, mkdir } from "node:fs/promises";
import { resolve, join } from "node:path";
import { spawnSync } from "node:child_process";
import { createHash, randomUUID } from "node:crypto";
import { setTimeout as delay } from "node:timers/promises";

let stage = "disposable-fixture-guard", browser, safeFailureLogs;
const requireProof = condition => { if (!condition) throw new Error("Browser proof failed."); };
const digest = value => createHash("sha256").update(value).digest("hex");
const guid = value => typeof value === "string" && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(value)
  && value !== "00000000-0000-0000-0000-000000000000";
try {
  const directory = resolve(process.argv[2] ?? "");
  requireProof(process.env.CI === "true" && process.env.GITHUB_ACTIONS === "true" && process.env.RUNNER_TEMP
    && directory === join(resolve(process.env.RUNNER_TEMP), "aioffice-local"));
  const manifest = JSON.parse(await readFile(join(directory, "installation.json"), "utf8"));
  for (const key of ["INSTALLATION", "TENANT", "COMPANY", "USER", "DATA_SOURCE"]) requireProof(guid(manifest[`AIOFFICE_${key}_ID`]));
  const compose = ["compose", "--env-file", join(directory, "local.env"), "-f", "compose.local.yaml"];
  const run = (args, env = process.env) => {
    const result = spawnSync("docker", [...compose, ...args], { encoding: "utf8", env, timeout: 90_000, maxBuffer: 8 * 1024 * 1024 });
    requireProof(result.status === 0); return result.stdout.trim();
  };
  const sql = text => run(["exec", "-T", "sql", "sh", "-c",
    'SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -I -b -m 1 -h -1 -W -Q "$1"',
    "sql", "SET NOCOUNT ON; " + text]);
  const flags = { ...process.env, AIOFFICE_BROWSER_OIDC_ENABLED: "true", AIOFFICE_LOCAL_UI_ENABLED: "false", AIOFFICE_BROWSER_CI_PROOF: "true" };
  const profile = JSON.parse(run(["config", "--format", "json"], flags));
  requireProof(profile.name === "aioffice-" + manifest.AIOFFICE_INSTALLATION_ID.replaceAll("-", ""));
  safeFailureLogs = () => {
    for (const line of run(["logs", "--no-color", "--tail", "30", "web"]).split("\n")) {
      const marker = line.match(/\[aioffice-browser-proof\] (configuration|origin|transaction|response|claim|exchange|signed-exchange|authority|publish|lifetime)(?: (OAUTH_INVALID_RESPONSE|OAUTH_RESPONSE_BODY_ERROR|OAUTH_INVALID_REQUEST|OAUTH_JWT_CLAIM_COMPARISON_FAILED|OAUTH_JWT_TIMESTAMP_CHECK_FAILED|OAUTH_KEY_SELECTION_FAILED|OAUTH_SIGNATURE_VERIFICATION_FAILED|OAUTH_UNSUPPORTED_OPERATION|OAUTH_INVALID_SERVER_METADATA))?$/);
      if (marker) console.error(`CI verification refusal: ${marker[1]}${marker[2] ? " " + marker[2] : ""}`);
    }
  };
  const service = profile.services.web;
  const settings = service.environment;
  requireProof(settings.AIOFFICE_BROWSER_OIDC_ENABLED === "true" && settings.AIOFFICE_LOCAL_UI_ENABLED === "false"
    && settings.AIOFFICE_BROWSER_OIDC_LOCAL_HTTP === "true");
  for (const spec of Object.values(profile.services)) for (const port of spec.ports ?? []) requireProof(port.host_ip === "127.0.0.1");
  const app = settings.AIOFFICE_BROWSER_OIDC_PUBLIC_ORIGIN;
  const issuer = settings.AIOFFICE_BROWSER_OIDC_ISSUER;
  const identity = new URL(issuer).origin;
  const callback = app + "/api/local/session/oidc/callback";
  requireProof(app === `http://127.0.0.1:${service.ports[0].published}`
    && issuer === `http://127.0.0.1:${profile.services.identity.ports[0].published}/realms/aioffice-local`);
  stage = "browser-mode-readiness";
  run(["up", "-d", "--no-deps", "--force-recreate", "web"], flags);
  let ready = false;
  for (let attempt = 0; attempt < 60; attempt++) {
    try { ready = (await fetch(app, { signal: AbortSignal.timeout(2000) })).status === 200; } catch { /* Bounded readiness. */ }
    if (ready) break; await delay(500);
  }
  requireProof(ready);
  browser = await chromium.launch({ headless: true, timeout: 15_000 });
  const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
  const page = await context.newPage(); page.setDefaultTimeout(15_000); page.setDefaultNavigationTimeout(20_000);
  const company = manifest.AIOFFICE_COMPANY_ID, tenant = manifest.AIOFFICE_TENANT_ID, user = manifest.AIOFFICE_USER_ID;
  const query = `?companyId=${company}`;
  const waitForSource = async () => {
    const selector = page.getByRole("combobox", { name: "Nguồn dữ liệu", exact: true });
    await selector.waitFor();
    // Native select options have no rendered box while the popup is closed.
    // Require the visible selector and exact owned option in its DOM instead.
    const option = selector.locator(`option[value="${manifest.AIOFFICE_DATA_SOURCE_ID}"]`);
    await option.waitFor({ state: "attached" });
    requireProof((await option.textContent()) === "Local sample ERP");
  };
  const current = () => page.evaluate(async path => {
    const response = await fetch(path, { cache: "no-store" });
    return { status: response.status, body: await response.json(), cache: response.headers.get("cache-control") };
  }, "/api/local/session" + query);
  const get = path => page.evaluate(async path => {
    const response = await fetch(path, { cache: "no-store" });
    return { status: response.status, text: await response.text(), cache: response.headers.get("cache-control") };
  }, path);
  const mutate = (path, method, body) => page.evaluate(async ({ path, method, body }) => {
    const response = await fetch(path, { method, headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
    let payload = null;
    try { payload = await response.json(); } catch { /* Core's forbidden response may have no body. */ }
    return { status: response.status, body: payload, cache: response.headers.get("cache-control") };
  }, { path, method, body });
  async function signIn(passwordRequired = false) {
    await page.goto(app);
    await page.getByRole("button", { name: "Đăng nhập doanh nghiệp", exact: true }).waitFor();
    requireProof(await page.locator('input[name="username"],input[name="password"]').count() === 0);
    const response = page.waitForRequest(request => request.url().startsWith(callback + "?"));
    await page.getByRole("button", { name: "Đăng nhập doanh nghiệp", exact: true }).click();
    if (passwordRequired) {
      stage = "provider-username-fields";
      await page.locator("#username").waitFor({ state: "visible" });
      requireProof(new URL(page.url()).origin === identity);
      await page.locator("#username").fill("owner");
      await page.locator("#password").fill(manifest.AIOFFICE_OWNER_PASSWORD);
      await page.locator("#kc-login").click();
    }
    stage = "verified-provider-callback";
    const callbackRequest = await response;
    const callbackStatus = (await callbackRequest.response())?.status();
    if (callbackStatus !== 303) console.error(`CI callback HTTP status: ${Number.isInteger(callbackStatus) ? callbackStatus : "unavailable"}`);
    requireProof(callbackStatus === 303);
    await page.waitForURL(url => url.origin === app && url.pathname === "/");
    stage = "authenticated-workspace-ready";
    await page.getByRole("button", { name: "Đăng xuất", exact: true }).waitFor();
    const authority = await current();
    requireProof(authority.status === 200 && authority.body.companyId === company && authority.body.tenantId === tenant
      && authority.body.userId === user && authority.cache === "no-store");
    return { url: callbackRequest.url(), cookie: (await callbackRequest.allHeaders()).cookie };
  }
  const cookie = async name => (await context.cookies(app)).find(item => item.name === name);
  stage = "provider-page-sign-in";
  const originalCallback = await signIn(true);
  const sid = await cookie("aioffice_browser_session"), binding = await cookie("aioffice_browser_binding");
  requireProof(sid && /^[A-Za-z0-9_-]{43}$/.test(sid.value) && binding && /^[A-Za-z0-9_-]{43}\.[A-Za-z0-9_-]{43}$/.test(binding.value));
  for (const value of [sid, binding]) requireProof(value.httpOnly && !value.secure && value.sameSite === "Lax" && value.path === "/");
  requireProof(!await cookie("aioffice_local_access_token") && !await cookie("aioffice_oidc_transaction"));
  const visibleCookies = await page.evaluate(() => document.cookie);
  requireProof(!visibleCookies.includes(sid.value) && !visibleCookies.includes(binding.value));
  console.log("PASS actual Chromium provider-page Code/S256 sign-in, scoped Core authority and opaque HttpOnly cookies");

  stage = "company-source-member-access";
  const sources = await get("/api/local/data-sources" + query);
  const members = await get("/api/local/company/members" + query);
  requireProof(sources.status === 200 && members.status === 200 && sources.cache === "no-store" && members.cache === "no-store");
  const source = JSON.parse(sources.text).find(item => item.id === manifest.AIOFFICE_DATA_SOURCE_ID);
  requireProof(source && guid(source.id) && source.allowRead && !source.allowWrite && source.isEnabled);
  requireProof(JSON.parse(members.text).items.some(item => item.userId === user));
  requireProof(!/access_token|accessToken|client_secret|connectionSecretReference|secretref:\/\//.test(sources.text + members.text));
  stage = "company-source-selector-ready";
  await waitForSource();
  stage = "company-member-panel-ready";
  await page.getByRole("button", { name: "Thành viên", exact: true }).first().click();
  await page.locator('section[aria-label="Thành viên công ty"] tbody tr').first().waitFor();
  const ownerMember = JSON.parse(members.text).items.find(item => item.userId === user);
  requireProof(await page.getByRole("button", { name: `Khóa quyền ${ownerMember.displayName}`, exact: true }).isDisabled());
  stage = "owned-browser-member-fixture";
  const memberId = randomUUID(), memberName = "Disposable browser member";
  const memberScope = `TenantId='${tenant}' AND CompanyId='${company}' AND UserId='${memberId}'`;
  const memberAuditScope = `TenantId='${tenant}' AND CompanyId='${company}' AND TargetUserId='${memberId}'`;
  // Retain this owned member and immutable history for the CI volume lifetime.
  sql(`USE AIOfficeLocal; BEGIN TRANSACTION;
    INSERT aioffice.Users(TenantId,Id,IdentityProvider,Subject,DisplayName) VALUES('${tenant}','${memberId}',N'disposable-browser-proof',N'${randomUUID()}',N'${memberName}');
    INSERT aioffice.CompanyMemberships(TenantId,CompanyId,UserId) VALUES('${tenant}','${company}','${memberId}');
    INSERT aioffice.RoleAssignments(TenantId,CompanyId,UserId,RoleKey) VALUES('${tenant}','${company}','${memberId}',N'viewer'); COMMIT TRANSACTION;`);
  const memberPath = `/api/local/company/members/${memberId}/access` + query;
  const memberCount = () => sql(`USE AIOfficeLocal; SELECT COUNT(*) FROM aioffice.CompanyMembershipAccessAudits WHERE ${memberAuditScope};`);
  const memberState = () => sql(`USE AIOfficeLocal; SELECT CONCAT(CONVERT(int,IsActive),N':',Version) FROM aioffice.CompanyMemberships WHERE ${memberScope};`);
  await page.getByRole("button", { name: "Tải lại thành viên", exact: true }).click();
  const memberButton = active => page.getByRole("button", { name: `${active ? "Khóa quyền" : "Mở lại quyền"} ${memberName}`, exact: true });
  await memberButton(true).waitFor();
  stage = "browser-member-suspend-reactivate";
  await memberButton(true).click(); await memberButton(false).waitFor();
  requireProof(memberState() === "0:2" && memberCount() === "1");
  await memberButton(false).click(); await memberButton(true).waitFor();
  requireProof(memberState() === "1:3" && memberCount() === "2");
  stage = "browser-member-committed-lost-reply";
  let lostInput, committed = false;
  const loseReply = async route => {
    lostInput = route.request().postDataJSON();
    const response = await route.fetch(); committed = response.status() === 200;
    await route.abort("failed");
  };
  await page.route(app + memberPath, loseReply);
  await memberButton(true).click();
  await page.getByRole("button", { name: `Thử lại thao tác với ${memberName}`, exact: true }).waitFor();
  requireProof(committed && lostInput.expectedVersion === "3" && !lostInput.isActive && guid(lostInput.operationId)
    && memberState() === "0:4" && memberCount() === "3");
  await page.unroute(app + memberPath, loseReply);
  const retried = page.waitForResponse(response => response.url() === app + memberPath && response.request().method() === "POST");
  await page.getByRole("button", { name: `Thử lại thao tác với ${memberName}`, exact: true }).click();
  const retry = await retried;
  requireProof(retry.status() === 200 && JSON.stringify(retry.request().postDataJSON()) === JSON.stringify(lostInput));
  await memberButton(false).waitFor(); requireProof(memberCount() === "3" && memberState() === "0:4");
  await memberButton(false).click(); await memberButton(true).waitFor();
  requireProof(memberState() === "1:5" && memberCount() === "4");
  stage = "browser-member-stale-version";
  sql(`USE AIOfficeLocal; UPDATE aioffice.CompanyMemberships SET IsActive=0,Version=Version+1 WHERE ${memberScope};`);
  await memberButton(true).click(); await page.getByRole("alert").waitFor();
  requireProof(await memberButton(true).count() === 0 && await memberButton(false).count() === 0
    && memberState() === "0:6" && memberCount() === "4");
  await page.getByRole("button", { name: "Tải lại thành viên", exact: true }).click();
  await memberButton(false).waitFor(); await memberButton(false).click(); await memberButton(true).waitFor();
  requireProof(memberState() === "1:7" && memberCount() === "5");
  const roleProof = sql(`USE AIOfficeLocal; SELECT COUNT(*) FROM aioffice.RoleAssignments WHERE ${memberScope} AND RoleKey=N'viewer';`);
  requireProof(roleProof === "1" && (await current()).status === 200 && (await cookie("aioffice_browser_session")).value === sid.value);
  console.log("PASS actual Chromium member suspend/reactivate, committed lost reply with stable replay, stale version reload and unchanged role/session");
  const foreign = randomUUID();
  for (const path of ["/api/local/session", "/api/local/data-sources", "/api/local/company/members"]) {
    requireProof((await get(path + `?companyId=${foreign}`)).status === 401);
  }
  const password = await page.evaluate(async () => (await fetch("/api/local/session/login", {
    method: "POST", headers: { "Content-Type": "application/json" }, body: "{}",
  })).status);
  requireProof(password === 503 && (await current()).status === 200);
  console.log("PASS browser company/member/source access, cross-company denial and disabled local password route");
  requireProof((await mutate(`/api/local/company/members/${memberId}/access?companyId=${foreign}`, "POST",
    { operationId: randomUUID(), expectedVersion: "7", isActive: false })).status === 401 && memberState() === "1:7" && memberCount() === "5");

  stage = "owned-source-metadata-mutation";
  const sourcePath = `/api/local/data-sources/${source.id}/metadata` + query;
  const metadata = Object.fromEntries(["logicalName", "purpose", "maxConcurrency", "isEnabled"].map(key => [key, source[key]]));
  const changed = { ...metadata, purpose: "Disposable browser metadata control" };
  try {
    const updated = await mutate(sourcePath, "PUT", changed);
    requireProof(updated.status === 200 && updated.cache === "no-store" && updated.body.purpose === changed.purpose);
    requireProof(updated.body.kind === source.kind && updated.body.environment === source.environment
      && updated.body.allowRead === source.allowRead && updated.body.allowWrite === source.allowWrite);
  } finally {
    const restored = await mutate(sourcePath, "PUT", metadata);
    requireProof(restored.status === 200 && Object.entries(metadata).every(([key, value]) => restored.body[key] === value));
  }
  console.log("PASS browser source metadata mutation preserves policy and restores the original owned fixture");

  stage = "callback-denial-preserves-session";
  requireProof(originalCallback.cookie?.includes("aioffice_oidc_transaction="));
  const replayCookies = originalCallback.cookie.split("; ").filter(item => !item.startsWith("aioffice_browser_session="));
  replayCookies.push(`aioffice_browser_session=${sid.value}`);
  const replay = await context.request.get(originalCallback.url, { maxRedirects: 0, headers: { Cookie: replayCookies.join("; ") } });
  requireProof(replay.status() === 401 && !replay.headers()["set-cookie"] && (await current()).status === 200);
  const start = await page.evaluate(async companyId => {
    const response = await fetch("/api/local/session/oidc/start", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ companyId }) });
    return { status: response.status, body: await response.json() };
  }, company);
  requireProof(start.status === 200 && new URL(start.body.authorizationUrl).origin === identity);
  const wrong = new URL(callback); wrong.searchParams.set("code", "invalid-disposable-code"); wrong.searchParams.set("state", "wrong-state");
  const denied = await context.request.get(wrong.href, { maxRedirects: 0 });
  requireProof(denied.status() === 401 && !denied.headers()["set-cookie"] && (await cookie("aioffice_browser_session")).value === sid.value
    && (await current()).status === 200);
  const oversized = await page.evaluate(async companyId => (await fetch(`/api/local/tasks?companyId=${companyId}`, {
    method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ ignored: "x".repeat(1024 * 1024) }),
  })).status, company);
  requireProof(oversized === 413 && (await current()).status === 200);
  console.log("PASS actual callback replay/wrong-state and oversized mutation denial preserve the issued browser session");

  stage = "fresh-sql-role-revocation";
  const scope = `TenantId='${tenant}' AND CompanyId='${company}' AND UserId='${user}'`;
  const backup = "tempdb.dbo.AIOfficeBrowserRoles_" + randomUUID().replaceAll("-", "");
  requireProof((await current()).body.roles.includes("admin"));
  sql(`USE AIOfficeLocal; SELECT TenantId,CompanyId,UserId,RoleKey,CreatedAtUtc INTO ${backup} FROM aioffice.RoleAssignments WHERE ${scope};`);
  try {
    stage = "owned-sql-admin-revocation";
    sql(`USE AIOfficeLocal; DELETE FROM aioffice.RoleAssignments WHERE ${scope} AND RoleKey COLLATE Latin1_General_100_BIN2=N'admin';`);
    stage = "fresh-authority-after-role-revocation";
    const authority = await current(); requireProof(authority.status === 200 && !authority.body.roles.includes("admin"));
    stage = "member-denial-after-role-revocation";
    requireProof((await get("/api/local/company/members" + query)).status === 403);
    requireProof((await mutate(memberPath, "POST", { operationId: randomUUID(), expectedVersion: "7", isActive: false })).status === 403
      && memberState() === "1:7" && memberCount() === "5");
    stage = "registration-denial-after-role-revocation";
    requireProof((await get("/api/local/data-sources/registration-options" + query)).status === 403);
    stage = "metadata-denial-after-role-revocation";
    requireProof((await mutate(sourcePath, "PUT", changed)).status === 403);
    stage = "member-ui-clearing-after-role-revocation";
    await page.evaluate(() => window.dispatchEvent(new Event("focus")));
    await page.waitForFunction(() => ![...document.querySelectorAll("button")].some(item => item.textContent?.trim() === "Thành viên")
      && !document.querySelector('section[aria-label="Thành viên công ty"] tbody'));
  } finally {
    sql(`USE AIOfficeLocal; BEGIN TRANSACTION; DELETE FROM aioffice.RoleAssignments WHERE ${scope};
      INSERT INTO aioffice.RoleAssignments(TenantId,CompanyId,UserId,RoleKey,CreatedAtUtc) SELECT TenantId,CompanyId,UserId,RoleKey,CreatedAtUtc FROM ${backup};
      DROP TABLE ${backup}; COMMIT TRANSACTION;`);
  }
  stage = "fresh-authority-after-role-restoration";
  requireProof((await current()).body.roles.includes("admin") && (await get("/api/local/company/members" + query)).status === 200);
  await page.evaluate(() => window.dispatchEvent(new Event("focus")));
  await page.getByRole("button", { name: "Thành viên", exact: true }).first().waitFor();
  console.log("PASS actual SQL role revoke/restore under same opaque SID and private member UI clearing");

  stage = "logout-relogin";
  const previous = await context.cookies(app);
  await page.getByRole("button", { name: "Đăng xuất", exact: true }).click();
  await page.getByRole("button", { name: "Đăng nhập doanh nghiệp", exact: true }).waitFor();
  requireProof(!await cookie("aioffice_browser_session") && (await current()).status === 401);
  const old = await context.request.get(app + "/api/local/session" + query, {
    headers: { Cookie: previous.filter(item => item.name.startsWith("aioffice_browser_")).map(item => `${item.name}=${item.value}`).join("; ") },
  });
  requireProof(old.status() === 401);
  const oldMember = await context.request.post(app + memberPath, { headers: {
    Origin: app, Cookie: previous.filter(item => item.name.startsWith("aioffice_browser_")).map(item => `${item.name}=${item.value}`).join("; "),
  }, data: { operationId: randomUUID(), expectedVersion: "7", isActive: false } });
  requireProof(oldMember.status() === 401 && memberState() === "1:7" && memberCount() === "5");
  stage = "provider-sso-relogin";
  await signIn(); requireProof((await cookie("aioffice_browser_session")).value !== sid.value);
  console.log("PASS actual logout denies presented old binding/SID and provider SSO re-login issues fresh opaque session");

  stage = "owned-session-expiry-private-clear";
  await waitForSource();
  // Prove browser mutation/SQL persistence and populate private chat before
  // expiry. Provider business-answer qualification is a separate requirement.
  const privateMessage = "Disposable browser task " + randomUUID();
  await page.getByRole("combobox", { name: "Nguồn dữ liệu", exact: true }).selectOption(source.id);
  await page.locator('textarea[name="question"]').fill(privateMessage);
  const submitted = page.waitForResponse(response => response.request().method() === "POST"
    && response.url() === app + "/api/local/tasks" + query);
  await page.getByRole("button", { name: "Gửi", exact: true }).click();
  const acceptedResponse = await submitted;
  requireProof(acceptedResponse.status() === 202 && acceptedResponse.headers()["cache-control"] === "no-store");
  const accepted = await acceptedResponse.json(); requireProof(guid(accepted.taskId));
  const task = await get(`/api/local/tasks/${accepted.taskId}` + query);
  requireProof(task.status === 200 && task.cache === "no-store" && JSON.parse(task.text).taskId === accepted.taskId);
  await page.getByText(privateMessage, { exact: true }).waitFor();
  const privateDraft = "Disposable browser draft " + randomUUID();
  await page.locator('textarea[name="question"]:enabled').waitFor();
  await page.locator('textarea[name="question"]').fill(privateDraft);
  requireProof((await mutate(`/api/local/tasks?companyId=${foreign}`, "POST", { dataSourceId: source.id, question: privateMessage })).status === 401);
  console.log("PASS browser task submission and durable scoped task read; cross-company mutation denied");
  const liveBinding = (await cookie("aioffice_browser_binding")).value.split(".");
  const liveSid = (await cookie("aioffice_browser_session")).value;
  const namespace = digest(JSON.stringify(["aioffice-browser-session-v1", issuer, "aioffice-browser", callback]));
  const key = `aioffice:browser:${namespace}:{${digest(liveBinding[0])}}`;
  // Only this script's issued/current owned browser row is touched. Redis TIME
  // advances naturally; no clock mocking, token forging or broad Redis cleanup.
  const expire = `if redis.call('HGET',KEYS[1],'generation') ~= ARGV[1] or redis.call('HGET',KEYS[1],'sid') ~= ARGV[2] then return 0 end
    local now=tonumber(redis.call('TIME')[1]); redis.call('HSET',KEYS[1],'sessionExpiry',now+1); return 1`;
  requireProof(run(["exec", "-T", "redis", "redis-cli", "--raw", "EVAL", expire, "1", key, digest(liveBinding[1]), digest(liveSid)]) === "1");
  await delay(1500);
  requireProof((await current()).status === 401);
  await page.evaluate(() => window.dispatchEvent(new Event("focus")));
  await page.getByRole("button", { name: "Đăng nhập doanh nghiệp", exact: true }).waitFor();
  requireProof(await page.locator(`option[value="${source.id}"]`).count() === 0
    && await page.locator('section[aria-label="Thành viên công ty"] tbody').count() === 0
    && await page.getByText(privateMessage, { exact: true }).count() === 0
    && !await page.locator('textarea[name="question"]').count());
  stage = "provider-expiry-reauthentication";
  await signIn(); await waitForSource();
  requireProof((await current()).status === 200);
  requireProof(await page.getByText(privateMessage, { exact: true }).count() === 0
    && (await page.locator('textarea[name="question"]').inputValue()) === "");
  console.log("PASS actual Redis TIME session expiry clears private UI and fresh provider reauthentication recovers");
  stage = "sanitized-ui-artifact";
  requireProof(!JSON.stringify(await current()).includes(manifest.AIOFFICE_OWNER_PASSWORD));
  const artifact = join(resolve(process.env.RUNNER_TEMP), "aioffice-browser-proof"); await mkdir(artifact, { recursive: true });
  await page.screenshot({ path: join(artifact, "workspace.png"), fullPage: true });
  console.log("PASS actual Chromium browser OIDC gate");
} catch {
  // Assertion/library/CLI errors can embed a password, authorization code,
  // JWT or private environment. Emit only a fixed stage identifier.
  console.error(`FAIL actual Chromium browser OIDC gate: ${stage}`); process.exitCode = 1;
  try { safeFailureLogs?.(); } catch { console.error("CI verification refusal stage unavailable"); }
} finally { if (browser) await browser.close().catch(() => {}); }
