import { fetchCoreApi, isCanonicalCompanyId, isOfficeAiUiEnabled, localUiDisabledResponse, officeSessionIsCurrent, unauthenticatedResponse } from "./local-ai-bff";
import { readBoundedRequestJson } from "./bounded-request-json";
import { parseSubmissionJson } from "./task-submission-intent";
import { botChannel } from "./bot-channels";
import { parseBotMessageGroups, parseBotMessagePage } from "./bot-messages";
const headers = { "Cache-Control": "no-store" };
const failure = (status: number) => Response.json({ error: "Chưa đọc được tin nhắn. Hãy thử lại sau." }, { status, headers });

// Read-only: the groups selected on Kết nối, or one page of a group's messages, newest first.
export async function botMessagesBff(request: Request, channel?: string): Promise<Response> {
  if (!isOfficeAiUiEnabled()) return localUiDisabledResponse();
  if (request.method !== "GET" || channel !== undefined && !botChannel(channel)) return failure(404);
  const query = new URL(request.url).searchParams, company = query.get("companyId");
  const allowed = channel ? ["companyId", "group", "before", "limit"] : ["companyId"];
  if (!isCanonicalCompanyId(company) || [...query.keys()].some(k => !allowed.includes(k) || query.getAll(k).length !== 1)) return failure(400);
  const group = query.get("group"), before = query.get("before"), limit = query.get("limit") ?? "50";
  if (channel && (!group || group.length > 200 || !/^[1-9][0-9]{0,2}$/.test(limit) || Number(limit) > 200
    || before !== null && (before.length > 40 || !Number.isFinite(Date.parse(before))))) return failure(400);
  const companyId = company.toLowerCase(), signal = AbortSignal.any([request.signal, AbortSignal.timeout(10000)]);
  try {
    const path = channel ? `/${channel}?${new URLSearchParams({ group: group!, limit, ...(before ? { before: new Date(before).toISOString() } : {}) })}` : "";
    const response = await fetchCoreApi(`/api/companies/${companyId}/bot-messages${path}`, companyId, { signal, redirect: "error", method: "GET" });
    if (!response) return unauthenticatedResponse();
    if (response.status !== 200) { void response.body?.cancel().catch(() => {}); return failure([400, 401, 403, 404, 503].includes(response.status) ? response.status : 502); }
    const raw = await readBoundedRequestJson({ body: response.body, headers: response.headers, signal }, 4 * 1024 * 1024, parseSubmissionJson);
    if (!raw.ok) return failure(502);
    const result = channel ? parseBotMessagePage(raw.value, Number(limit)) : parseBotMessageGroups(raw.value);
    if (!result) return failure(502);
    if (!await officeSessionIsCurrent(companyId)) return unauthenticatedResponse();
    return signal.aborted ? failure(503) : Response.json(result, { headers });
  } catch { return failure(503); }
}
