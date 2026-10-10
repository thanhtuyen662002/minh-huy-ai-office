// Owned CI diagnostics only: observe the original signal; never abort, retry,
// replace a response or inspect a body/cookie/authorization value.
import { resolve, join } from "node:path";

export function observeSubmissionLifecycle({ origin, company }) {
  if (origin !== "http://127.0.0.1:3000" || window.location.origin !== origin
    || !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(company)
    || window.__aiofficeOwnedSubmissionLifecycle) throw new Error("Owned browser observation refused.");
  const original = window.fetch, records = [];
  let exhausted = false;
  function observed(...args) {
    let record;
    try {
      const [input, init] = args, url = typeof input === "string" ? new URL(input, origin) : null;
      if (url?.origin === origin && /^\/api\/local\/tasks\/intents\/[0-9a-f-]{36}\/submit$/.test(url.pathname)
        && url.search === "?companyId=" + company && init?.method === "POST" && init.signal instanceof AbortSignal) {
        if (records.length >= 8) exhausted = true;
        else {
          const signal = init.signal;
          record = { url: url.href, started: performance.now(), headers: null, aborted: null, kind: "absent", signal };
          record.abort = () => {
            record.aborted = performance.now();
            try {
              record.kind = signal.reason instanceof DOMException && signal.reason.name === "TimeoutError" ? "timeout"
                : signal.reason instanceof DOMException && signal.reason.name === "AbortError" ? "owner" : "other";
            } catch { record.kind = "other"; }
          };
          records.push(record); signal.addEventListener("abort", record.abort, { once: true });
          if (signal.aborted) record.abort();
        }
      }
    } catch { /* Observation must not change the original request. */ }
    const result = Reflect.apply(original, this, args);
    if (!record) return result;
    return result.then(response => { record.headers = performance.now(); return response; });
  }
  window.fetch = observed;
  window.__aiofficeOwnedSubmissionLifecycle = {
    read(url) {
      if (exhausted) return null;
      const record = records.findLast(item => item.url === url);
      return record ? { kind: record.kind,
        headersBeforeAbort: record.headers !== null && record.aborted !== null && record.headers <= record.aborted,
        headersNearDeadline: record.headers !== null && record.headers - record.started >= 9000 } : null;
    },
    dispose() {
      for (const record of records) record.signal.removeEventListener("abort", record.abort);
      if (window.fetch === observed) window.fetch = original;
      delete window.__aiofficeOwnedSubmissionLifecycle;
    },
  };
}

export function submissionAbortStage(evidence) {
  // Project only exact fixed categories; hostile/accessor/unknown input cannot
  // become a log message. Every category remains a strict proof refusal.
  if (!evidence || typeof evidence !== "object") return "replay-request-aborted";
  const keys = Object.keys(evidence);
  if (keys.length !== 3 || !["kind", "headersBeforeAbort", "headersNearDeadline"].every(key => keys.includes(key))) return "replay-request-aborted";
  const values = keys.map(key => Object.getOwnPropertyDescriptor(evidence, key));
  if (values.some(value => !value || !("value" in value))) return "replay-request-aborted";
  if (typeof evidence.headersBeforeAbort !== "boolean" || typeof evidence.headersNearDeadline !== "boolean") return "replay-request-aborted";
  if (evidence.kind === "timeout" && evidence.headersBeforeAbort) return evidence.headersNearDeadline
    ? "replay-timeout-near-deadline-headers" : "replay-timeout-after-headers";
  if (evidence.kind === "owner" && evidence.headersBeforeAbort) return "replay-owner-abort-after-headers";
  return "replay-request-aborted";
}

/** Observe Chromium wire metadata without interception, body access or sends. */
export async function observeSubmissionWire(context, page, { directory, origin, company }) {
  if (process.env.CI !== "true" || process.env.GITHUB_ACTIONS !== "true" || !process.env.RUNNER_TEMP
    || resolve(directory) !== join(resolve(process.env.RUNNER_TEMP), "aioffice-local")
    || origin !== "http://127.0.0.1:3000" || !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(company)
    || company === "00000000-0000-0000-0000-000000000000") throw new Error("Owned browser wire observation refused.");
  const session = await context.newCDPSession(page), records = [];
  let exhausted = false;
  const handlers = {
    "Network.requestWillBeSent": event => {
      const request = event.request;
      let url; try { url = new URL(request.url); } catch { return; }
      if (request.method === "POST" && url.origin === origin
        && /^\/api\/local\/tasks\/intents\/[0-9a-f-]{36}\/submit$/.test(url.pathname) && url.search === "?companyId=" + company) {
        if (records.length >= 8) { exhausted = true; return; }
        records.push({ id: event.requestId, url: url.href, bodyStarted: false, wireFinished: false, compressed: false });
      }
    },
    "Network.responseReceived": event => {
      const record = records.find(item => item.id === event.requestId);
      if (!record) return;
      // Project only a fixed encoding category, never arbitrary header values.
      const header = Object.keys(event.response.headers).find(key => key.toLowerCase() === "content-encoding");
      const encoding = header ? event.response.headers[header] : undefined;
      record.compressed = ["gzip", "br", "deflate", "zstd"].includes(encoding);
    },
    "Network.dataReceived": event => {
      const record = records.find(item => item.id === event.requestId);
      if (record && Number.isSafeInteger(event.dataLength) && event.dataLength > 0) record.bodyStarted = true;
    },
    "Network.loadingFinished": event => {
      const record = records.find(item => item.id === event.requestId);
      if (record) record.wireFinished = true;
    },
  };
  for (const [name, handler] of Object.entries(handlers)) session.on(name, handler);
  const dispose = async () => {
    for (const [name, handler] of Object.entries(handlers)) session.off(name, handler);
    await session.detach();
  };
  try { await session.send("Network.enable"); }
  catch (error) { await dispose().catch(() => {}); throw error; }
  return {
    read(url) {
      if (exhausted) return null;
      const record = records.findLast(item => item.url === url);
      return record ? { bodyStarted: record.bodyStarted, wireFinished: record.wireFinished, compressed: record.compressed } : null;
    }, dispose,
  };
}

export function submissionWireStage(stage, evidence) {
  if (stage !== "replay-timeout-after-headers") return stage;
  if (!evidence || typeof evidence !== "object" || Object.keys(evidence).length !== 4
    || !["bodyStarted", "wireFinished", "compressed", "acceptedUi"].every(key => {
      const value = Object.getOwnPropertyDescriptor(evidence, key);
      return value && "value" in value && typeof value.value === "boolean";
    })) return stage;
  if (evidence.acceptedUi) return "replay-timeout-after-accepted-ui";
  if (evidence.wireFinished) return "replay-timeout-after-wire-finished";
  if (evidence.bodyStarted) return evidence.compressed ? "replay-timeout-compressed-body-started" : "replay-timeout-body-started";
  return "replay-timeout-no-body-observed";
}
