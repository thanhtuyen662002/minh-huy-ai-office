import { copyCoreResponse, fetchCoreApi, isCanonicalCompanyId, isOfficeAiUiEnabled, localUiDisabledResponse, unauthenticatedResponse } from "../../../../../lib/local-ai-bff";

export async function GET(request: Request) {
  if (!isOfficeAiUiEnabled()) return localUiDisabledResponse();
  const query = new URL(request.url).searchParams;
  const company = query.get("companyId");
  const offset = query.get("offset") ?? "0";
  const limit = query.get("limit") ?? "25";
  if ([...query.keys()].some(key => !["companyId", "offset", "limit", "includeAccessVersion"].includes(key) || query.getAll(key).length !== 1)
    || query.has("includeAccessVersion") && query.get("includeAccessVersion") !== "true"
    || !isCanonicalCompanyId(company) || !/^(0|[1-9][0-9]*)$/.test(offset) || Number(offset) > 1000
    || !/^[1-9][0-9]*$/.test(limit) || Number(limit) > 100) {
    return Response.json({ error: "Invalid member page." }, { status: 400, headers: { "Cache-Control": "no-store" } });
  }
  try {
    const response = await fetchCoreApi(`/api/company/members?offset=${offset}&limit=${limit}${query.has("includeAccessVersion") ? "&includeAccessVersion=true" : ""}`, company);
    return response ? copyCoreResponse(response) : unauthenticatedResponse();
  } catch {
    return Response.json({ error: "Company directory is unavailable." }, { status: 502, headers: { "Cache-Control": "no-store" } });
  }
}
