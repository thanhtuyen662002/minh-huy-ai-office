import { parseTaskHistoryDetail, parseTaskHistoryPage } from "./task-history";

// The shared Core helper has already enforced its byte/deadline/final SID
// fence. Archive replies additionally require exact typed JSON and UTF8;
// never repair private stored text or relay diagnostics in an error body.
export async function taskHistoryResponse(response: Response, companyId: string,
  selector: { taskId: string } | { offset: number; limit: number }): Promise<Response> {
  const headers = { "Cache-Control": "no-store" };
  if (!response.ok) {
    const status = [400, 401, 403, 404, 502, 503].includes(response.status) ? response.status : 502;
    return Response.json({ error: "Task history could not be read." }, { status, headers });
  }
  try {
    if (response.status !== 200) throw new Error("Unexpected status");
    const text = new TextDecoder("utf-8", { fatal: true }).decode(await response.arrayBuffer());
    const payload: unknown = JSON.parse(text);
    if ("taskId" in selector) {
      const detail = parseTaskHistoryDetail(payload);
      if (!detail || detail.companyId !== companyId || detail.task.taskId !== selector.taskId) throw new Error("Invalid detail");
      return Response.json(detail, { headers });
    }
    const page = parseTaskHistoryPage(payload);
    if (!page || page.companyId !== companyId || page.offset !== selector.offset || page.limit !== selector.limit) throw new Error("Invalid page");
    return Response.json(page, { headers });
  } catch {
    return Response.json({ error: "Task history is unavailable." }, { status: 502, headers });
  }
}
