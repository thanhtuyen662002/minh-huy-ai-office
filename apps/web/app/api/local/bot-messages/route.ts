import { botMessagesBff } from "../../../../lib/bot-messages-bff";
export async function GET(request: Request) { return botMessagesBff(request); }
