import { copyCoreResponse, fetchCoreApi, isCanonicalCompanyId, isOfficeAiUiEnabled, localUiDisabledResponse, unauthenticatedResponse } from "../../../../../lib/local-ai-bff";

export async function GET(request: Request) {
  if (!isOfficeAiUiEnabled()) return localUiDisabledResponse();
  const url = new URL(request.url);
  const company = url.searchParams.get("companyId");
  const offset = url.searchParams.get("offset") ?? "0";
  const limit = url.searchParams.get("limit") ?? "50";
  if (!isCanonicalCompanyId(company) || !/^(0|[1-9][0-9]*)$/.test(offset) || Number(offset) > 1000
    || !/^[1-9][0-9]*$/.test(limit) || Number(limit) > 100) {
    return Response.json({ error: "Invalid registration options page." }, { status: 400, headers: { "Cache-Control": "no-store" } });
  }
  try {
    const response = await fetchCoreApi(`/api/data-sources/registration-options?offset=${offset}&limit=${limit}`, company);
    return response ? copyCoreResponse(response) : unauthenticatedResponse();
  } catch {
    return Response.json({ error: "Core API is unavailable." }, { status: 502, headers: { "Cache-Control": "no-store" } });
  }
}
