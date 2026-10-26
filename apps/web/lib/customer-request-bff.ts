import { fetchCoreApi, isCanonicalCompanyId, isOfficeAiUiEnabled, localUiDisabledResponse,
  officeMutationIsSameOrigin, officeSessionIsCurrent, unauthenticatedResponse } from "./local-ai-bff";
import { readBoundedRequestJson } from "./bounded-request-json";
import { parseSubmissionJson } from "./task-submission-intent";
import { isCustomerRequestPatch, isRequestCode, isRequestStatus, parseCustomerRequest, parseCustomerRequestDetail, parseCustomerRequestPage } from "./customer-requests";

const headers = { "Cache-Control": "no-store" };
const failure = (status: number) => Response.json({ error: "Không đọc hoặc cập nhật được phiếu. Hãy tải lại sau." }, { status, headers });
const number = (value: string, max: number, min = 0) => /^(0|[1-9][0-9]*)$/.test(value) && Number(value) >= min && Number(value) <= max;
async function bounded<T>(operation: Promise<T>, signal: AbortSignal): Promise<T> {
  let abort: (() => void) | undefined;
  try {
    return await Promise.race([operation, new Promise<never>((_resolve, reject) => {
      abort = () => reject(new Error("Customer request ended."));
      signal.addEventListener("abort", abort, { once: true });
      if (signal.aborted) abort();
    })]);
  } finally { if (abort) signal.removeEventListener("abort", abort); }
}

export async function customerRequestBff(request: Request, code?: string): Promise<Response> {
  if (!isOfficeAiUiEnabled()) return localUiDisabledResponse();
  const patch = request.method === "PATCH", query = new URL(request.url).searchParams, company = query.get("companyId");
  if (patch && !officeMutationIsSameOrigin(request)) return failure(403);
  const allowed = code === undefined ? ["companyId", "status", "customer", "offset", "limit"] : ["companyId"];
  if (!isCanonicalCompanyId(company) || code !== undefined && !isRequestCode(code) || patch && code === undefined
    || [...query.keys()].some(key => !allowed.includes(key) || query.getAll(key).length !== 1)) return failure(400);
  const offset = query.get("offset") ?? "0", limit = query.get("limit") ?? "25", status = query.get("status"), customer = query.get("customer");
  if (!number(offset, 100000) || !number(limit, 100, 1) || status !== null && !isRequestStatus(status)
    || customer !== null && (!customer.trim() || customer.length > 200 || customer.includes("\0"))) return failure(400);
  const companyId = company.toLowerCase(), signal = AbortSignal.any([request.signal, AbortSignal.timeout(10000)]);
  try {
    let init: RequestInit = { signal, redirect: "error" };
    if (patch) {
      const body = await readBoundedRequestJson({ body: request.body, headers: request.headers, signal }, 2048, parseSubmissionJson);
      if (!body.ok) return failure(body.status);
      if (!isCustomerRequestPatch(body.value)) return failure(400);
      init = { ...init, method: "PATCH", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body.value) };
    }
    const filters = new URLSearchParams({ offset, limit });
    if (status !== null) filters.set("status", status);
    if (customer !== null) filters.set("customer", customer);
    const path = `/api/companies/${companyId}/customer-requests${code === undefined ? `?${filters}` : `/${encodeURIComponent(code)}`}`;
    const response = await bounded(fetchCoreApi(path, companyId, init), signal);
    if (!response) return unauthenticatedResponse();
    if (response.status !== 200) {
      void response.body?.cancel().catch(() => {});
      return failure([400, 401, 403, 404, 409, 503].includes(response.status) ? response.status : 502);
    }
    // A 100-row page and a maximally populated detail can exceed 128 KB in Vietnamese UTF-8.
    const body = await readBoundedRequestJson({ body: response.body, headers: response.headers, signal }, 8 * 1024 * 1024, parseSubmissionJson);
    if (!body.ok) return failure(502);
    const view = code === undefined ? parseCustomerRequestPage(body.value, Number(offset), Number(limit))
      : patch ? parseCustomerRequest(body.value, code) : parseCustomerRequestDetail(body.value, code);
    if (!view) return failure(502);
    const current = await bounded(officeSessionIsCurrent(companyId), signal);
    if (signal.aborted) return failure(503);
    return current ? Response.json(view, { headers }) : unauthenticatedResponse();
  } catch { return failure(503); }
}
