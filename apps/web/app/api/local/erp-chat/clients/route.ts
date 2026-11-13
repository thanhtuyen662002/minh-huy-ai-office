import { copyCoreResponse, fetchCoreApi, isCanonicalCompanyId, isOfficeAiUiEnabled, localUiDisabledResponse, unauthenticatedResponse } from "../../../../../lib/local-ai-bff";
import { mutateLocalDataSource } from "../../../../../lib/local-data-source-mutation";
import { isErpChatClientCreate } from "../../../../../lib/erp-chat";

const headers = { "Cache-Control": "no-store" };

/** Web ERP clients of the chat API (administrators). */
export async function GET(request: Request) {
  if (!isOfficeAiUiEnabled()) return localUiDisabledResponse();
  const query = new URL(request.url).searchParams, companyId = query.get("companyId");
  if (!isCanonicalCompanyId(companyId) || [...query.keys()].some(key => key !== "companyId"))
    return Response.json({ code: "invalid_fields" }, { status: 400, headers });
  try {
    const response = await fetchCoreApi("/api/erp-chat/clients", companyId);
    return response ? copyCoreResponse(response) : unauthenticatedResponse();
  } catch { return Response.json({ code: "unavailable" }, { status: 502, headers }); }
}

export async function POST(request: Request) {
  return mutateLocalDataSource(request, "POST", "/api/erp-chat/clients", { validate: isErpChatClientCreate, maxBodyBytes: 2048 });
}
