import { fetchCoreApi, isCanonicalCompanyId, isOfficeAiUiEnabled,
  localUiDisabledResponse, unauthenticatedResponse } from "../../../../../../lib/local-ai-bff";
import { taskHistoryResponse } from "../../../../../../lib/task-history-response";

export async function GET(request: Request, context: { params: Promise<{ taskId: string }> }) {
  if (!isOfficeAiUiEnabled()) return localUiDisabledResponse();
  const query = new URL(request.url).searchParams;
  const company = query.get("companyId"), { taskId } = await context.params;
  if ([...query.keys()].some(key => key !== "companyId" || query.getAll(key).length !== 1)
    || !isCanonicalCompanyId(company) || !isCanonicalCompanyId(taskId)) {
    return Response.json({ error: "Invalid task history selector." }, { status: 400, headers: { "Cache-Control": "no-store" } });
  }
  try {
    const companyId = company.toLowerCase(), selectedTask = taskId.toLowerCase();
    const response = await fetchCoreApi(`/api/tasks/${selectedTask}/history`, companyId);
    return response ? taskHistoryResponse(response, companyId, { taskId: selectedTask }) : unauthenticatedResponse();
  } catch {
    return Response.json({ error: "Task history is unavailable." }, { status: 502, headers: { "Cache-Control": "no-store" } });
  }
}
