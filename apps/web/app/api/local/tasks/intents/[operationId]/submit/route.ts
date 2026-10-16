import { taskSubmissionBff } from "../../../../../../../lib/task-submission-bff";

export async function POST(request: Request, context: { params: Promise<{ operationId: string }> }) {
  return taskSubmissionBff(request, { kind: "submit", operationId: (await context.params).operationId });
}
