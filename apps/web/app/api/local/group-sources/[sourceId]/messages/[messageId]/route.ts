import { groupSourceBff } from "../../../../../../../lib/group-source-bff";
export async function GET(request: Request, context: { params: Promise<{ sourceId: string; messageId: string }> }) {
  return groupSourceBff(request, { kind: "message", ...await context.params });
}
