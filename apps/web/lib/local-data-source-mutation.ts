import { copyCoreResponse, fetchCoreApi, isCanonicalCompanyId, isOfficeAiUiEnabled, officeMutationIsSameOrigin, localUiDisabledResponse, unauthenticatedResponse } from "./local-ai-bff";
import { readBoundedRequestJson } from "./bounded-request-json";

function failure(message: string, status: number) {
  return Response.json({ error: message }, { status, headers: { "Cache-Control": "no-store" } });
}

export async function mutateLocalDataSource(request: Request, method: "POST" | "PUT", path: string,
  strict?: { validate: (value: unknown) => boolean; maxBodyBytes: number }) {
  if (!isOfficeAiUiEnabled()) return localUiDisabledResponse();
  const url = new URL(request.url);
  if (!officeMutationIsSameOrigin(request)) return failure("Same-origin request is required.", 403);
  const companyId = url.searchParams.get("companyId");
  if (!isCanonicalCompanyId(companyId)) return failure("Invalid company selection.", 400);
  const input = await readBoundedRequestJson(request, strict?.maxBodyBytes ?? 8192);
  if (!input.ok) return failure("Invalid source metadata.", input.status);
  const payload = input.value;
  if (strict && !strict.validate(payload)) return failure("Invalid source registration.", 400);
  if (!payload || typeof payload !== "object" || Array.isArray(payload)) return failure("Invalid source metadata.", 400);
  try {
    const response = await fetchCoreApi(path, companyId, { method, headers: { "Content-Type": "application/json" }, body: JSON.stringify(payload) });
    return response ? copyCoreResponse(response) : unauthenticatedResponse();
  } catch {
    return failure("Core API is unavailable.", 502);
  }
}
