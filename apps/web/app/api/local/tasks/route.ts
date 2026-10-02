import {
  copyCoreResponse,
  fetchCoreApi,
  isCanonicalCompanyId,
  isLocalAiUiEnabled,
  localUiDisabledResponse,
  unauthenticatedResponse,
} from "../../../../lib/local-ai-bff";

type TaskBody = { dataSourceId?: unknown; question?: unknown };

export async function POST(request: Request) {
  if (!isLocalAiUiEnabled()) return localUiDisabledResponse();

  const companyId = new URL(request.url).searchParams.get("companyId");
  if (!isCanonicalCompanyId(companyId)) {
    return Response.json({ error: "Invalid company selector." }, { status: 400 });
  }

  let body: TaskBody;
  try {
    body = await request.json() as TaskBody;
  } catch {
    return Response.json({ error: "Invalid task payload." }, { status: 400 });
  }

  if (
    typeof body.dataSourceId !== "string"
    || !/^[0-9a-fA-F-]{36}$/.test(body.dataSourceId)
    || typeof body.question !== "string"
    || body.question.length === 0
    || body.question.length > 4000
    || body.question.trim() !== body.question
    || /[\0]/.test(body.question)
  ) {
    return Response.json({ error: "Invalid task payload." }, { status: 400 });
  }

  const response = await fetchCoreApi("/api/tasks", companyId, {
    method: "POST",
    headers: {
      "Content-Type": "application/json; charset=utf-8",
      "Idempotency-Key": `web-${crypto.randomUUID().replaceAll("-", "")}`,
    },
    body: JSON.stringify({
      dataSourceId: body.dataSourceId,
      question: body.question,
    }),
  });
  if (!response) return unauthenticatedResponse();
  return copyCoreResponse(response);
}
