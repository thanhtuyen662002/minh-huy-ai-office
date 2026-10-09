import { fetchCoreApi, isCanonicalCompanyId, isOfficeAiUiEnabled, localUiDisabledResponse, officeMutationIsSameOrigin, officeSessionIsCurrent, unauthenticatedResponse } from "./local-ai-bff";
import { readBoundedRequestJson } from "./bounded-request-json";
import { parseSubmissionInput, parseSubmissionJson, parseSubmissionReceipt, receiptMatches, submissionFingerprint,
  submissionFingerprintIsCanonical, verifiedSubmissionIntent, verifiedSubmissionPage, SubmissionInput, SubmissionIntent } from "./task-submission-intent";

const headers = { "Cache-Control": "no-store" };
const conflicts = new Set(["operation-conflict", "intent-unavailable", "intent-expired", "intent-limit"]);
const failure = (status: number, code?: string) => Response.json({ error: "Không xác nhận được yêu cầu. Kiểm tra lại cùng thao tác.", ...(code ? { code } : {}) }, { status, headers });
type Route = { kind: "prepare" | "list" } | { kind: "detail" | "submit"; operationId: string };

async function payload(response: Response, signal: AbortSignal, maximum = 65536) {
  return readBoundedRequestJson(new Request("http://bounded-response.invalid", { method: "POST", body: response.body,
    signal, headers: { "Content-Type": response.headers.get("Content-Type") ?? "" }, duplex: "half" } as RequestInit), maximum, parseSubmissionJson);
}
async function rejected(response: Response, signal: AbortSignal): Promise<Response | null> {
  if (response.ok) return null;
  if (response.status === 409) {
    const body = await payload(response, signal, 4096);
    const value = body.ok && body.value && typeof body.value === "object" && Object.hasOwn(body.value, "code")
      ? (body.value as { code: unknown }).code : null;
    return failure(409, typeof value === "string" && conflicts.has(value) ? value : undefined);
  }
  void response.body?.cancel().catch(() => {});
  return failure([400, 401, 403, 404, 413, 502, 503].includes(response.status) ? response.status : 503);
}

/** Strict private routes share the issued-session helper, including its final body/SID fence. */
export async function taskSubmissionBff(request: Request, route: Route): Promise<Response> {
  if (!isOfficeAiUiEnabled()) return localUiDisabledResponse();
  const mutation = route.kind === "prepare" || route.kind === "submit";
  if (mutation && !officeMutationIsSameOrigin(request)) return failure(403);
  const query = new URL(request.url).searchParams, company = query.get("companyId");
  const allowed = route.kind === "list" ? ["companyId", "offset", "limit"] : ["companyId"];
  if (!isCanonicalCompanyId(company) || query.getAll("companyId").length !== 1
    || [...query.keys()].some(key => !allowed.includes(key) || query.getAll(key).length !== 1)) return failure(400);
  const companyId = company.toLowerCase();
  if ("operationId" in route && !isCanonicalCompanyId(route.operationId)) return failure(400);
  const operationId = "operationId" in route ? route.operationId.toLowerCase() : null;
  const offsetText = query.get("offset") ?? "0", limitText = query.get("limit") ?? "25";
  if (route.kind === "list" && (!/^(0|[1-9][0-9]*)$/.test(offsetText) || Number(offsetText) > 10000
    || !/^[1-9][0-9]*$/.test(limitText) || Number(limitText) > 25)) return failure(400);

  let input: SubmissionInput | null = null;
  let fingerprint: string | null = null;
  if (mutation) {
    const body = await readBoundedRequestJson(request, 32768, parseSubmissionJson);
    if (!body.ok) return failure(body.status);
    if (route.kind === "prepare") {
      input = parseSubmissionInput(body.value);
      if (!input) return failure(400);
    } else {
      const value = body.value;
      if (!value || typeof value !== "object" || Array.isArray(value) || Object.keys(value).length !== 1
        || !Object.hasOwn(value, "inputFingerprint") || !submissionFingerprintIsCanonical((value as { inputFingerprint: unknown }).inputFingerprint)) return failure(400);
      fingerprint = (value as { inputFingerprint: string }).inputFingerprint;
    }
  }
  const signal = AbortSignal.any([request.signal, AbortSignal.timeout(10000)]);
  const release = async (value: unknown, status = 200) => await officeSessionIsCurrent(companyId)
    ? Response.json(value, { status, headers }) : unauthenticatedResponse();
  try {
    if (input) fingerprint = await submissionFingerprint(input);
    let prepared: SubmissionIntent | null = null;
    if (route.kind === "submit") {
      // Bind the receipt to the authoritative stored input. This owner GET has
      // no effects; POST below is still a deliberate fresh execution admission.
      const existing = await fetchCoreApi(`/api/tasks/intents/${operationId}`, companyId, { signal });
      if (!existing) return unauthenticatedResponse();
      const denial = await rejected(existing, signal); if (denial) return denial;
      if (existing.status !== 200) return failure(503);
      const body = await payload(existing, signal);
      prepared = body.ok ? await verifiedSubmissionIntent(body.value) : null;
      if (!prepared || prepared.companyId.toLowerCase() !== companyId || prepared.operationId.toLowerCase() !== operationId) return failure(503);
      if (prepared.state === 3) return failure(409, "intent-unavailable");
      if (prepared.inputFingerprint !== fingerprint) return failure(409, "operation-conflict");
      input = { operationId: prepared.operationId, dataSourceId: prepared.dataSourceId!, question: prepared.question! };
    }
    const path = route.kind === "list" ? `/api/tasks/intents?offset=${offsetText}&limit=${limitText}`
      : route.kind === "prepare" ? "/api/tasks/intents" : `/api/tasks/intents/${operationId}${route.kind === "submit" ? "/submit" : ""}`;
    const response = await fetchCoreApi(path, companyId, mutation ? { method: "POST", signal,
      headers: { "Content-Type": "application/json; charset=utf-8" },
      body: JSON.stringify(route.kind === "prepare" ? input : { inputFingerprint: fingerprint }) } : { signal });
    if (!response) return unauthenticatedResponse();
    const denial = await rejected(response, signal); if (denial) return denial;
    if (response.status !== (route.kind === "submit" ? 202 : 200)) return failure(503);
    const body = await payload(response, signal, route.kind === "list" ? 1048576 : route.kind === "submit" ? 4096 : 65536);
    if (!body.ok) return failure(503);
    if (route.kind === "submit") {
      const receipt = parseSubmissionReceipt(body.value);
      if (!receipt || !input || !fingerprint || !receiptMatches(receipt, companyId, input, fingerprint, prepared?.accepted)) return failure(503);
      return release(receipt, 202);
    }
    if (route.kind === "list") {
      const page = await verifiedSubmissionPage(body.value);
      if (!page || page.companyId.toLowerCase() !== companyId || page.offset !== Number(offsetText) || page.limit !== Number(limitText)) return failure(503);
      return release(page);
    }
    const intent = await verifiedSubmissionIntent(body.value);
    if (!intent || intent.companyId.toLowerCase() !== companyId || intent.operationId.toLowerCase() !== (input?.operationId ?? operationId)) return failure(503);
    if (input && (intent.state === 3 || intent.dataSourceId?.toLowerCase() !== input.dataSourceId || intent.question !== input.question
      || intent.inputFingerprint !== fingerprint)) return failure(503);
    return release(intent);
  } catch { return failure(503); }
}
