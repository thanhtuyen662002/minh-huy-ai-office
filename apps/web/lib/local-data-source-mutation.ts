import { copyCoreResponse, fetchCoreApi, isCanonicalCompanyId, isLocalAiUiEnabled, localUiDisabledResponse, unauthenticatedResponse } from "./local-ai-bff";

function failure(message: string, status: number) {
  return Response.json({ error: message }, { status, headers: { "Cache-Control": "no-store" } });
}

export async function mutateLocalDataSource(request: Request, method: "POST" | "PUT", path: string) {
  if (!isLocalAiUiEnabled()) return localUiDisabledResponse();
  const url = new URL(request.url);
  if (request.headers.get("origin") !== url.origin) return failure("Same-origin request is required.", 403);
  const companyId = url.searchParams.get("companyId");
  if (!isCanonicalCompanyId(companyId)) return failure("Invalid company selection.", 400);
  let payload: unknown;
  try { payload = await request.json(); } catch { return failure("Invalid source metadata.", 400); }
  if (!payload || typeof payload !== "object" || Array.isArray(payload)) return failure("Invalid source metadata.", 400);
  try {
    const response = await fetchCoreApi(path, companyId, { method, headers: { "Content-Type": "application/json" }, body: JSON.stringify(payload) });
    return response ? copyCoreResponse(response) : unauthenticatedResponse();
  } catch {
    return failure("Core API is unavailable.", 502);
  }
}
