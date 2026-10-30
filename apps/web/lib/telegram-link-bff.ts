import { fetchCoreApi, isCanonicalCompanyId, isOfficeAiUiEnabled, localUiDisabledResponse,
  officeMutationIsSameOrigin, officeSessionIsCurrent, unauthenticatedResponse } from "./local-ai-bff";
import { readBoundedRequestJson } from "./bounded-request-json";
import { parseSubmissionJson } from "./task-submission-intent";
import { parseTelegramLink, parseTelegramLinkCode } from "./telegram-link";

const headers = { "Cache-Control": "no-store" };
const failure = (status: number) => Response.json({ error: "Không đọc hoặc đổi được liên kết Telegram. Hãy thử lại sau." }, { status, headers });
async function bounded<T>(operation: Promise<T>, signal: AbortSignal): Promise<T> {
  let abort: (() => void) | undefined;
  try {
    return await Promise.race([operation, new Promise<never>((_resolve, reject) => {
      abort = () => reject(new Error("Telegram link request ended."));
      signal.addEventListener("abort", abort, { once: true });
      if (signal.aborted) abort();
    })]);
  } finally { if (abort) signal.removeEventListener("abort", abort); }
}

// GET reads the member's link, POST creates a one-time code for the bot, DELETE unlinks.
export async function telegramLinkBff(request: Request): Promise<Response> {
  if (!isOfficeAiUiEnabled()) return localUiDisabledResponse();
  const method = request.method, query = new URL(request.url).searchParams, company = query.get("companyId");
  if (method !== "GET" && !officeMutationIsSameOrigin(request)) return failure(403);
  if (!isCanonicalCompanyId(company) || !["GET", "POST", "DELETE"].includes(method)
    || [...query.keys()].some(key => key !== "companyId" || query.getAll(key).length !== 1)) return failure(400);
  const companyId = company.toLowerCase(), signal = AbortSignal.any([request.signal, AbortSignal.timeout(10000)]);
  try {
    const response = await bounded(fetchCoreApi(`/api/companies/${companyId}/telegram-link`, companyId, { signal, redirect: "error", method }), signal);
    if (!response) return unauthenticatedResponse();
    if (method === "DELETE" ? response.status !== 204 : response.status !== 200) {
      void response.body?.cancel().catch(() => {});
      return failure([400, 401, 403, 404, 503].includes(response.status) ? response.status : 502);
    }
    let view: unknown = null;
    if (method !== "DELETE") {
      const body = await readBoundedRequestJson({ body: response.body, headers: response.headers, signal }, 16 * 1024, parseSubmissionJson);
      if (!body.ok) return failure(502);
      view = method === "GET" ? parseTelegramLink(body.value) : parseTelegramLinkCode(body.value);
      if (!view) return failure(502);
    }
    const current = await bounded(officeSessionIsCurrent(companyId), signal);
    if (signal.aborted) return failure(503);
    if (!current) return unauthenticatedResponse();
    return method === "DELETE" ? new Response(null, { status: 204, headers }) : Response.json(view, { headers });
  } catch { return failure(503); }
}
