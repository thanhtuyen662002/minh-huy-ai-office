import { fetchCoreApi, isCanonicalCompanyId, isOfficeAiUiEnabled, localUiDisabledResponse, officeMutationIsSameOrigin, unauthenticatedResponse } from "../../../../../../../lib/local-ai-bff";
import { readBoundedRequestJson } from "../../../../../../../lib/bounded-request-json";
import { parseInvitation, parseInvitationJson } from "../../../../../../../lib/company-invitations";

const failure = (status: number) => Response.json({ error: "Không xác nhận được việc thu hồi lời mời." },
  { status, headers: { "Cache-Control": "no-store" } });

export async function POST(request: Request, context: { params: Promise<{ id: string }> }) {
  if (!isOfficeAiUiEnabled()) return localUiDisabledResponse();
  if (!officeMutationIsSameOrigin(request)) return failure(403);
  const { id } = await context.params;
  const query = new URL(request.url).searchParams, company = query.get("companyId");
  if (!isCanonicalCompanyId(id) || !isCanonicalCompanyId(company) || query.getAll("companyId").length !== 1
    || [...query.keys()].some(key => key !== "companyId") || request.body !== null) return failure(400);
  try {
    const deadline = AbortSignal.timeout(10_000);
    const response = await fetchCoreApi(`/api/company/invitations/${encodeURIComponent(id)}/revoke`, company,
      { method: "POST", signal: deadline });
    if (!response) return unauthenticatedResponse();
    if (!response.ok) { void response.body?.cancel().catch(() => {}); return failure([400, 401, 403, 404, 409].includes(response.status) ? response.status : 503); }
    const received = await readBoundedRequestJson(new Request("http://bounded-response.invalid", {
      method: "POST", body: response.body, signal: deadline, headers: { "Content-Type": response.headers.get("Content-Type") ?? "" }, duplex: "half",
    } as RequestInit), 4096, parseInvitationJson);
    const result = received.ok ? parseInvitation(received.value) : null;
    if (!result || result.companyId.toLowerCase() !== company.toLowerCase() || result.id.toLowerCase() !== id.toLowerCase()
      || result.revokedAtUtc === null) return failure(503);
    return Response.json(result, { headers: { "Cache-Control": "no-store" } });
  } catch { return failure(503); }
}
