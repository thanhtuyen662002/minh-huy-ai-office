// Server log for a web-side 503 that never reached Core's own log. Only the route
// shape, failure kind and elapsed time are written: no ids, query, token or body.
export function reportCoreCallFailure(path: string, error: unknown, startedAt: number) {
  const route = path.split("?")[0].replace(/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/gi, "{id}").replace(/\/YC-[0-9]+/g, "/{code}");
  const name = error && typeof error === "object" && "name" in error ? String(error.name) : "unknown";
  const kind = name === "TimeoutError" ? "timeout" : name === "AbortError" ? "aborted" : name === "TypeError" ? "network" : "error";
  console.warn(`[aioffice-core-call] ${route} answered 503 after ${kind} (${Math.round(performance.now() - startedAt)} ms)`);
}
