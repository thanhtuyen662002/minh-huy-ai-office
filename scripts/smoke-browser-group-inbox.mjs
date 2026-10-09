// Shipping inbox with real Code/S256, SQL grants and private BFF; owned CI only.
import { chromium } from "playwright";
import { readFile, mkdir } from "node:fs/promises";
import { resolve, join } from "node:path";
import { pathToFileURL } from "node:url";
import { spawnSync } from "node:child_process";
import { randomUUID } from "node:crypto";
import { setTimeout as delay } from "node:timers/promises";

const proof = value => { if (!value) throw new Error("Owned group inbox proof refused."); };
const guid = value => typeof value === "string" && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(value)
  && value !== "00000000-0000-0000-0000-000000000000";
export function requireOwnedGroupInbox(directory, environment = process.env) {
  proof(environment.CI === "true" && environment.GITHUB_ACTIONS === "true" && environment.RUNNER_TEMP
    && resolve(directory) === join(resolve(environment.RUNNER_TEMP), "aioffice-local"));
}
let stage = "owned-guard";
export async function verifyOwnedGroupInbox(directory, fixture) {
  requireOwnedGroupInbox(directory); // Before file, Docker, SQL, browser or network.
  proof(fixture && Object.keys(fixture).sort().join(",") === "messageId,sequence,sourceId,text"
    && guid(fixture.sourceId) && guid(fixture.messageId) && Number.isSafeInteger(fixture.sequence) && fixture.sequence > 0
    && typeof fixture.text === "string" && fixture.text.length <= 8000);
  const manifest = JSON.parse(await readFile(join(directory, "installation.json"), "utf8"));
  const [tenant, company, owner] = ["TENANT", "COMPANY", "USER"].map(key => manifest[`AIOFFICE_${key}_ID`]);
  proof([manifest.AIOFFICE_INSTALLATION_ID, tenant, company, owner].every(guid));
  const compose = ["compose", "--env-file", join(directory, "local.env"), "-f", "compose.local.yaml"];
  const flags = { ...process.env, AIOFFICE_BROWSER_OIDC_ENABLED: "true", AIOFFICE_LOCAL_UI_ENABLED: "false" };
  const run = (args, environment = process.env, input) => {
    const result = spawnSync("docker", [...compose, ...args], { encoding: "utf8", env: environment, input, timeout: 90000, maxBuffer: 1024 * 1024 });
    proof(result.status === 0); return result.stdout.trim();
  };
  const profile = JSON.parse(run(["config", "--format", "json"], flags));
  proof(profile.name === "aioffice-" + manifest.AIOFFICE_INSTALLATION_ID.replaceAll("-", ""));
  for (const service of Object.values(profile.services)) for (const port of service.ports ?? []) proof(port.host_ip === "127.0.0.1");
  const app = profile.services.web.environment.AIOFFICE_BROWSER_OIDC_PUBLIC_ORIGIN;
  const issuer = profile.services.web.environment.AIOFFICE_BROWSER_OIDC_ISSUER;
  proof(app === "http://127.0.0.1:3000" && issuer === "http://127.0.0.1:8081/realms/aioffice-local");
  const sql = statement => run(["exec", "-T", "sql", "sh", "-c",
    'SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -I -b -m 1 -h -1 -W -i /dev/stdin'], process.env,
    "SET NOCOUNT ON; USE AIOfficeLocal; " + statement);
  const grant = `TenantId='${tenant}' AND CompanyId='${company}' AND BindingId='${fixture.sourceId}' AND UserId='${owner}'`;
  proof(sql(`SELECT COUNT(*) FROM aioffice.GroupReaderGrants WHERE ${grant} AND IsEnabled=1;`) === "1");
  let browser, webChanged = false, release;
  try {
    stage = "browser-web-mode"; run(["up", "-d", "--no-deps", "--force-recreate", "web"], flags); webChanged = true;
    let ready = false;
    for (let attempt = 0; attempt < 60; attempt++) {
      try { ready = (await fetch(app, { signal: AbortSignal.timeout(2000) })).status === 200; } catch { /* Fixed readiness. */ }
      if (ready) break; await delay(500);
    }
    proof(ready); browser = await chromium.launch({ headless: true, timeout: 15000 });
    const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } }), page = await context.newPage();
    page.setDefaultTimeout(15000); page.setDefaultNavigationTimeout(20000);
    const callback = app + "/api/local/session/oidc/callback", query = `?companyId=${company}`;
    const get = async path => {
      const response = await context.request.get(app + path); return { status: response.status(), cache: response.headers()["cache-control"], body: await response.json() };
    };
    const sid = async () => (await context.cookies(app)).find(item => item.name === "aioffice_browser_session")?.value;
    async function signIn(passwordRequired) {
      await page.goto(app); const authRequest = page.waitForRequest(request => request.url().startsWith(issuer + "/protocol/openid-connect/auth?"));
      const finished = page.waitForResponse(response => response.url().startsWith(callback + "?"));
      await page.getByRole("button", { name: "Đăng nhập doanh nghiệp", exact: true }).click();
      const auth = new URL((await authRequest).url()); proof(auth.searchParams.get("response_type") === "code" && auth.searchParams.get("code_challenge_method") === "S256");
      if (passwordRequired) {
        await page.locator("#username").fill("owner"); await page.locator("#password").fill(manifest.AIOFFICE_OWNER_PASSWORD);
        await page.locator("#kc-login").click();
      }
      proof((await finished).status() === 303); await page.getByRole("button", { name: "Đăng xuất", exact: true }).waitFor();
      const current = await get("/api/local/session" + query);
      proof(current.status === 200 && current.cache === "no-store" && current.body.tenantId === tenant && current.body.companyId === company && current.body.userId === owner);
    }
    const sourceButton = () => page.getByRole("button", { name: "Owned CI source", exact: true });
    const article = () => page.getByRole("article", { name: "Nội dung tin", exact: true });
    async function open() {
      await sourceButton().click(); await page.getByRole("button", { name: `Đọc tin ${fixture.sequence}`, exact: true }).click();
      await article().waitFor(); proof(await article().locator("p.whitespace-pre-wrap").textContent() === fixture.text);
    }
    stage = "issued-code-session"; await signIn(true); const originalSid = await sid(); proof(originalSid && /^[A-Za-z0-9_-]{43}$/.test(originalSid));
    stage = "granted-source-metadata-private-positive";
    await page.getByRole("button", { name: "Hộp thư nguồn", exact: true }).first().click(); await open();
    const privatePath = `/api/local/group-sources/${fixture.sourceId}/messages/${fixture.messageId}${query}`;
    const value = await get(privatePath); proof(value.status === 200 && value.cache === "no-store" && value.body.text === fixture.text
      && value.body.source.tenantId === tenant && value.body.source.companyId === company && value.body.source.sourceBindingId === fixture.sourceId);
    stage = "foreign-scope-denial";
    proof((await get(`/api/local/group-sources?companyId=${randomUUID()}`)).status === 401);
    proof((await get(`/api/local/group-sources/${randomUUID()}/messages/${fixture.messageId}${query}`)).status === 403);
    proof((await get(`/api/local/group-sources/${fixture.sourceId}/messages/${randomUUID()}${query}`)).status === 404);
    try {
      stage = "native-reader-grant-revocation"; sql(`UPDATE aioffice.GroupReaderGrants SET IsEnabled=0 WHERE ${grant};`);
      await page.getByRole("button", { name: "Cập nhật tin", exact: true }).click(); await page.getByRole("alert").waitFor();
      proof(await article().count() === 0 && await sourceButton().count() === 0 && (await get(privatePath)).status === 403 && await sid() === originalSid);
      proof((await get("/api/local/group-sources" + query)).body.items.every(item => item.source.sourceBindingId !== fixture.sourceId));
    } finally { sql(`UPDATE aioffice.GroupReaderGrants SET IsEnabled=1 WHERE ${grant};`); }
    stage = "grant-restored-private-positive";
    await page.getByRole("button", { name: "Tải lại nguồn", exact: true }).click(); await open(); proof(await sid() === originalSid);
    console.log("PASS owned group Chromium Code/S256 source catalog -> metadata -> exact private Unicode, foreign scope and native ReaderGrant denial/clearing/restored positive under same SID");
    stage = "actual-private-body-held";
    let observed; const arrived = new Promise(resolve => { observed = resolve; }), held = new Promise(resolve => { release = resolve; });
    await page.route("**/api/local/group-sources/*/messages/*?*", async route => {
      const upstream = await route.fetch(); proof(upstream.status() === 200 && (await upstream.json()).text === fixture.text);
      observed(); await held;
      try { await route.fulfill({ response: upstream }); } catch { /* Shipping logout may already have aborted this exact request. */ }
    });
    await page.getByRole("button", { name: "Cập nhật tin", exact: true }).click(); await arrived;
    proof(await article().count() === 0);
    const oldCookies = await context.cookies(app);
    stage = "logout-clears-held-private-body";
    await page.getByRole("button", { name: "Đăng xuất", exact: true }).click(); await page.getByRole("button", { name: "Đăng nhập doanh nghiệp", exact: true }).waitFor();
    release(); await page.unrouteAll({ behavior: "wait" }); proof(await article().count() === 0);
    const obsolete = await context.request.get(app + privatePath, { headers: { Cookie: oldCookies.filter(item => item.name.startsWith("aioffice_browser_")).map(item => `${item.name}=${item.value}`).join("; ") } });
    proof(obsolete.status() === 401);
    stage = "new-issued-session-private-restoration"; await signIn(false); proof(await sid() !== originalSid);
    await page.getByRole("button", { name: "Hộp thư nguồn", exact: true }).first().click(); await open();
    const artifacts = join(resolve(process.env.RUNNER_TEMP), "aioffice-browser-proof"); await mkdir(artifacts, { recursive: true });
    await page.screenshot({ path: join(artifacts, "group-inbox.png"), fullPage: true });
    console.log("PASS owned group Chromium held actual private BFF body discarded on logout, old SID401 and fresh issued session/source read after reload without writes");
  } finally {
    release?.();
    const previousStage = stage;
    try { if (browser) await browser.close(); }
    finally {
      if (webChanged) { stage = "shipping-web-mode-restoration"; run(["up", "-d", "--no-deps", "--force-recreate", "web"]); }
      stage = previousStage;
    }
  }
}
if (process.argv[1] && pathToFileURL(resolve(process.argv[1])).href === import.meta.url) {
  try {
    const directory = resolve(process.argv[2] ?? ""); requireOwnedGroupInbox(directory);
    let text = "";
    for await (const chunk of process.stdin) { text += chunk; proof(Buffer.byteLength(text) <= 65536); }
    await verifyOwnedGroupInbox(directory, JSON.parse(text));
  } catch { console.error(`FAIL owned group Chromium inbox gate: ${stage}`); process.exitCode = 1; }
}
