import { taskSubmissionBff } from "../../../../../../lib/task-submission-bff";

export async function GET(request: Request, context: { params: Promise<{ operationId: string }> }) {
  return taskSubmissionBff(request, { kind: "detail", operationId: (await context.params).operationId });
}
