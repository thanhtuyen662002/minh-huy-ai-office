import { copyCoreResponse, fetchCoreApi, isCanonicalCompanyId, isOfficeAiUiEnabled, localUiDisabledResponse, officeMutationIsSameOrigin, unauthenticatedResponse } from "../../../../../../../lib/local-ai-bff";
import { readBoundedRequestJson } from "../../../../../../../lib/bounded-request-json";
import { isMemberAccessInput } from "../../../../../../../lib/company-members";

const failure = (status: number, error: string) => Response.json({ error }, { status, headers: { "Cache-Control": "no-store" } });

export async function POST(request: Request, context: { params: Promise<{ userId: string }> }) {
  if (!isOfficeAiUiEnabled()) return localUiDisabledResponse();
  if (!officeMutationIsSameOrigin(request)) return failure(403, "Same-origin request is required.");
  const { userId } = await context.params;
  const query = new URL(request.url).searchParams;
  const company = query.get("companyId");
  if (!isCanonicalCompanyId(userId) || !isCanonicalCompanyId(company) || query.getAll("companyId").length !== 1 ||
    [...query.keys()].some(key => key !== "companyId")) return failure(400, "Invalid membership selection.");
  const body = await readBoundedRequestJson(request, 2048);
  if (!body.ok) return failure(body.status, "Invalid membership access request.");
  if (!isMemberAccessInput(body.value)) return failure(400, "Invalid membership access request.");
  try {
    const response = await fetchCoreApi(`/api/company/members/${encodeURIComponent(userId)}/access`, company,
      { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body.value) });
    return response ? copyCoreResponse(response) : unauthenticatedResponse();
  } catch { return failure(502, "Company administration is unavailable."); }
}
