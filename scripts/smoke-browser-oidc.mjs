// Required real Chromium proof against only the owned disposable GitHub CI stack.
// No traces, provider bodies, credentials, tokens or private cookie values are logged.
import { chromium } from "playwright";
import { readFile, mkdir } from "node:fs/promises";
import { resolve, join } from "node:path";
import { spawnSync } from "node:child_process";
import { createHash, randomUUID } from "node:crypto";
import { setTimeout as delay } from "node:timers/promises";

let stage = "disposable-fixture-guard", browser;
const requireProof = condition => { if (!condition) throw new Error("Browser proof failed."); };
const digest = value => createHash("sha256").update(value).digest("hex");
const guid = value => typeof value === "string" && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(value)
  && value !== "00000000-0000-0000-0000-000000000000";
try {
  const directory = resolve(process.argv[2] ?? "");
  requireProof(process.env.CI === "true" && process.env.GITHUB_ACTIONS === "true" && process.env.RUNNER_TEMP
    && directory === join(resolve(process.env.RUNNER_TEMP), "aioffice-local"));
  const manifest = JSON.parse(await readFile(join(directory, "installation.json"), "utf8"));
  for (const key of ["INSTALLATION", "TENANT", "COMPANY", "USER"]) requireProof(guid(manifest[`AIOFFICE_${key}_ID`]));
  const compose = ["compose", "--env-file", join(directory, "local.env"), "-f", "compose.local.yaml"];
  const run = (args, env = process.env) => {
    const result = spawnSync("docker", [...compose, ...args], { encoding: "utf8", env, timeout: 90_000, maxBuffer: 8 * 1024 * 1024 });
    requireProof(result.status === 0); return result.stdout.trim();
  };
  const flags = { ...process.env, AIOFFICE_BROWSER_OIDC_ENABLED: "true", AIOFFICE_LOCAL_UI_ENABLED: "false" };
  const profile = JSON.parse(run(["config", "--format", "json"], flags));
  requireProof(profile.name === "aioffice-" + manifest.AIOFFICE_INSTALLATION_ID.replaceAll("-", ""));
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
  const current = () => page.evaluate(async path => {
    const response = await fetch(path, { cache: "no-store" });
    return { status: response.status, body: await response.json(), cache: response.headers.get("cache-control") };
  }, "/api/local/session" + query);
  const get = path => page.evaluate(async path => {
    const response = await fetch(path, { cache: "no-store" });
    return { status: response.status, text: await response.text(), cache: response.headers.get("cache-control") };
  }, path);
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
    requireProof((await callbackRequest.response())?.status() === 303);
    await page.waitForURL(url => url.origin === app && url.pathname === "/");
    stage = "authenticated-workspace-ready";
    await page.getByRole("button", { name: "Đăng xuất", exact: true }).waitFor();
    const authority = await current();
    requireProof(authority.status === 200 && authority.body.companyId === company && authority.body.tenantId === tenant
      && authority.body.userId === user && authority.cache === "no-store");
    return callbackRequest.url();
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
  requireProof(JSON.parse(sources.text).some(item => item.id === manifest.AIOFFICE_DATA_SOURCE_ID));
  requireProof(JSON.parse(members.text).items.some(item => item.userId === user));
  requireProof(!/access_token|accessToken|client_secret|connectionSecretReference|secretref:\/\//.test(sources.text + members.text));
  await page.getByRole("option", { name: "Local sample ERP", exact: true }).waitFor();
  await page.getByRole("button", { name: "Thành viên", exact: true }).first().click();
  await page.locator('section[aria-label="Thành viên công ty"] tbody tr').first().waitFor();
  const foreign = randomUUID();
  for (const path of ["/api/local/session", "/api/local/data-sources", "/api/local/company/members"]) {
    requireProof((await get(path + `?companyId=${foreign}`)).status === 401);
  }
  const password = await page.evaluate(async () => (await fetch("/api/local/session/login", {
    method: "POST", headers: { "Content-Type": "application/json" }, body: "{}",
  })).status);
  requireProof(password === 503 && (await current()).status === 200);
  console.log("PASS browser company/member/source access, cross-company denial and disabled local password route");

  stage = "callback-denial-preserves-session";
  const replay = await context.request.get(originalCallback, { maxRedirects: 0 });
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
  const sql = text => run(["exec", "-T", "sql", "sh", "-c",
    'SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -I -b -m 1 -h -1 -W -Q "$1"',
    "sql", "SET NOCOUNT ON; " + text]);
  const scope = `TenantId='${tenant}' AND CompanyId='${company}' AND UserId='${user}'`;
  const backup = "tempdb.dbo.AIOfficeBrowserRoles_" + randomUUID().replaceAll("-", "");
  requireProof((await current()).body.roles.includes("admin"));
  sql(`USE AIOfficeLocal; SELECT TenantId,CompanyId,UserId,RoleKey,CreatedAtUtc INTO ${backup} FROM aioffice.RoleAssignments WHERE ${scope};`);
  try {
    sql(`USE AIOfficeLocal; DELETE FROM aioffice.RoleAssignments WHERE ${scope} AND RoleKey COLLATE Latin1_General_100_BIN2=N'admin';`);
    const authority = await current(); requireProof(authority.status === 200 && !authority.body.roles.includes("admin"));
    requireProof((await get("/api/local/company/members" + query)).status === 403);
    requireProof((await get("/api/local/data-sources/registration-options" + query)).status === 403);
    await page.evaluate(() => window.dispatchEvent(new Event("focus")));
    await page.waitForFunction(() => ![...document.querySelectorAll("button")].some(item => item.textContent?.trim() === "Thành viên")
      && !document.querySelector('section[aria-label="Thành viên công ty"] tbody'));
  } finally {
    sql(`USE AIOfficeLocal; BEGIN TRANSACTION; DELETE FROM aioffice.RoleAssignments WHERE ${scope};
      INSERT INTO aioffice.RoleAssignments(TenantId,CompanyId,UserId,RoleKey,CreatedAtUtc) SELECT TenantId,CompanyId,UserId,RoleKey,CreatedAtUtc FROM ${backup};
      DROP TABLE ${backup}; COMMIT TRANSACTION;`);
  }
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
  stage = "provider-sso-relogin";
  await signIn(); requireProof((await cookie("aioffice_browser_session")).value !== sid.value);
  console.log("PASS actual logout denies presented old binding/SID and provider SSO re-login issues fresh opaque session");

  stage = "owned-session-expiry-private-clear";
  await page.getByRole("option", { name: "Local sample ERP", exact: true }).waitFor();
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
  requireProof(await page.getByRole("option", { name: "Local sample ERP", exact: true }).count() === 0
    && await page.locator('section[aria-label="Thành viên công ty"] tbody').count() === 0);
  stage = "provider-expiry-reauthentication";
  await signIn(); await page.getByRole("option", { name: "Local sample ERP", exact: true }).waitFor();
  requireProof((await current()).status === 200);
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
} finally { if (browser) await browser.close().catch(() => {}); }
