// Disposable CI transport only. No public listener, production hook or logs.
import { createServer } from "node:http";
import { readFileSync, writeFileSync } from "node:fs";
import { pathToFileURL } from "node:url";

export async function startOwnedCoreReplyProxy(directory) {
  if (process.env.CI !== "true" || process.env.GITHUB_ACTIONS !== "true"
    || process.env.AIOFFICE_BROWSER_CORE_REPLY_PROOF !== "true" || directory !== "/tmp/aioffice-core-reply-proof") {
    throw new Error("Owned Core reply proof refused.");
  }
  const control = directory + "/fault.json";
  const server = createServer(async (req, res) => {
    try {
      if (!req.url?.startsWith("/api/")) { res.writeHead(404).end(); return; }
      const chunks = []; let length = 0;
      for await (const chunk of req) {
        length += chunk.length; if (length > 1024 * 1024) { res.writeHead(413).end(); return; } chunks.push(chunk);
      }
      const body = Buffer.concat(chunks);
      // Keep all authorization privately in memory and use one fixed owned Core.
      const headers = { ...req.headers }; delete headers.host; delete headers.connection;
      const upstream = await fetch("http://core-api:8080" + req.url, {
        method: req.method, headers, ...(body.length ? { body } : {}), redirect: "error", signal: AbortSignal.timeout(15_000),
      });
      const bytes = Buffer.from(await upstream.arrayBuffer());
      if (bytes.length > 8 * 1024 * 1024) { res.destroy(); return; }
      let fault; try { fault = JSON.parse(readFileSync(control, "utf8")); } catch { /* Unarmed: transparent forwarding. */ }
      if (fault?.count === 0 && req.method === "POST" && req.url === fault.path
        && req.headers["x-aioffice-company-id"] === fault.company && upstream.status === 200) {
        const input = JSON.parse(body.toString("utf8")), receipt = JSON.parse(bytes.toString("utf8"));
        if (input.expectedVersion === fault.expectedVersion && input.isAdministrator === fault.isAdministrator
          && /^[0-9a-f-]{36}$/.test(input.operationId) && receipt.operationId === input.operationId
          && receipt.companyId === fault.company && receipt.userId === fault.user
          && receipt.isAdministrator === input.isAdministrator) {
          // The real upstream receipt has fully arrived after its SQL commit.
          // Record only owned fixture metadata, then lose the downstream reply.
          writeFileSync(control, JSON.stringify({ ...fault, count: 1, operationId: input.operationId,
            membershipVersion: receipt.membershipVersion, upstreamStatus: upstream.status }), { mode: 0o600 });
          if (fault.boundary === "body") {
            res.writeHead(200, { "Content-Type": "application/json", "Content-Length": String(bytes.length) });
            res.flushHeaders(); res.write(bytes.subarray(0, 1)); setTimeout(() => res.destroy(), 50);
          } else res.destroy();
          return;
        }
      }
      const forwarded = { "Content-Type": upstream.headers.get("content-type") ?? "application/json", "Cache-Control": "no-store" };
      res.writeHead(upstream.status, forwarded); res.end(bytes);
    } catch { res.destroy(); }
  });
  await new Promise((resolve, reject) => { server.once("error", reject); server.listen(8099, "127.0.0.1", resolve); });
  writeFileSync(directory + "/ready", "ready", { mode: 0o600 });
  return server;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  startOwnedCoreReplyProxy(process.argv[2]).catch(() => { process.exitCode = 1; });
}
