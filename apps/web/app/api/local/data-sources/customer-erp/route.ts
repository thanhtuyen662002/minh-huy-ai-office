import { copyCoreResponse, fetchCoreApi, isCanonicalCompanyId, isOfficeAiUiEnabled, localUiDisabledResponse, unauthenticatedResponse } from "../../../../../lib/local-ai-bff";
import { mutateLocalDataSource } from "../../../../../lib/local-data-source-mutation";
import { isErpConnection } from "../../../../../lib/customer-erp";

export async function GET(request: Request) {
  if (!isOfficeAiUiEnabled()) return localUiDisabledResponse();
  const companyId = new URL(request.url).searchParams.get("companyId");
  if (!isCanonicalCompanyId(companyId)) return Response.json({ code: "invalid_fields" }, { status: 400, headers: { "Cache-Control": "no-store" } });
  try {
    const response = await fetchCoreApi("/api/data-sources/customer-erp", companyId);
    return response ? copyCoreResponse(response) : unauthenticatedResponse();
  } catch { return Response.json({ code: "unavailable" }, { status: 502, headers: { "Cache-Control": "no-store" } }); }
}
export async function POST(request: Request) {
  return mutateLocalDataSource(request, "POST", "/api/data-sources/customer-erp",
    { validate: value => isErpConnection(value, true), maxBodyBytes: 16384, timeoutMs: 20000 });
}
