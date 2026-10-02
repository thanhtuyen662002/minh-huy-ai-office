import {
  copyCoreResponse,
  fetchCoreApi,
  isCanonicalCompanyId,
  isLocalAiUiEnabled,
  localUiDisabledResponse,
  unauthenticatedResponse,
} from "../../../../../../lib/local-ai-bff";

type RouteContext = { params: Promise<{ dataSourceId: string }> };

export async function POST(request: Request, context: RouteContext) {
  if (!isLocalAiUiEnabled()) return localUiDisabledResponse();

  const companyId = new URL(request.url).searchParams.get("companyId");
  if (!isCanonicalCompanyId(companyId)) {
    return Response.json({ error: "Invalid company selector." }, { status: 400 });
  }

  const { dataSourceId } = await context.params;
  if (!/^[0-9a-fA-F-]{36}$/.test(dataSourceId)) {
    return Response.json({ error: "Invalid data source id." }, { status: 400 });
  }

  const response = await fetchCoreApi(
    `/api/data-sources/${encodeURIComponent(dataSourceId)}/connection-test`,
    companyId,
    { method: "POST" },
  );
  if (!response) return unauthenticatedResponse();
  return copyCoreResponse(response);
}
