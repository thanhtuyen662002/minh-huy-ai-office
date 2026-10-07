import {
  copyCoreResponse,
  fetchCoreApi,
  isCanonicalCompanyId,
  isOfficeAiUiEnabled,
  localUiDisabledResponse,
  unauthenticatedResponse,
} from "../../../../../lib/local-ai-bff";

type RouteContext = { params: Promise<{ taskId: string }> };

export async function GET(request: Request, context: RouteContext) {
  if (!isOfficeAiUiEnabled()) return localUiDisabledResponse();

  const companyId = new URL(request.url).searchParams.get("companyId");
  if (!isCanonicalCompanyId(companyId)) {
    return Response.json({ error: "Invalid company selector." }, { status: 400 });
  }

  const { taskId } = await context.params;
  if (!/^[0-9a-fA-F-]{36}$/.test(taskId)) {
    return Response.json({ error: "Invalid task id." }, { status: 400 });
  }

  const response = await fetchCoreApi(
    `/api/tasks/${encodeURIComponent(taskId)}`,
    companyId,
  );
  if (!response) return unauthenticatedResponse();
  return copyCoreResponse(response);
}
