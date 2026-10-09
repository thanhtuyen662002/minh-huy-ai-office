import {
  copyCoreResponse,
  fetchCoreApi,
  isCanonicalCompanyId,
  isOfficeAiUiEnabled,
  officeMutationIsSameOrigin,
  localUiDisabledResponse,
  unauthenticatedResponse,
} from "../../../../lib/local-ai-bff";
import { readBoundedRequestJson } from "../../../../lib/bounded-request-json";
import { taskHistoryResponse } from "../../../../lib/task-history-response";

type TaskBody = { dataSourceId?: unknown; question?: unknown };

export async function GET(request: Request) {
  if (!isOfficeAiUiEnabled()) return localUiDisabledResponse();
  const query = new URL(request.url).searchParams;
  const company = query.get("companyId"), offset = query.get("offset") ?? "0", limit = query.get("limit") ?? "25";
  if ([...query.keys()].some(key => !["companyId", "offset", "limit"].includes(key) || query.getAll(key).length !== 1)
    || !isCanonicalCompanyId(company) || !/^(0|[1-9][0-9]*)$/.test(offset) || Number(offset) > 10000
    || !/^[1-9][0-9]*$/.test(limit) || Number(limit) > 100) {
    return Response.json({ error: "Invalid task history page." }, { status: 400, headers: { "Cache-Control": "no-store" } });
  }
  try {
    const response = await fetchCoreApi(`/api/tasks?offset=${offset}&limit=${limit}`, company);
    return response ? taskHistoryResponse(response, company, { offset: Number(offset), limit: Number(limit) }) : unauthenticatedResponse();
  } catch {
    return Response.json({ error: "Task history is unavailable." }, { status: 502, headers: { "Cache-Control": "no-store" } });
  }
}

export async function POST(request: Request) {
  if (!isOfficeAiUiEnabled()) return localUiDisabledResponse();
  if (!officeMutationIsSameOrigin(request)) return Response.json({ error: "Same-origin request is required." }, {
    status: 403, headers: { "Cache-Control": "no-store" },
  });

  const companyId = new URL(request.url).searchParams.get("companyId");
  if (!isCanonicalCompanyId(companyId)) {
    return Response.json({ error: "Invalid company selector." }, { status: 400 });
  }

  // 32 KiB preserves the supported 4000 UTF-16 units, even fully JSON-escaped.
  const input = await readBoundedRequestJson(request, 32 * 1024);
  if (!input.ok) return Response.json({ error: "Invalid task payload." }, {
    status: input.status, headers: { "Cache-Control": "no-store" },
  });
  const body = input.value as TaskBody;

  if (
    !body || typeof body !== "object" || Array.isArray(body)
    || typeof body.dataSourceId !== "string"
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
