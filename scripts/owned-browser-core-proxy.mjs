// Disposable CI transport only. No public listener, production hook or logs.
import { createServer } from "node:http";
import { readFileSync, writeFileSync } from "node:fs";
import { pathToFileURL } from "node:url";
import { createHash } from "node:crypto";

const guid = value => typeof value === "string" && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(value)
  && value !== "00000000-0000-0000-0000-000000000000";

// Match a fully received real Core response before losing it downstream.
// This helper cannot produce a receipt or change an upstream request.
export function committedReplyEvidence(fault, request, upstreamStatus, input, receipt) {
  if (fault?.count !== 0 || request.method !== "POST" || request.headers["x-aioffice-company-id"] !== fault.company
    || !guid(fault.company) || !input || !receipt) return null;
  if (!fault.kind) {
    if (request.url !== fault.path || upstreamStatus !== 200 || input.expectedVersion !== fault.expectedVersion
      || input.isAdministrator !== fault.isAdministrator || !guid(input.operationId) || receipt.operationId !== input.operationId
      || receipt.companyId !== fault.company || receipt.userId !== fault.user || receipt.isAdministrator !== input.isAdministrator) return null;
    return { operationId: input.operationId, membershipVersion: receipt.membershipVersion, upstreamStatus };
  }
  if (!guid(fault.source) || typeof fault.question !== "string") return null;
  const question = Buffer.from(fault.question, "utf8"), size = Buffer.alloc(8);
  size.writeUInt32LE(3); size.writeUInt32LE(question.length, 4);
  const fingerprint = createHash("sha256").update("aioffice-task-intent-v1\0", "ascii")
    .update(fault.source.replaceAll("-", ""), "ascii").update(size).update(question).digest("hex").toUpperCase();
  if (receipt.companyId !== fault.company || receipt.dataSourceId !== fault.source || receipt.inputFingerprint !== fingerprint
    || !guid(receipt.operationId) || !Number.isFinite(Date.parse(receipt.createdAtUtc))) return null;
  if (fault.kind === "intent-prepare") {
    if (request.url !== "/api/tasks/intents" || upstreamStatus !== 200 || !guid(input.operationId)
      || receipt.operationId !== input.operationId || input.question !== fault.question || input.dataSourceId !== fault.source
      || receipt.question !== fault.question || receipt.state !== 0 || receipt.accepted !== null
      || Date.parse(receipt.expiresAtUtc) - Date.parse(receipt.createdAtUtc) !== 86400000) return null;
    return { operationId: receipt.operationId, inputFingerprint: fingerprint, upstreamStatus };
  }
  if (fault.kind === "task-submit") {
    if (request.url !== `/api/tasks/intents/${receipt.operationId}/submit` || upstreamStatus !== 202
      || Object.keys(input).join(",") !== "inputFingerprint" || input.inputFingerprint !== fingerprint
      || ![receipt.taskId, receipt.stepId, receipt.messageId].every(guid)
      || !Number.isInteger(receipt.status) || receipt.status < 0 || receipt.status > 6
      || !Number.isInteger(receipt.dispatchState) || receipt.dispatchState < 0 || receipt.dispatchState > 3) return null;
    return { operationId: receipt.operationId, inputFingerprint: fingerprint, taskId: receipt.taskId,
      stepId: receipt.stepId, messageId: receipt.messageId, createdAtUtc: receipt.createdAtUtc, upstreamStatus };
  }
  return null;
}

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
      if (fault?.count === 0 && req.method === "POST") {
        let evidence;
        try { evidence = committedReplyEvidence(fault, req, upstream.status, JSON.parse(body.toString("utf8")), JSON.parse(bytes.toString("utf8"))); }
        catch { /* An unrelated or non-JSON response must remain transparent. */ }
        if (evidence) {
          // The real upstream receipt has fully arrived after its SQL commit.
          // Record only owned fixture metadata, then lose the downstream reply.
          writeFileSync(control, JSON.stringify({ ...fault, count: 1, ...evidence }), { mode: 0o600 });
          if (fault.boundary === "body") {
            res.writeHead(upstream.status, { "Content-Type": "application/json", "Content-Length": String(bytes.length) });
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
