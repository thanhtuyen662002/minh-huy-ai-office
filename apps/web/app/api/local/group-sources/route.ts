import { groupSourceBff } from "../../../../lib/group-source-bff";
export async function GET(request: Request) { return groupSourceBff(request, { kind: "sources" }); }
