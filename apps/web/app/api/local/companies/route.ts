import { parseCompanyChoices } from "../../../../lib/company-choices";
import { fetchCoreApi, isCanonicalCompanyId, isOfficeAiUiEnabled, localUiDisabledResponse,
  unauthenticatedResponse } from "../../../../lib/local-ai-bff";

const unavailable = (status: number) => Response.json({ error: "Không xác minh được danh sách công ty." },
  { status, headers: { "Cache-Control": "no-store" } });

export async function GET(request: Request) {
  if (!isOfficeAiUiEnabled()) return localUiDisabledResponse();
  const selectors = new URL(request.url).searchParams.getAll("companyId");
  if (selectors.length !== 1 || !isCanonicalCompanyId(selectors[0])) return unavailable(400);
  try {
    const response = await fetchCoreApi("/api/auth/companies", selectors[0]);
    if (!response) return unauthenticatedResponse();
    if (!response.ok) return unavailable(response.status === 401 ? 401 : response.status === 403 ? 403 : 503);
    if (response.headers.get("content-type")?.split(";")[0].trim().toLowerCase() !== "application/json") return unavailable(503);
    const items = parseCompanyChoices(await response.json());
    if (!items) return unavailable(503);
    return Response.json({ items }, { headers: { "Cache-Control": "no-store" } });
  } catch { return unavailable(503); }
}
