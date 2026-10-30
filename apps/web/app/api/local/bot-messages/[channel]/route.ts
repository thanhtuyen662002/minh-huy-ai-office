import { botMessagesBff } from "../../../../../lib/bot-messages-bff";
type Context = { params: Promise<{ channel: string }> };
export async function GET(request: Request, context: Context) { return botMessagesBff(request, (await context.params).channel); }
