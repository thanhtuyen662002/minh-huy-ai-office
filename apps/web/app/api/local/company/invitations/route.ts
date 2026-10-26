import { copyCoreResponse, fetchCoreApi, isCanonicalCompanyId, isOfficeAiUiEnabled, localUiDisabledResponse, officeMutationIsSameOrigin, unauthenticatedResponse } from "../../../../../lib/local-ai-bff";
import { readBoundedRequestJson } from "../../../../../lib/bounded-request-json";
import { isInvitationInput, parseCreatedInvitation, parseInvitationJson } from "../../../../../lib/company-invitations";

const failure = (status: number, code?: string) => Response.json({ error: "Không xác nhận được lời mời thành viên.", ...(code ? { code } : {}) },
  { status, headers: { "Cache-Control": "no-store" } });

export async function GET(request: Request) {
  if (!isOfficeAiUiEnabled()) return localUiDisabledResponse();
  const query = new URL(request.url).searchParams, company = query.get("companyId");
  const offset = query.get("offset") ?? "0", limit = query.get("limit") ?? "25";
  if (!isCanonicalCompanyId(company) || [...query.keys()].some(key => !["companyId", "offset", "limit"].includes(key)
    || query.getAll(key).length !== 1) || !/^(0|[1-9][0-9]*)$/.test(offset) || Number(offset) > 1000
    || !/^[1-9][0-9]*$/.test(limit) || Number(limit) > 100) return failure(400);
  try {
    const response = await fetchCoreApi(`/api/company/invitations?offset=${offset}&limit=${limit}`, company, { cache: "no-store" });
    return response ? copyCoreResponse(response) : unauthenticatedResponse();
  } catch { return failure(502); }
}

export async function POST(request: Request) {
  if (!isOfficeAiUiEnabled()) return localUiDisabledResponse();
  if (!officeMutationIsSameOrigin(request)) return failure(403);
  const query = new URL(request.url).searchParams, company = query.get("companyId");
  if (!isCanonicalCompanyId(company) || query.getAll("companyId").length !== 1
    || [...query.keys()].some(key => key !== "companyId")) return failure(400);
  const body = await readBoundedRequestJson(request, 2048, parseInvitationJson);
  if (!body.ok) return failure(body.status);
  if (!isInvitationInput(body.value)) return failure(400);
  try {
    const deadline = AbortSignal.timeout(20_000);
    const response = await fetchCoreApi("/api/company/invitations", company,
      { method: "POST", cache: "no-store", signal: deadline, headers: { "Content-Type": "application/json" }, body: JSON.stringify(body.value) });
    if (!response) return unauthenticatedResponse();
    if (!response.ok && response.status !== 409) { void response.body?.cancel().catch(() => {}); return failure([400, 401, 403].includes(response.status) ? response.status : 503); }
    const received = await readBoundedRequestJson(new Request("http://bounded-response.invalid", {
      method: "POST", body: response.body, signal: deadline, headers: { "Content-Type": response.headers.get("Content-Type") ?? "" }, duplex: "half",
    } as RequestInit), 4096, parseInvitationJson);
    if (!received.ok) return failure(503);
    if (response.status === 409) {
      const code = received.value && typeof received.value === "object" && Object.hasOwn(received.value, "code")
        ? (received.value as { code: unknown }).code : null;
      return failure(409, code === "duplicate-invitation" || code === "existing-member" ? code : undefined);
    }
    const result = parseCreatedInvitation(received.value);
    if (response.status !== 201 || !result || result.companyId.toLowerCase() !== company.toLowerCase()
      || result.acceptedAtUtc !== null || result.revokedAtUtc !== null) return failure(503);
    return Response.json(result, { status: 201, headers: { "Cache-Control": "no-store" } });
  } catch { return failure(503); }
}
