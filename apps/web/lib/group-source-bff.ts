import { fetchCoreApi, isCanonicalCompanyId, isOfficeAiUiEnabled, localUiDisabledResponse, officeSessionIsCurrent, unauthenticatedResponse } from "./local-ai-bff";
import { readBoundedRequestJson } from "./bounded-request-json";
import { parseSubmissionJson as parsePrivateJson } from "./task-submission-intent";
import { parseGroupMessage, parseGroupMessages, parseGroupSources } from "./group-source";

type Route = { kind: "sources" } | { kind: "messages"; sourceId: string } | { kind: "message"; sourceId: string; messageId: string };
const headers = { "Cache-Control": "no-store" };
const failure = (status: number) => Response.json({ error: "Không đọc được hộp thư nguồn. Hãy tải lại sau." }, { status, headers });
const number = (value: string, minimum: number, maximum: number) => /^(0|[1-9][0-9]*)$/.test(value)
  && Number.isSafeInteger(Number(value)) && Number(value) >= minimum && Number(value) <= maximum;
const canonical = (value: string) => isCanonicalCompanyId(value) && value === value.toLowerCase();

async function bounded<T>(operation: Promise<T>, signal: AbortSignal): Promise<T> {
  let abort: (() => void) | undefined;
  try {
    return await Promise.race([operation, new Promise<never>((_resolve, reject) => {
      abort = () => reject(new Error("Inbox read ended."));
      signal.addEventListener("abort", abort, { once: true });
      if (signal.aborted) abort();
    })]);
  } finally { if (abort) signal.removeEventListener("abort", abort); }
}

/** Read-only private inbox; provider HMAC/client headers never become browser authority. */
export async function groupSourceBff(request: Request, route: Route): Promise<Response> {
  if (!isOfficeAiUiEnabled()) return localUiDisabledResponse();
  const query = new URL(request.url).searchParams, company = query.get("companyId");
  const allowed = ["companyId", ...(route.kind === "sources" ? ["offset", "limit"] : route.kind === "messages" ? ["beforeSequence", "limit"] : [])];
  if (!isCanonicalCompanyId(company) || [...query.keys()].some(key => !allowed.includes(key) || query.getAll(key).length !== 1)
    || "sourceId" in route && !canonical(route.sourceId) || "messageId" in route && !canonical(route.messageId)) return failure(400);
  const offset = query.get("offset") ?? "0", limit = query.get("limit") ?? "25", before = query.get("beforeSequence");
  if (route.kind !== "message" && !number(limit, 1, 25) || route.kind === "sources" && !number(offset, 0, 10000)
    || route.kind === "messages" && before !== null && !number(before, 1, Number.MAX_SAFE_INTEGER)) return failure(400);
  const companyId = company.toLowerCase();
  const path = route.kind === "sources" ? `/api/group-sources?offset=${offset}&limit=${limit}`
    : `/api/group-sources/${route.sourceId}/messages${route.kind === "message" ? `/${route.messageId}` : `?limit=${limit}${before === null ? "" : `&beforeSequence=${before}`}`}`;
  const signal = AbortSignal.any([request.signal, AbortSignal.timeout(10000)]);
  try {
    const response = await bounded(fetchCoreApi(path, companyId, { signal }), signal);
    if (!response) return unauthenticatedResponse();
    if (response.status !== 200) {
      void response.body?.cancel().catch(() => {});
      return failure([400, 401, 403, 404, 503].includes(response.status) ? response.status : 502);
    }
    const body = await readBoundedRequestJson({ body: response.body, headers: response.headers, signal }, 128 * 1024, parsePrivateJson);
    if (!body.ok) return failure(502);
    const view = route.kind === "sources" ? parseGroupSources(body.value, companyId, Number(limit))
      : route.kind === "messages" ? parseGroupMessages(body.value, companyId, route.sourceId, Number(limit), before === null ? undefined : Number(before))
      : parseGroupMessage(body.value, companyId, route.sourceId, route.messageId);
    if (!view) return failure(502);
    if (signal.aborted) return failure(503);
    // The shared helper fences after network buffering; this fence also covers
    // private body parsing in password development mode and final release.
    const current = await bounded(officeSessionIsCurrent(companyId), signal);
    if (signal.aborted) return failure(503);
    return current ? Response.json(view, { headers }) : unauthenticatedResponse();
  } catch { return failure(503); }
}
