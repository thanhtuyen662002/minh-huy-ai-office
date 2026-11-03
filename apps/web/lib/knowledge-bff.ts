import { fetchCoreApi, isCanonicalCompanyId, isOfficeAiUiEnabled, localUiDisabledResponse,
  officeMutationIsSameOrigin, officeSessionIsCurrent, unauthenticatedResponse } from "./local-ai-bff";
import { readBoundedRequestJson } from "./bounded-request-json";
import { parseSubmissionJson } from "./task-submission-intent";
import { isKnowledgeInput, knowledgeFileLimits, knowledgeFileName, knowledgeLimits, knowledgeVersion, parseKnowledgeExtract, parseKnowledgeList,
  parseKnowledgePage } from "./knowledge-pages";

const headers = { "Cache-Control": "no-store" };
// A full page as JSON: up to three UTF-8 bytes per character plus escapes.
const PAGE_JSON_BYTES = 6 * knowledgeLimits.body + 4096;
const CORE_JSON_BYTES = 8 * 1024 * 1024;
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
      const body = await readBoundedRequestJson({ body: request.body, headers: request.headers, signal }, PAGE_JSON_BYTES, parseSubmissionJson);
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
      const body = await readBoundedRequestJson({ body: response.body, headers: response.headers, signal }, CORE_JSON_BYTES, parseSubmissionJson);
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

const fileFailure = (status: number, reason?: string) => Response.json({ error: "Không đọc được file này.", ...(reason ? { reason } : {}) }, { status, headers });
const reasons = ["unsupported", "legacy", "empty", "encrypted", "damaged", "tooLarge"];

/** Reads the raw upload with a hard byte bound; the browser sends the file itself as the body. */
async function readBoundedBytes(body: ReadableStream<Uint8Array> | null, maxBytes: number): Promise<Uint8Array<ArrayBuffer> | "tooLarge" | null> {
  const reader = body?.getReader();
  if (!reader) return null;
  const chunks: Uint8Array[] = [];
  let length = 0;
  try {
    while (true) {
      const chunk = await reader.read();
      if (chunk.done) break;
      length += chunk.value.byteLength;
      if (length > maxBytes) { void reader.cancel().catch(() => {}); return "tooLarge"; }
      chunks.push(chunk.value);
    }
  } catch { return null; }
  finally { reader.releaseLock(); }
  const bytes = new Uint8Array(length);
  let offset = 0;
  for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.byteLength; }
  return bytes;
}

// POST a file (PDF, Word, Excel, ...) and get its text back for review; nothing is saved here.
export async function knowledgeExtractBff(request: Request): Promise<Response> {
  if (!isOfficeAiUiEnabled()) return localUiDisabledResponse();
  if (!officeMutationIsSameOrigin(request)) return fileFailure(403);
  const query = new URL(request.url).searchParams, company = query.get("companyId"), name = query.get("fileName");
  if (request.method !== "POST" || !isCanonicalCompanyId(company) || !knowledgeFileName(name)
    || [...query.keys()].some(key => !["companyId", "fileName"].includes(key) || query.getAll(key).length !== 1)
    || request.headers.get("content-type")?.split(";")[0].trim().toLowerCase() !== "application/octet-stream") return fileFailure(400);
  const declared = Number(request.headers.get("content-length") ?? "0");
  if (declared > knowledgeFileLimits.bytes) return fileFailure(422, "tooLarge");
  const companyId = company.toLowerCase(), signal = AbortSignal.any([request.signal, AbortSignal.timeout(90_000)]);
  try {
    const bytes = await bounded(readBoundedBytes(request.body, knowledgeFileLimits.bytes), signal);
    if (bytes === "tooLarge") return fileFailure(422, "tooLarge");
    if (!bytes || bytes.byteLength === 0) return fileFailure(bytes ? 422 : 400, bytes ? "empty" : undefined);
    const path = `/api/companies/${companyId}/knowledge-pages/extract?fileName=${encodeURIComponent(name)}`;
    const response = await bounded(fetchCoreApi(path, companyId,
      { signal, redirect: "error", method: "POST", headers: { "Content-Type": "application/octet-stream" }, body: bytes }, 60_000), signal);
    if (!response) return unauthenticatedResponse();
    const body = await readBoundedRequestJson({ body: response.body, headers: response.headers, signal }, CORE_JSON_BYTES, parseSubmissionJson);
    if (response.status === 422) {
      const reason = body.ok && body.value && typeof body.value === "object" ? (body.value as { reason?: unknown }).reason : undefined;
      return fileFailure(422, typeof reason === "string" && reasons.includes(reason) ? reason : "damaged");
    }
    if (response.status !== 200) return fileFailure([400, 401, 403, 404, 503].includes(response.status) ? response.status : 502);
    const extract = body.ok ? parseKnowledgeExtract(body.value) : null;
    if (!extract) return fileFailure(502);
    const current = await bounded(officeSessionIsCurrent(companyId), signal);
    if (signal.aborted) return fileFailure(503);
    if (!current) return unauthenticatedResponse();
    return Response.json(extract, { headers });
  } catch { return fileFailure(503); }
}
