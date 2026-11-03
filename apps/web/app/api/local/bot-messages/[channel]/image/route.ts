import { botMessageImageBff } from "../../../../../../lib/bot-messages-bff";
type Context = { params: Promise<{ channel: string }> };
export async function GET(request: Request, context: Context) { return botMessageImageBff(request, (await context.params).channel); }
