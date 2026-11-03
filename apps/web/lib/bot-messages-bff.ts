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

const imageTypes = ["image/jpeg", "image/png", "image/webp", "image/gif"];
const maxImageBytes = 5 * 1024 * 1024;
const imageFailure = (status: number) => new Response(null, { status, headers });
// Same check as the server: only photo formats whose first bytes match the declared type are passed to the browser.
export function sniffImageType(bytes: Uint8Array): string | null {
  const ascii = (from: number, to: number) => String.fromCharCode(...bytes.subarray(from, to));
  if (bytes.length < 12) return null;
  if (bytes[0] === 0xff && bytes[1] === 0xd8 && bytes[2] === 0xff) return "image/jpeg";
  if ([137, 80, 78, 71, 13, 10, 26, 10].every((b, i) => bytes[i] === b)) return "image/png";
  if (ascii(0, 6) === "GIF87a" || ascii(0, 6) === "GIF89a") return "image/gif";
  if (ascii(0, 4) === "RIFF" && ascii(8, 12) === "WEBP") return "image/webp";
  return null;
}
async function readBounded(body: ReadableStream<Uint8Array> | null, max: number, signal: AbortSignal): Promise<Uint8Array | null> {
  if (!body) return null;
  const reader = body.getReader(), parts: Uint8Array[] = []; let size = 0;
  try {
    for (;;) {
      if (signal.aborted) return null;
      const { done, value } = await reader.read(); if (done) break;
      size += value.byteLength; if (size > max) return null; parts.push(value);
    }
  } finally { void reader.cancel().catch(() => {}); }
  const all = new Uint8Array(size); let at = 0; for (const p of parts) { all.set(p, at); at += p.byteLength; }
  return all;
}

// One picture of a stored message. Pictures never change, so the browser may keep its private copy for a day.
export async function botMessageImageBff(request: Request, channel: string): Promise<Response> {
  if (!isOfficeAiUiEnabled()) return localUiDisabledResponse();
  if (request.method !== "GET" || !botChannel(channel)) return imageFailure(404);
  const query = new URL(request.url).searchParams, company = query.get("companyId");
  if (!isCanonicalCompanyId(company) || [...query.keys()].some(k => !["companyId", "group", "message", "position"].includes(k) || query.getAll(k).length !== 1)) return imageFailure(400);
  const group = query.get("group"), message = query.get("message"), position = query.get("position");
  if (!group || group.length > 200 || !message || message.length > 200 || !position || !/^[0-9]$/.test(position)) return imageFailure(400);
  const companyId = company.toLowerCase(), signal = AbortSignal.any([request.signal, AbortSignal.timeout(15000)]);
  try {
    const response = await fetchCoreApi(`/api/companies/${companyId}/bot-messages/${channel}/image?${new URLSearchParams({ group, message, position })}`,
      companyId, { signal, redirect: "error", method: "GET" }, 15000);
    if (!response) return unauthenticatedResponse();
    if (response.status !== 200) { void response.body?.cancel().catch(() => {}); return imageFailure([400, 401, 403, 404, 503].includes(response.status) ? response.status : 502); }
    const declared = (response.headers.get("content-type") ?? "").split(";")[0].trim().toLowerCase();
    const bytes = await readBounded(response.body, maxImageBytes, signal);
    if (!bytes || !imageTypes.includes(declared) || sniffImageType(bytes) !== declared) return imageFailure(502);
    if (!await officeSessionIsCurrent(companyId)) return unauthenticatedResponse();
    return new Response(bytes as BodyInit, { headers: { "Content-Type": declared, "Cache-Control": "private, max-age=86400",
      "X-Content-Type-Options": "nosniff", "Content-Security-Policy": "default-src 'none'; sandbox", "Content-Length": String(bytes.byteLength) } });
  } catch { return imageFailure(503); }
}
