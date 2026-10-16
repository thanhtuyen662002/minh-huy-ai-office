import { groupSourceBff } from "../../../../../../lib/group-source-bff";
export async function GET(request: Request, context: { params: Promise<{ sourceId: string }> }) {
  return groupSourceBff(request, { kind: "messages", sourceId: (await context.params).sourceId });
}
