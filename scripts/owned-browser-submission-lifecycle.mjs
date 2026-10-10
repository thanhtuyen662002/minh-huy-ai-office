// Owned CI diagnostics only: observe the original signal; never abort, retry,
// replace a response or inspect a body/cookie/authorization value.
export function observeSubmissionLifecycle({ origin, company }) {
  if (origin !== "http://127.0.0.1:3000" || window.location.origin !== origin
    || !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(company)
    || window.__aiofficeOwnedSubmissionLifecycle) throw new Error("Owned browser observation refused.");
  const original = window.fetch, records = [];
  function observed(...args) {
    let record;
    try {
      const [input, init] = args, url = typeof input === "string" ? new URL(input, origin) : null;
      if (url?.origin === origin && /^\/api\/local\/tasks\/intents\/[0-9a-f-]{36}\/submit$/.test(url.pathname)
        && url.search === "?companyId=" + company && init?.method === "POST" && init.signal instanceof AbortSignal
        && records.length < 8) {
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
    } catch { /* Observation must not change the original request. */ }
    const result = Reflect.apply(original, this, args);
    if (!record) return result;
    return result.then(response => { record.headers = performance.now(); return response; });
  }
  window.fetch = observed;
  window.__aiofficeOwnedSubmissionLifecycle = {
    read(url) {
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
