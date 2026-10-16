import { taskSubmissionBff } from "../../../../../lib/task-submission-bff";

export const GET = (request: Request) => taskSubmissionBff(request, { kind: "list" });
export const POST = (request: Request) => taskSubmissionBff(request, { kind: "prepare" });
