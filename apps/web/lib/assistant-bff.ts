import { fetchCoreApi, isCanonicalCompanyId, isOfficeAiUiEnabled, localUiDisabledResponse, officeMutationIsSameOrigin,
  officeSessionIsCurrent, unauthenticatedResponse } from "./local-ai-bff";
import { readBoundedRequestJson } from "./bounded-request-json";
import { sniffImageType } from "./bot-messages-bff";
import { assistantImageTypes, maxAssistantImageBytes } from "./assistant-images";

const headers = { "Cache-Control": "no-store" };
const failure = (status: number) => Response.json({ error: "Trợ lý chưa xử lý được yêu cầu này." }, { status, headers });
const guid = (value: unknown): value is string => isCanonicalCompanyId(value);
const types: readonly string[] = assistantImageTypes;

async function readBounded(body: ReadableStream<Uint8Array> | null, max: number, signal: AbortSignal): Promise<Uint8Array | null | "tooLarge"> {
  if (!body) return null;
  const reader = body.getReader(), parts: Uint8Array[] = []; let size = 0;
  try {
    for (;;) {
      if (signal.aborted) return null;
      const { done, value } = await reader.read(); if (done) break;
      size += value.byteLength; if (size > max) return "tooLarge"; parts.push(value);
    }
  } finally { void reader.cancel().catch(() => {}); }
  const all = new Uint8Array(size); let at = 0; for (const part of parts) { all.set(part, at); at += part.byteLength; }
  return all;
}

function companyOf(request: Request, allowed: readonly string[] = ["companyId"]): string | null {
  const query = new URL(request.url).searchParams, company = query.get("companyId");
  if (!isCanonicalCompanyId(company) || [...query.keys()].some(key => !allowed.includes(key) || query.getAll(key).length !== 1)) return null;
  return company.toLowerCase();
}

/** POST one picture (raw bytes with its image type) for a Trợ lý question; returns its id. */
export async function assistantImageUploadBff(request: Request): Promise<Response> {
  if (!isOfficeAiUiEnabled()) return localUiDisabledResponse();
  if (!officeMutationIsSameOrigin(request)) return failure(403);
  const companyId = companyOf(request), type = request.headers.get("content-type")?.split(";")[0].trim().toLowerCase() ?? "";
  if (request.method !== "POST" || !companyId || !types.includes(type)) return failure(400);
  if (Number(request.headers.get("content-length") ?? "0") > maxAssistantImageBytes) return failure(413);
  const signal = AbortSignal.any([request.signal, AbortSignal.timeout(60_000)]);
  try {
    const bytes = await readBounded(request.body, maxAssistantImageBytes, signal);
    if (bytes === "tooLarge") return failure(413);
    if (!bytes || sniffImageType(bytes) !== type) return failure(400);
    const response = await fetchCoreApi(`/api/companies/${companyId}/assistant-images`, companyId,
      { signal, redirect: "error", method: "POST", headers: { "Content-Type": type }, body: bytes as BodyInit }, 60_000);
    if (!response) return unauthenticatedResponse();
    const body = await readBoundedRequestJson({ body: response.body, headers: response.headers, signal }, 4096);
    if (response.status !== 200) return failure([400, 401, 403, 413, 429, 503].includes(response.status) ? response.status : 502);
    const value = body.ok ? body.value as { id?: unknown; contentType?: unknown; bytes?: unknown } : null;
    if (!value || !guid(value.id) || value.contentType !== type || value.bytes !== bytes.byteLength) return failure(502);
    return Response.json({ id: value.id.toLowerCase(), contentType: type, bytes: bytes.byteLength }, { headers });
  } catch { return failure(503); }
}

/** GET one of the member's own pictures. Pictures never change, so the browser may keep a private copy. */
export async function assistantImageBff(request: Request, id: string): Promise<Response> {
  if (!isOfficeAiUiEnabled()) return localUiDisabledResponse();
  const companyId = companyOf(request);
  if (request.method !== "GET" || !companyId || !guid(id)) return new Response(null, { status: 400, headers });
  const signal = AbortSignal.any([request.signal, AbortSignal.timeout(15_000)]);
  try {
    const response = await fetchCoreApi(`/api/companies/${companyId}/assistant-images/${id.toLowerCase()}`, companyId,
      { signal, redirect: "error", method: "GET" }, 15_000);
    if (!response) return unauthenticatedResponse();
    if (response.status !== 200) {
      void response.body?.cancel().catch(() => {});
      return new Response(null, { status: [400, 401, 403, 404, 503].includes(response.status) ? response.status : 502, headers });
    }
    const declared = (response.headers.get("content-type") ?? "").split(";")[0].trim().toLowerCase();
    const bytes = await readBounded(response.body, maxAssistantImageBytes, signal);
    if (!bytes || bytes === "tooLarge" || !types.includes(declared) || sniffImageType(bytes) !== declared) return new Response(null, { status: 502, headers });
    if (!await officeSessionIsCurrent(companyId)) return unauthenticatedResponse();
    return new Response(bytes as BodyInit, { headers: { "Content-Type": declared, "Cache-Control": "private, max-age=86400",
      "X-Content-Type-Options": "nosniff", "Content-Security-Policy": "default-src 'none'; sandbox", "Content-Length": String(bytes.byteLength) } });
  } catch { return new Response(null, { status: 503, headers }); }
}

/** POST {taskIds} to delete the member's own questions from their Trợ lý history. */
export async function assistantHideBff(request: Request): Promise<Response> {
  if (!isOfficeAiUiEnabled()) return localUiDisabledResponse();
  if (!officeMutationIsSameOrigin(request)) return failure(403);
  const companyId = companyOf(request);
  if (request.method !== "POST" || !companyId) return failure(400);
  const input = await readBoundedRequestJson(request, 8 * 1024);
  const ids = input.ok && input.value && typeof input.value === "object" && !Array.isArray(input.value)
    && Object.keys(input.value).length === 1 ? (input.value as { taskIds?: unknown }).taskIds : null;
  if (!Array.isArray(ids) || ids.length === 0 || ids.length > 100 || !ids.every(guid)) return failure(400);
  try {
    const response = await fetchCoreApi(`/api/companies/${companyId}/assistant-history/hide`, companyId, {
      method: "POST", redirect: "error", headers: { "Content-Type": "application/json; charset=utf-8" },
      body: JSON.stringify({ taskIds: ids.map(id => id.toLowerCase()) }) }, 15_000);
    if (!response) return unauthenticatedResponse();
    const body = await readBoundedRequestJson({ body: response.body, headers: response.headers, signal: AbortSignal.timeout(15_000) }, 1024);
    if (response.status !== 200) return failure([400, 401, 403, 503].includes(response.status) ? response.status : 502);
    const hidden = body.ok ? (body.value as { hidden?: unknown })?.hidden : null;
    if (typeof hidden !== "number" || !Number.isSafeInteger(hidden) || hidden < 0 || hidden > ids.length) return failure(502);
    return Response.json({ hidden }, { headers });
  } catch { return failure(503); }
}
