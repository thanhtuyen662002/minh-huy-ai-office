import { copyCoreResponse, fetchCoreApi, isCanonicalCompanyId, isOfficeAiUiEnabled, localUiDisabledResponse, unauthenticatedResponse } from "../../../../../lib/local-ai-bff";

const headers = { "Cache-Control": "no-store" };

/** Prices per model, billed customers and the per-person rate (administrators). */
export async function GET(request: Request) {
  if (!isOfficeAiUiEnabled()) return localUiDisabledResponse();
  const query = new URL(request.url).searchParams, companyId = query.get("companyId");
  if (!isCanonicalCompanyId(companyId) || [...query.keys()].some(key => key !== "companyId"))
    return Response.json({ code: "invalid_fields" }, { status: 400, headers });
  try {
    const response = await fetchCoreApi("/api/ai-usage/settings", companyId);
    return response ? copyCoreResponse(response) : unauthenticatedResponse();
  } catch { return Response.json({ code: "unavailable" }, { status: 502, headers }); }
}
