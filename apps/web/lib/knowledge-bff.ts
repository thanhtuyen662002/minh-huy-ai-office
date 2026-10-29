import { fetchCoreApi, isCanonicalCompanyId, isOfficeAiUiEnabled, localUiDisabledResponse,
  officeMutationIsSameOrigin, officeSessionIsCurrent, unauthenticatedResponse } from "./local-ai-bff";
import { readBoundedRequestJson } from "./bounded-request-json";
import { parseSubmissionJson } from "./task-submission-intent";
import { isKnowledgeInput, knowledgeVersion, parseKnowledgeList, parseKnowledgePage } from "./knowledge-pages";

const headers = { "Cache-Control": "no-store" };
const failure = (status: number) => Response.json({ error: "Không đọc hoặc lưu được trang kiến thức. Hãy tải lại sau." }, { status, headers });
async function bounded<T>(operation: Promise<T>, signal: AbortSignal): Promise<T> {
  let abort: (() => void) | undefined;
  try {
    return await Promise.race([operation, new Promise<never>((_resolve, reject) => {
      abort = () => reject(new Error("Knowledge request ended."));
      signal.addEventListener("abort", abort, { once: true });
      if (signal.aborted) abort();
    })]);
  } finally { if (abort) signal.removeEventListener("abort", abort); }
}

// GET/POST on the collection, GET/PUT/DELETE on one page; the browser never reaches Core.Api directly.
export async function knowledgeBff(request: Request, id?: string): Promise<Response> {
  if (!isOfficeAiUiEnabled()) return localUiDisabledResponse();
  const method = request.method, query = new URL(request.url).searchParams, company = query.get("companyId");
  const mutation = method !== "GET";
  if (mutation && !officeMutationIsSameOrigin(request)) return failure(403);
  const allowed = method === "DELETE" ? ["companyId", "rowVersion"] : ["companyId"];
  if (!isCanonicalCompanyId(company) || id !== undefined && !isCanonicalCompanyId(id)
    || (id === undefined ? !["GET", "POST"].includes(method) : !["GET", "PUT", "DELETE"].includes(method))
    || [...query.keys()].some(key => !allowed.includes(key) || query.getAll(key).length !== 1)
    || method === "DELETE" && !knowledgeVersion(query.get("rowVersion"))) return failure(400);
  const companyId = company.toLowerCase(), signal = AbortSignal.any([request.signal, AbortSignal.timeout(10000)]);
  try {
    let init: RequestInit = { signal, redirect: "error", method };
    if (method === "POST" || method === "PUT") {
      const body = await readBoundedRequestJson({ body: request.body, headers: request.headers, signal }, 128 * 1024, parseSubmissionJson);
      if (!body.ok) return failure(body.status);
      if (!isKnowledgeInput(body.value, method === "PUT")) return failure(400);
      init = { ...init, headers: { "Content-Type": "application/json" }, body: JSON.stringify(body.value) };
    }
    const root = `/api/companies/${companyId}/knowledge-pages`;
    const path = id === undefined ? root : `${root}/${id.toLowerCase()}${method === "DELETE" ? `?rowVersion=${encodeURIComponent(query.get("rowVersion")!)}` : ""}`;
    const response = await bounded(fetchCoreApi(path, companyId, init), signal);
    if (!response) return unauthenticatedResponse();
    if (method === "DELETE" ? response.status !== 204 : response.status !== 200) {
      void response.body?.cancel().catch(() => {});
      return failure([400, 401, 403, 404, 409, 503].includes(response.status) ? response.status : 502);
    }
    let view: unknown = null;
    if (method !== "DELETE") {
      const body = await readBoundedRequestJson({ body: response.body, headers: response.headers, signal }, 2 * 1024 * 1024, parseSubmissionJson);
      if (!body.ok) return failure(502);
      view = id === undefined && method === "GET" ? parseKnowledgeList(body.value) : parseKnowledgePage(body.value, id);
      if (!view) return failure(502);
    }
    const current = await bounded(officeSessionIsCurrent(companyId), signal);
    if (signal.aborted) return failure(503);
    if (!current) return unauthenticatedResponse();
    return method === "DELETE" ? new Response(null, { status: 204, headers }) : Response.json(view, { headers });
  } catch { return failure(503); }
}
