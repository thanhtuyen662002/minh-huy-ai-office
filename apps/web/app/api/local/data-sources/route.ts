import {
  copyCoreResponse,
  fetchCoreApi,
  isCanonicalCompanyId,
  isLocalAiUiEnabled,
  localUiDisabledResponse,
  unauthenticatedResponse,
} from "../../../../lib/local-ai-bff";
import { mutateLocalDataSource } from "../../../../lib/local-data-source-mutation";

export async function POST(request: Request) {
  return mutateLocalDataSource(request, "POST", "/api/data-sources/");
}

export async function GET(request: Request) {
  if (!isLocalAiUiEnabled()) return localUiDisabledResponse();
  const companyId = new URL(request.url).searchParams.get("companyId");
  if (!isCanonicalCompanyId(companyId)) {
    return Response.json({ error: "Invalid company selector." }, { status: 400 });
  }

  const response = await fetchCoreApi("/api/data-sources/", companyId);
  if (!response) return unauthenticatedResponse();
  return copyCoreResponse(response);
}
