import { fetchCoreApi, isCanonicalCompanyId, isOfficeAiUiEnabled, localUiDisabledResponse,
  officeMutationIsSameOrigin, officeSessionIsCurrent, unauthenticatedResponse } from "./local-ai-bff";
import { readBoundedRequestJson } from "./bounded-request-json";
import { parseSubmissionJson } from "./task-submission-intent";
import { botObject, parseBotAdmin, parseBotAi, parseBotAudit, parseBotChannel, parseBotCommand, parseBotQr, validBotMutation } from "./bot-channels";
const headers = { "Cache-Control": "no-store" };
const failure = (status: number) => Response.json({ error: "Không đọc hoặc cập nhật được cấu hình bot. Hãy tải lại sau." }, { status, headers });

export async function botChannelBff(request: Request, segments: string[] = []): Promise<Response> {
  if (!isOfficeAiUiEnabled()) return localUiDisabledResponse();
  const path = segments.join("/"), mutation = request.method !== "GET";
  const allowed = request.method === "GET" ? /^(|audit|(?:zalo|whatsapp)\/qr)$/ : request.method === "PUT" ? /^(ai|zalo|whatsapp)$/
    : request.method === "POST" ? /^(ai\/test|(?:zalo|whatsapp)\/commands)$/ : /(?!) /;
  if (!allowed.test(path)) return failure(404);
  if (mutation && !officeMutationIsSameOrigin(request)) return failure(403);
  const query = new URL(request.url).searchParams, company = query.get("companyId");
  if (!isCanonicalCompanyId(company) || [...query.keys()].some(k => !["companyId", ...(path === "audit" ? ["offset", "limit"] : [])].includes(k) || query.getAll(k).length !== 1)) return failure(400);
  const companyId = company.toLowerCase(), signal = AbortSignal.any([request.signal, AbortSignal.timeout(10000)]);
  try {
    let init: RequestInit = { signal, redirect: "error", method: request.method };
    if (mutation && path !== "ai/test") {
      const input = await readBoundedRequestJson({ body: request.body, headers: request.headers, signal }, 524288, parseSubmissionJson);
      if (!input.ok) return failure(input.status);
      if (!validBotMutation(input.value, path)) return failure(400);
      init = { ...init, headers: { "Content-Type": "application/json" }, body: JSON.stringify(input.value) };
    }
    const page = path === "audit" ? `?offset=${query.get("offset") ?? "0"}&limit=${query.get("limit") ?? "50"}` : "";
    if (path === "audit" && (![query.get("offset") ?? "0", query.get("limit") ?? "50"].every(x => /^(0|[1-9][0-9]{0,5})$/.test(x))
      || Number(query.get("offset") ?? 0) > 100000 || Number(query.get("limit") ?? 50) < 1 || Number(query.get("limit") ?? 50) > 100)) return failure(400);
    const response = await fetchCoreApi(`/api/companies/${companyId}/bot-channels${path ? `/${path}` : ""}${page}`, companyId, init);
    if (!response) return unauthenticatedResponse();
    if (response.status !== 200) { void response.body?.cancel().catch(() => {}); return failure([400, 401, 403, 404, 409, 413, 503].includes(response.status) ? response.status : 502); }
    const body = await readBoundedRequestJson({ body: response.body, headers: response.headers, signal }, 4 * 1024 * 1024, parseSubmissionJson);
    if (!body.ok) return failure(502);
    const raw = body.value;
    const result = path === "" ? parseBotAdmin(raw) : path === "ai" ? parseBotAi(raw) : path === "audit" ? parseBotAudit(raw)
      : path.endsWith("/qr") ? parseBotQr(raw) : path.endsWith("/commands") ? parseBotCommand(raw) : path === "ai/test"
        ? botObject(raw) && typeof raw.success === "boolean" && ["connected", "host_not_allowed", "upstream_rejected", "connection_failed"].includes(String(raw.code))
          ? { success: raw.success, code: raw.code } : null : parseBotChannel(raw);
    if (!result) return failure(502);
    if (!await officeSessionIsCurrent(companyId)) return unauthenticatedResponse();
    if (path.endsWith("/qr") && !parseBotQr(result)) return failure(404);
    return signal.aborted ? failure(503) : Response.json(result, { headers });
  } catch { return failure(503); }
}
