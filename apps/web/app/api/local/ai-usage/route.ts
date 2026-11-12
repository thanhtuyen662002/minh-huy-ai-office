import { copyCoreResponse, fetchCoreApi, isCanonicalCompanyId, isOfficeAiUiEnabled, localUiDisabledResponse, unauthenticatedResponse } from "../../../../lib/local-ai-bff";

const headers = { "Cache-Control": "no-store" };

/** Monthly AI usage report (administrators). ?month=YYYY-MM, default this month in Vietnam time. */
export async function GET(request: Request) {
  if (!isOfficeAiUiEnabled()) return localUiDisabledResponse();
  const query = new URL(request.url).searchParams, companyId = query.get("companyId"), month = query.get("month");
  if (!isCanonicalCompanyId(companyId) || [...query.keys()].some(key => key !== "companyId" && key !== "month")
    || (month !== null && !/^20[2-9][0-9]-(0[1-9]|1[0-2])$/.test(month)))
    return Response.json({ code: "invalid_fields" }, { status: 400, headers });
  try {
    const response = await fetchCoreApi(`/api/ai-usage${month ? `?month=${month}` : ""}`, companyId);
    return response ? copyCoreResponse(response) : unauthenticatedResponse();
  } catch { return Response.json({ code: "unavailable" }, { status: 502, headers }); }
}
