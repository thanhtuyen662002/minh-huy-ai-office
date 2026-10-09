import { fetchCoreApi, isCanonicalCompanyId, isOfficeAiUiEnabled, localUiDisabledResponse, officeMutationIsSameOrigin, unauthenticatedResponse } from "../../../../../../../lib/local-ai-bff";
import { readBoundedRequestJson } from "../../../../../../../lib/bounded-request-json";
import { isAdministratorInput, parseAdministratorJson, parseAdministratorResult } from "../../../../../../../lib/company-administrators";

const failure = (status: number, code?: string) => Response.json({ error: "Không xác nhận được thay đổi quyền quản trị.", ...(code ? { code } : {}) },
  { status, headers: { "Cache-Control": "no-store" } });
const conflicts = new Set(["self-role-change", "last-administrator", "inactive-user", "inactive-membership", "invalid-role-state", "role-limit", "version-limit", "stale-version", "operation-conflict"]);

export async function POST(request: Request, context: { params: Promise<{ userId: string }> }) {
  if (!isOfficeAiUiEnabled()) return localUiDisabledResponse();
  if (!officeMutationIsSameOrigin(request)) return failure(403);
  const { userId } = await context.params;
  const query = new URL(request.url).searchParams, company = query.get("companyId");
  if (!isCanonicalCompanyId(userId) || !isCanonicalCompanyId(company) || query.getAll("companyId").length !== 1 ||
    [...query.keys()].some(key => key !== "companyId")) return failure(400);
  const body = await readBoundedRequestJson(request, 2048, parseAdministratorJson);
  if (!body.ok) return failure(body.status);
  if (!isAdministratorInput(body.value)) return failure(400);
  const input = body.value;
  try {
    const deadline = AbortSignal.timeout(10_000);
    const response = await fetchCoreApi(`/api/company/members/${encodeURIComponent(userId)}/administrator`, company,
      { method: "POST", signal: deadline, headers: { "Content-Type": "application/json" }, body: JSON.stringify(input) });
    if (!response) return unauthenticatedResponse();
    if (!response.ok && response.status !== 409) {
      void response.body?.cancel().catch(() => {});
      return failure([400, 401, 403, 404].includes(response.status) ? response.status : 503);
    }
    // Reuse strict byte/time limits for the response, after the issued-session
    // helper has completed its final shared binding fence. No cookies or private
    // upstream diagnostics are copied into the browser response.
    const received = await readBoundedRequestJson(new Request("http://bounded-response.invalid", {
      method: "POST", body: response.body, signal: deadline, headers: { "Content-Type": response.headers.get("Content-Type") ?? "" }, duplex: "half",
    } as RequestInit), 4096, parseAdministratorJson);
    if (!received.ok) return failure(503);
    if (response.status === 409) {
      const code = received.value && typeof received.value === "object" && Object.hasOwn(received.value, "code")
        ? (received.value as { code: unknown }).code : null;
      return failure(409, typeof code === "string" && conflicts.has(code) ? code : undefined);
    }
    const result = parseAdministratorResult(received.value);
    if (!result || result.companyId.toLowerCase() !== company.toLowerCase() || result.userId.toLowerCase() !== userId.toLowerCase() ||
      result.operationId.toLowerCase() !== input.operationId.toLowerCase() || result.isAdministrator !== input.isAdministrator) return failure(503);
    return Response.json(result, { headers: { "Cache-Control": "no-store" } });
  } catch { return failure(503); }
}
