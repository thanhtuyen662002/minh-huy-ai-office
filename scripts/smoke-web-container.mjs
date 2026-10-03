import assert from "node:assert/strict";
import { randomUUID } from "node:crypto";
import { spawnSync } from "node:child_process";

const image = process.argv[2] ?? "aioffice/web:ci";

function docker(args) {
  const result = spawnSync("docker", args, { encoding: "utf8", timeout: 120_000 });
  if (result.error || result.status !== 0) {
    throw new Error(`docker ${args[0]} failed: ${result.error?.message ?? result.stderr}`);
  }
  return result.stdout.trim();
}

async function awaitPage(url) {
  const deadline = Date.now() + 60_000;
  while (Date.now() < deadline) {
    try {
      const response = await fetch(url, { signal: AbortSignal.timeout(3_000) });
      if (response.ok) return response;
    } catch {
      // Container creation does not mean that the HTTP server is listening yet.
    }
    await new Promise((resolve) => setTimeout(resolve, 500));
  }
  throw new Error(`Web server did not become ready at ${url}`);
}

async function awaitHealthy(container) {
  const deadline = Date.now() + 60_000;
  while (Date.now() < deadline) {
    const status = JSON.parse(docker(["inspect", container]))[0].State.Health?.Status;
    assert.ok(status, "The image must define a real Docker healthcheck");
    if (status === "healthy") return;
    if (status === "unhealthy") throw new Error("FE container healthcheck failed");
    await new Promise((resolve) => setTimeout(resolve, 500));
  }
  throw new Error("FE container healthcheck did not become healthy");
}

async function smoke(companyId, companyName, excludedName) {
  const container = `aioffice-web-smoke-${randomUUID()}`;
  let created = false;
  try {
    docker([
      "run", "--detach", "--name", container,
      "--read-only", "--cap-drop", "ALL", "--security-opt", "no-new-privileges:true",
      "--tmpfs", "/tmp:rw,noexec,nosuid,size=64m",
      "--tmpfs", "/app/apps/web/.next/cache:rw,noexec,nosuid,size=64m,uid=1000,gid=1000,mode=0700",
      "--publish", "127.0.0.1::3000",
      "--env", `AIOFFICE_LOCAL_COMPANY_ID=${companyId}`,
      "--env", `AIOFFICE_LOCAL_COMPANY_NAME=${companyName}`,
      "--env", "AIOFFICE_LOCAL_UI_ENABLED=false",
      image,
    ]);
    created = true;

    const config = JSON.parse(docker(["inspect", container]))[0];
    const port = config.NetworkSettings.Ports["3000/tcp"][0];
    assert.equal(port.HostIp, "127.0.0.1");
    assert.equal(config.HostConfig.ReadonlyRootfs, true);
    assert.ok(config.HostConfig.CapDrop.includes("ALL"));
    assert.ok(config.HostConfig.SecurityOpt.includes("no-new-privileges:true"));
    assert.notEqual(docker(["exec", container, "id", "-u"]), "0");

    const baseUrl = `http://127.0.0.1:${port.HostPort}`;
    const page = await awaitPage(baseUrl);
    const html = await page.text();
    assert.ok(html.includes(companyId), "Company ID must be read from this container environment");
    assert.ok(html.includes(companyName), "Company name must be read from this container environment");
    assert.ok(!html.includes(excludedName), "Settings from the other instance must not be cached in the image");
    assert.match(page.headers.get("cache-control") ?? "", /no-store/);

    const assets = [...html.matchAll(/(?:src|href)="(\/_next\/static\/[^\"]+)"/g)]
      .map((match) => match[1].replaceAll("&amp;", "&"));
    assert.ok(assets.length > 0, "The rendered page must include standalone static assets");
    for (const asset of new Set(assets)) {
      const response = await fetch(`${baseUrl}${asset}`, { signal: AbortSignal.timeout(5_000) });
      assert.equal(response.status, 200, `Static asset ${asset} must be served`);
      assert.ok(!(response.headers.get("content-type") ?? "").includes("text/html"));
      await response.arrayBuffer();
    }

    const session = await fetch(`${baseUrl}/api/local/session?companyId=${companyId}`);
    assert.equal(session.status, 503, "Local-only authentication must remain disabled by default");
    assert.match(session.headers.get("cache-control") ?? "", /no-store/);
    assert.equal(session.headers.get("set-cookie"), null);

    const login = await fetch(`${baseUrl}/api/local/session/login`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ companyId, username: "smoke-user", password: "smoke-password" }),
    });
    assert.equal(login.status, 503);
    assert.match(login.headers.get("cache-control") ?? "", /no-store/);
    assert.equal(login.headers.get("set-cookie"), null);
    await awaitHealthy(container);
    console.log(`PASS ${companyName}: runtime configuration, static assets, non-root/read-only runtime and disabled local session`);
  } catch (error) {
    if (created) console.error(docker(["logs", "--tail", "40", container]));
    throw error;
  } finally {
    if (created) docker(["rm", "--force", container]);
  }
}

const firstName = `RuntimeCompanyA${randomUUID()}`;
const secondName = `RuntimeCompanyB${randomUUID()}`;
await smoke("11111111-1111-4111-8111-111111111111", firstName, secondName);
await smoke("22222222-2222-4222-8222-222222222222", secondName, firstName);
console.log("FE container runtime smoke passed using one image and two different company environments.");
